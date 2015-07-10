﻿/*
 * Copyright 2015 Stanislav Muhametsin. All rights Reserved.
 *
 * Licensed  under the  Apache License,  Version 2.0  (the "License");
 * you may not use  this file  except in  compliance with the License.
 * You may obtain a copy of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed  under the  License is distributed on an "AS IS" BASIS,
 * WITHOUT  WARRANTIES OR CONDITIONS  OF ANY KIND, either  express  or
 * implied.
 *
 * See the License for the specific language governing permissions and
 * limitations under the License. 
 */
using CILAssemblyManipulator.Logical;
using CILAssemblyManipulator.Physical;
using CILAssemblyManipulator.Tests.Logical;
using CommonUtils;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace CILAssemblyManipulator.Tests.Logical
{
   public class LogicalPhysicalInteropTest : AbstractCAMTest
   {

      [Test]
      public void TestPhysicalInteropWithWrapperAssemblies()
      {
         PerformRoundtripTest( CAMPhysicalLocation );
      }

      private static void PerformRoundtripTest( String mdLocation )
      {
         CILMetaData md;
         using ( var fs = File.OpenRead( mdLocation ) )
         {
            md = fs.ReadModule();
         }
         PerformTest( ctx =>
         {
            var mdLoader = new CILMetaDataLoaderNotThreadSafeForFiles();
            var loader = new CILAssemblyLoaderNotThreadSafe( ctx, mdLoader );
            var logical = loader.LoadAssemblyFrom( mdLocation );
            var physicalLoaded = mdLoader.GetOrLoadMetaData( mdLocation );
            var physicalCreated = logical.MainModule.CreatePhysicalRepresentation();

            var structure1 = new AssemblyStructureInfo( physicalLoaded );
            var structure2 = new AssemblyStructureInfo( physicalLoaded );

            Assert.IsTrue( structure1.Equals( structure2 ) );
            Console.WriteLine( "Hmz" );
         } );
      }

      private static void PerformTest( Action<CILReflectionContext> test )
      {
         using ( var ctx = DotNETReflectionContext.CreateDotNETContext() )
         {
            test( ctx );
         }
      }
   }

   public abstract class StructuralElementWithCustomAttributes
   {
      private readonly List<CustomAttributeStructure> _customAttributes;

      internal StructuralElementWithCustomAttributes()
      {
         this._customAttributes = new List<CustomAttributeStructure>();
      }

      public abstract override Boolean Equals( Object obj );
      public abstract override Int32 GetHashCode();

      public List<CustomAttributeStructure> CustomAttributes
      {
         get
         {
            return this._customAttributes;
         }
      }

      protected Boolean CheckCAArgs( StructuralElementWithCustomAttributes other )
      {
         return ListEqualityComparer<List<CustomAttributeStructure>, CustomAttributeStructure>.DefaultListEqualityComparer.Equals( this.CustomAttributes, other.CustomAttributes );
      }
   }

   public sealed class CustomAttributeStructure : IEquatable<CustomAttributeStructure>
   {
      public MethodDefOrRefStructure Constructor { get; set; }
      public AbstractCustomAttributeSignature Signature { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as CustomAttributeStructure );
      }

      public override Int32 GetHashCode()
      {
         return this.Constructor.GetHashCodeSafe();
      }

      public Boolean Equals( CustomAttributeStructure other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && Equals( this.Constructor, other.Constructor )
            && Comparers.AbstractCustomAttributeSignatureEqualityComparer.Equals( this.Signature, other.Signature )
            );
      }
   }

   public sealed class AssemblyStructureInfo : StructuralElementWithCustomAttributes, IEquatable<AssemblyStructureInfo>
   {
      private readonly List<ModuleStructureInfo> _modules;

      public AssemblyStructureInfo()
      {
         this._modules = new List<ModuleStructureInfo>();
      }

      public AssemblyStructureInfo( CILMetaData md )
         : this()
      {
         var aDef = md.AssemblyDefinitions.TableContents[0];
         var aInfo = new AssemblyInformation();
         aDef.AssemblyInformation.DeepCopyContentsTo( aInfo );
         this.AssemblyInfo = aInfo;
         this.Attributes = aDef.Attributes;
         this.HashAlgorithm = aDef.HashAlgorithm;
         this.Modules.Add( new ModuleStructureInfo( this, md ) );
      }

      public AssemblyInformation AssemblyInfo { get; set; }
      public AssemblyFlags Attributes { get; set; }
      public AssemblyHashAlgorithm HashAlgorithm { get; set; }
      public SecurityStructuralInfo SecurityInfo { get; set; }
      public List<ModuleStructureInfo> Modules
      {
         get
         {
            return this._modules;
         }
      }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as AssemblyStructureInfo );
      }

      public override Int32 GetHashCode()
      {
         return this.AssemblyInfo.GetHashCodeSafe();
      }

      public Boolean Equals( AssemblyStructureInfo other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && this.AssemblyInfo.EqualsTypedEquatable( other.AssemblyInfo )
            && this.Attributes == other.Attributes
            && this.HashAlgorithm == other.HashAlgorithm
            && this.SecurityInfo.EqualsTypedEquatable( other.SecurityInfo )
            && new HashSet<ModuleStructureInfo>( this.Modules ).SetEquals( other.Modules )
            && this.CheckCAArgs( other )
            );
      }
   }

   public sealed class ModuleStructureInfo : StructuralElementWithCustomAttributes, IEquatable<ModuleStructureInfo>
   {
      private readonly List<TypeDefDescription> _typeDefDescriptions;
      private readonly List<ExportedTypeStructureInfo> _exportedTypes;

      private readonly List<TypeRefDescription> _typeRefDescriptions;
      private readonly List<TypeSpecDescription> _typeSpecDescriptions;

      internal ModuleStructureInfo( AssemblyStructureInfo assembly, CILMetaData md )
      {
         this.Name = md.ModuleDefinitions.TableContents[0].Name;

         // TypeDefs
         var nestedTypes = new Dictionary<Int32, ISet<Int32>>();
         var enclosingTypes = new Dictionary<Int32, Int32>();
         foreach ( var nc in md.NestedClassDefinitions.TableContents )
         {
            nestedTypes
               .GetOrAdd_NotThreadSafe( nc.EnclosingClass.Index, i => new HashSet<Int32>() )
               .Add( nc.NestedClass.Index );
            enclosingTypes[nc.NestedClass.Index] = nc.EnclosingClass.Index;
         }

         var tDefs = md.TypeDefinitions.TableContents;
         var tlTypes = new HashSet<Int32>( Enumerable.Range( 0, tDefs.Count ) );
         tlTypes.ExceptWith( nestedTypes.Values.SelectMany( v => v ) );
         this._typeDefDescriptions = tDefs
            .Select( tDef => new TypeDefDescription( tDef ) )
            .ToList();
         foreach ( var kvp in nestedTypes )
         {
            this._typeDefDescriptions[kvp.Key].NestedTypes.AddRange( kvp.Value.Select( i => this._typeDefDescriptions[i] ) );
         }
         var typeDefInfosTopLevel = tlTypes.ToDictionary( t => Miscellaneous.CombineTypeAndNamespace( tDefs[t].Name, tDefs[t].Namespace ), t => this._typeDefDescriptions[t] );

         // TypeRef
         var modRefList = md.ModuleReferences.TableContents.Select( mRef => new ModuleRefStructureInfo( mRef ) ).ToList();
         var aRefList = md.AssemblyReferences.TableContents.Select( aRef => new AssemblyRefStructureInfo( aRef ) ).ToList();
         var fRefList = md.FileReferences.TableContents.Select( fRef => new FileReferenceStructureInfo( fRef ) ).ToList();
         var eTypes = md.ExportedTypes.TableContents;
         var eTypeList = eTypes.Select( eType => new ExportedTypeStructureInfo( eType ) ).ToList();
         this._exportedTypes = eTypeList;
         var exportedTopLevelTypes = new Dictionary<String, ExportedTypeStructureInfo>();
         for ( var i = 0; i < eTypes.Count; ++i )
         {
            var resScope = eTypes[i].Implementation;
            ExportedTypeResolutionScope resScopeInfo;
            var isTopLevel = true;
            switch ( resScope.Table )
            {
               case Tables.File:
                  resScopeInfo = new ExportedTypeResolutionScopeFile()
                  {
                     File = fRefList[resScope.Index]
                  };
                  break;
               case Tables.AssemblyRef:
                  resScopeInfo = new ExportedTypeResolutionScopeAssemblyRef()
                  {
                     AssemblyRef = aRefList[resScope.Index]
                  };
                  break;
               case Tables.ExportedType:
                  resScopeInfo = new ExportedTypeResolutionScopeNested()
                  {
                     EnclosingType = eTypeList[resScope.Index]
                  };
                  isTopLevel = false;
                  break;
               default:
                  resScopeInfo = null;
                  break;
            }
            var eType = eTypeList[i];
            if ( resScopeInfo != null )
            {
               eType.ResolutionScope = resScopeInfo;
            }

            if ( isTopLevel )
            {
               exportedTopLevelTypes.Add( Miscellaneous.CombineTypeAndNamespace( eType.Name, eType.Namespace ), eType );
            }

         }
         var tRefs = md.TypeReferences.TableContents;
         this._typeRefDescriptions = tRefs
            .Select( tRef => new TypeRefDescription( tRef ) )
            .ToList();
         for ( var i = 0; i < tRefs.Count; ++i )
         {
            var tRef = tRefs[i];
            var resScopeNullable = tRef.ResolutionScope;
            var info = this._typeRefDescriptions[i];
            TypeRefResolutionScope resScopeInfo;
            ExportedTypeStructureInfo eType;
            if ( resScopeNullable.HasValue )
            {
               var resScope = resScopeNullable.Value;
               switch ( resScope.Table )
               {
                  case Tables.TypeRef:
                     resScopeInfo = new TypeRefResolutionScopeNested()
                     {
                        EnclosingTypeRef = this._typeRefDescriptions[resScope.Index] as TypeRefDescription
                     };
                     break;
                  case Tables.Module:
                     resScopeInfo = new TypeRefResolutionScopeTypeDef()
                     {
                        TypeDef = typeDefInfosTopLevel[Miscellaneous.CombineTypeAndNamespace( tRef.Name, tRef.Namespace )]
                     };
                     break;
                  case Tables.ModuleRef:
                     resScopeInfo = new TypeRefResolutionScopeModuleRef()
                     {
                        ModuleRef = modRefList[resScope.Index]
                     };
                     break;
                  case Tables.AssemblyRef:
                     var aRef = md.AssemblyReferences.TableContents[resScope.Index];
                     resScopeInfo = new TypeRefResolutionScopeAssemblyRef()
                     {
                        AssemblyRef = aRefList[resScope.Index]
                     };
                     break;
                  default:
                     resScopeInfo = null;
                     break;
               }
            }
            else if ( exportedTopLevelTypes.TryGetValue( Miscellaneous.CombineTypeAndNamespace( tRef.Name, tRef.Namespace ), out eType ) )
            {
               resScopeInfo = new TypeRefResolutionScopeExportedType()
               {
                  ExportedType = eType
               };
            }
            else
            {
               resScopeInfo = null;
            }

            if ( resScopeInfo != null )
            {
               info.ResolutionScope = resScopeInfo;
            }

         }

         // TypeSpecs
         var tSpecs = md.TypeSpecifications.TableContents;
         this._typeSpecDescriptions = tSpecs
            .Select( tSpec => new TypeSpecDescription() )
            .ToList();
         for ( var i = 0; i < tSpecs.Count; ++i )
         {
            var tSpec = tSpecs[i];
            this._typeSpecDescriptions[i].Signature = new SignatureStructuralInfo( this, tSpec.Signature );
         }

         this.PopulateStructure( assembly, md, this._typeRefDescriptions, this._typeSpecDescriptions, fRefList, modRefList, aRefList );
      }

      public String Name { get; set; }
      public List<TypeDefDescription> TypeDefinitions
      {
         get
         {
            return this._typeDefDescriptions;
         }
      }
      public List<ExportedTypeStructureInfo> ExportedTypes
      {
         get
         {
            return this._exportedTypes;
         }
      }


      private void PopulateStructure(
         AssemblyStructureInfo assembly,
         CILMetaData md,
         IList<TypeRefDescription> typeRefList,
         IList<TypeSpecDescription> typeSpecList,
         IList<FileReferenceStructureInfo> fileRefList,
         IList<ModuleRefStructureInfo> modRefList,
         IList<AssemblyRefStructureInfo> aRefList
         )
      {
         var tDefList = this._typeDefDescriptions;

         // Fields
         var fDefs = md.FieldDefinitions.TableContents;
         var fDefList = new List<FieldStructureInfo>( fDefs.Count );
         fDefList.AddRange( fDefs.Select( fDef => new FieldStructureInfo( fDef ) ) );

         // Methods
         var mDefs = md.MethodDefinitions.TableContents;
         var mDefList = new List<MethodStructureInfo>( mDefs.Count );
         mDefList.AddRange( mDefs.Select( mDef => new MethodStructureInfo( mDef ) ) );

         // Parameters
         var paramDefs = md.ParameterDefinitions.TableContents;
         var paramDefList = new List<ParameterStructureInfo>( paramDefs.Count );
         paramDefList.AddRange( paramDefs.Select( paramDef => new ParameterStructureInfo( paramDef ) ) );

         // Properties
         var propDefs = md.PropertyDefinitions.TableContents;
         var propDefList = new List<PropertyStructuralInfo>( propDefs.Count );
         propDefList.AddRange( propDefs.Select( propDef =>
         {
            var prop = new PropertyStructuralInfo( propDef );
            prop.Signature = new SignatureStructuralInfo( this, propDef.Signature );
            return prop;
         } ) );

         // Events
         var evtDefs = md.EventDefinitions.TableContents;
         var evtDefList = new List<EventStructuralInfo>( evtDefs.Count );
         evtDefList.AddRange( evtDefs.Select( evtDef =>
         {
            var evt = new EventStructuralInfo( evtDef );
            evt.EventType = this.FromTypeDefOrRefOrSpec( evtDef.EventType );
            return evt;
         } ) );

         // Process types
         var tDefs = md.TypeDefinitions.TableContents;
         for ( var i = 0; i < tDefs.Count; ++i )
         {
            var tDef = tDefs[i];
            var tDefDesc = tDefList[i];

            // Base type
            if ( tDef.BaseType.HasValue )
            {
               tDefDesc.BaseType = this.FromTypeDefOrRefOrSpec( tDef.BaseType.Value );
            }

            tDefDesc.Fields.AddRange( md.GetTypeFieldIndices( i ).Select( fIdx => fDefList[fIdx] ) );

            // Method parameter types and custom modifiers
            tDefDesc.Methods.AddRange( md.GetTypeMethodIndices( i ).Select( mIdx => mDefList[mIdx] ) );
         }

         for ( var i = 0; i < fDefs.Count; ++i )
         {
            fDefList[i].Signature = new SignatureStructuralInfo( this, fDefs[i].Signature );
         }

         for ( var i = 0; i < mDefs.Count; ++i )
         {
            var method = mDefList[i];
            method.Signature = new SignatureStructuralInfo( this, mDefs[i].Signature );
            method.Parameters.AddRange( md.GetMethodParameterIndices( i ).Select( pIdx => paramDefList[pIdx] ) );
         }

         // Properties
         var pMaps = md.PropertyMaps.TableContents;
         for ( var i = 0; i < pMaps.Count; ++i )
         {
            tDefList[pMaps[i].Parent.Index].Properties.AddRange( md.GetTypePropertyIndices( i ).Select( propIdx => propDefList[propIdx] ) );
         }

         // Events
         var eMaps = md.EventMaps.TableContents;
         for ( var i = 0; i < eMaps.Count; ++i )
         {
            tDefList[eMaps[i].Parent.Index].Events.AddRange( md.GetTypeEventIndices( i ).Select( evtIdx => evtDefList[evtIdx] ) );
         }

         // Method Semantics
         foreach ( var semantic in md.MethodSemantics.TableContents )
         {
            StructuralInfoWithSemanticsMethods semInfo;
            var asso = semantic.Associaton;
            switch ( asso.Table )
            {
               case Tables.Property:
                  semInfo = propDefList[asso.Index];
                  break;
               case Tables.Event:
                  semInfo = evtDefList[asso.Index];
                  break;
               default:
                  semInfo = null;
                  break;
            }

            if ( semInfo != null )
            {
               semInfo.SemanticMethods.Add( Tuple.Create( semantic.Attributes, mDefList[semantic.Method.Index] ) );
            }
         }

         // Generic Parameters
         var gArgs = md.GenericParameterDefinitions.TableContents;
         var gArgsList = new List<GenericParameterStructuralInfo>( gArgs.Count );
         gArgsList.AddRange( gArgs.Select( gArg =>
         {
            var gArgInfo = new GenericParameterStructuralInfo( gArg );
            List<GenericParameterStructuralInfo> thisArgs;
            var owner = gArg.Owner;
            switch ( owner.Table )
            {
               case Tables.TypeDef:
                  thisArgs = tDefList[owner.Index].GenericParameters;
                  break;
               case Tables.MethodDef:
                  thisArgs = mDefList[owner.Index].GenericParameters;
                  break;
               default:
                  thisArgs = null;
                  break;
            }
            if ( thisArgs != null )
            {
               thisArgs.Add( gArgInfo );
            }
            return gArgInfo;
         } ) );

         // Generic parameter constraints
         var gArgConstraints = md.GenericParameterConstraintDefinitions.TableContents;
         var gArgConstraintList = new List<GenericParameterConstraintStructuralInfo>( gArgConstraints.Count );
         gArgConstraintList.AddRange( gArgConstraints.Select( gConstraint =>
         {
            var gConstraintInfo = new GenericParameterConstraintStructuralInfo()
            {
               Constraint = this.FromTypeDefOrRefOrSpec( gConstraint.Constraint )
            };
            gArgsList[gConstraint.Owner.Index].Constraints.Add( gConstraintInfo );
            return gConstraintInfo;
         } ) );

         // Class layout
         foreach ( var layout in md.ClassLayouts.TableContents )
         {
            tDefList[layout.Parent.Index].Layout = new LayoutInfo()
            {
               ClassSize = layout.ClassSize,
               PackingSize = layout.PackingSize
            };
         }

         // Constants
         foreach ( var constant in md.ConstantDefinitions.TableContents )
         {
            var parent = constant.Parent;
            var value = constant.Value;
            switch ( parent.Table )
            {
               case Tables.Field:
                  fDefList[parent.Index].ConstantValue = value;
                  break;
               case Tables.Parameter:
                  paramDefList[parent.Index].ConstantValue = value;
                  break;
               case Tables.Property:
                  propDefList[parent.Index].ConstantValue = value;
                  break;
            }
         }

         // Field layouts 
         foreach ( var layout in md.FieldLayouts.TableContents )
         {
            fDefList[layout.Field.Index].FieldOffset = layout.Offset;
         }

         // Field marshals
         foreach ( var marshal in md.FieldMarshals.TableContents )
         {
            var parent = marshal.Parent;
            switch ( parent.Table )
            {
               case Tables.Field:
                  fDefList[parent.Index].MarshalingInfo = marshal.NativeType;
                  break;
               case Tables.Parameter:
                  paramDefList[parent.Index].MarshalingInfo = marshal.NativeType;
                  break;
            }
         }

         // Field RVAs
         foreach ( var rva in md.FieldRVAs.TableContents )
         {
            fDefList[rva.Field.Index].FieldData = rva.Data;
         }
         // Impl maps
         foreach ( var impl in md.MethodImplementationMaps.TableContents )
         {
            var parent = impl.MemberForwarded;
            var invokeInfo = new PInvokeInfo()
            {
               Attributes = impl.Attributes,
               PlatformInvokeName = impl.ImportName,
               PlatformInvokeModuleName = md.ModuleReferences.TableContents[impl.ImportScope.Index].ModuleName
            };

            switch ( parent.Table )
            {
               case Tables.MethodDef:
                  mDefList[parent.Index].PInvokeInfo = invokeInfo;
                  break;
               case Tables.Field:
                  fDefList[parent.Index].PInvokeInfo = invokeInfo;
                  break;
            }
         }

         // Interface Impls
         var interfaces = md.InterfaceImplementations.TableContents;
         var interfaceList = new List<InterfaceImplStructuralInfo>( interfaces.Count );
         interfaceList.AddRange( interfaces.Select( iFace =>
         {
            var iFaceInfo = new InterfaceImplStructuralInfo()
            {
               InterfaceType = this.FromTypeDefOrRefOrSpec( iFace.Interface )
            };
            tDefList[iFace.Class.Index].ImplementedInterfaces.Add( iFaceInfo );
            return iFaceInfo;
         } ) );

         // DeclSecurity
         var security = md.SecurityDefinitions.TableContents;
         var securityList = new List<SecurityStructuralInfo>( security.Count );
         securityList.AddRange( security.Select( sec =>
         {
            var secInfo = new SecurityStructuralInfo( sec );
            var parent = sec.Parent;
            switch ( parent.Table )
            {
               case Tables.TypeDef:
                  tDefList[parent.Index].SecurityInfo = secInfo;
                  break;
               case Tables.MethodDef:
                  mDefList[parent.Index].SecurityInfo = secInfo;
                  break;
               case Tables.Assembly:
                  assembly.SecurityInfo = secInfo;
                  break;
            }
            return secInfo;
         } ) );

         // ManifestResource
         var resources = md.ManifestResources.TableContents;
         var resourceList = new List<ManifestResourceStructuralInfo>( resources.Count );
         resourceList.AddRange( resources.Select( res =>
         {
            var resourceInfo = new ManifestResourceStructuralInfo( res );
            var implNullable = res.Implementation;
            ManifestResourceData data;
            if ( implNullable.HasValue )
            {
               var impl = implNullable.Value;
               switch ( impl.Table )
               {
                  case Tables.File:
                     data = new ManifestResourceDataFile()
                     {
                        FileReference = fileRefList[impl.Index]
                     };
                     break;
                  case Tables.AssemblyRef:
                     data = new ManifestResourceDataAssemblyRef()
                     {
                        AssemblyRef = aRefList[impl.Index]
                     };
                     break;
                  default:
                     data = null;
                     break;
               }
            }
            else
            {
               data = new ManifestResourceDataEmbedded()
               {
                  Data = res.DataInCurrentFile.CreateBlockCopy()
               };
            }
            if ( data != null )
            {
               resourceInfo.ManifestData = data;
            }

            return resourceInfo;
         } ) );

         // MemberRefs
         var memberRefs = md.MemberReferences.TableContents;
         var memberRefList = new List<MemberReferenceStructuralInfo>( memberRefs.Count );
         memberRefList.AddRange( memberRefs.Select( mRef =>
         {
            var mRefInfo = new MemberReferenceStructuralInfo( mRef );
            mRefInfo.Signature = new SignatureStructuralInfo( this, mRef.Signature );
            var parent = mRef.DeclaringType;
            MemberReferenceParent parentInfo;
            switch ( parent.Table )
            {
               case Tables.MethodDef:
                  parentInfo = new MemberReferenceParentMethodDef()
                  {
                     Method = mDefList[parent.Index]
                  };
                  break;
               case Tables.ModuleRef:
                  parentInfo = new MemberReferenceParentModuleRef()
                  {
                     ModuleRef = modRefList[parent.Index]
                  };
                  break;
               case Tables.TypeDef:
               case Tables.TypeRef:
               case Tables.TypeSpec:
                  parentInfo = new MemberReferenceParentType()
                  {
                     Type = this.FromTypeDefOrRefOrSpec( parent )
                  };
                  break;
               default:
                  parentInfo = null;
                  break;
            }

            if ( parentInfo != null )
            {
               mRefInfo.Parent = parentInfo;
            }
            return mRefInfo;
         } ) );
         // MethodImpl
         foreach ( var impl in md.MethodImplementations.TableContents )
         {
            tDefList[impl.Class.Index].OverriddenMethods.Add( new OverriddenMethodInfo( FromMethodDefOrMemberRef( mDefList, memberRefList, impl.MethodBody ), FromMethodDefOrMemberRef( mDefList, memberRefList, impl.MethodDeclaration ) ) );
         }
         // StandaloneSig
         var standaloneSigs = md.StandaloneSignatures.TableContents;
         var standaloneSigList = new List<StandaloneSignatureStructure>( standaloneSigs.Count );
         standaloneSigList.AddRange( standaloneSigs.Select( sig => new StandaloneSignatureStructure()
         {
            Signature = new SignatureStructuralInfo( this, sig.Signature )
         } ) );
         // MethodSpec
         var methodSpecs = md.MethodSpecifications.TableContents;
         var methodSpecList = new List<MethodSpecificationStructure>( methodSpecs.Count );
         methodSpecList.AddRange( methodSpecs.Select( mSpec => new MethodSpecificationStructure()
         {
            Signature = new SignatureStructuralInfo( this, mSpec.Signature ),
            Method = FromMethodDefOrMemberRef( mDefList, memberRefList, mSpec.Method )
         } ) );
         // Custom Attributes
         foreach ( var ca in md.CustomAttributeDefinitions.TableContents )
         {
            var parent = ca.Parent;
            StructuralElementWithCustomAttributes parentInfo;
            switch ( parent.Table )
            {
               case Tables.TypeDef:
                  parentInfo = this._typeDefDescriptions[parent.Index];
                  break;
               case Tables.ExportedType:
                  parentInfo = this._exportedTypes[parent.Index];
                  break;
               case Tables.MethodDef:
                  parentInfo = mDefList[parent.Index];
                  break;
               case Tables.Field:
                  parentInfo = fDefList[parent.Index];
                  break;
               case Tables.Parameter:
                  parentInfo = paramDefList[parent.Index];
                  break;
               case Tables.Module:
                  parentInfo = this;
                  break;
               case Tables.Property:
                  parentInfo = propDefList[parent.Index];
                  break;
               case Tables.Event:
                  parentInfo = evtDefList[parent.Index];
                  break;
               case Tables.Assembly:
                  parentInfo = assembly;
                  break;
               case Tables.GenericParameter:
                  parentInfo = gArgsList[parent.Index];
                  break;
               case Tables.TypeRef:
                  parentInfo = typeRefList[parent.Index];
                  break;
               case Tables.InterfaceImpl:
                  parentInfo = interfaceList[parent.Index];
                  break;
               case Tables.MemberRef:
                  parentInfo = memberRefList[parent.Index];
                  break;
               case Tables.DeclSecurity:
                  parentInfo = securityList[parent.Index];
                  break;
               case Tables.StandaloneSignature:
                  parentInfo = standaloneSigList[parent.Index];
                  break;
               case Tables.ModuleRef:
                  parentInfo = modRefList[parent.Index];
                  break;
               case Tables.TypeSpec:
                  parentInfo = typeSpecList[parent.Index];
                  break;
               case Tables.AssemblyRef:
                  parentInfo = aRefList[parent.Index];
                  break;
               case Tables.File:
                  parentInfo = fileRefList[parent.Index];
                  break;
               case Tables.ManifestResource:
                  parentInfo = resourceList[parent.Index];
                  break;
               case Tables.GenericParameterConstraint:
                  parentInfo = gArgConstraintList[parent.Index];
                  break;
               case Tables.MethodSpec:
                  parentInfo = methodSpecList[parent.Index];
                  break;
               default:
                  parentInfo = null;
                  break;
            }
            parentInfo.CustomAttributes.Add( new CustomAttributeStructure()
            {
               Constructor = FromMethodDefOrMemberRef( mDefList, memberRefList, ca.Type ),
               Signature = ca.Signature // TODO clone
            } );
         }
         // IL
      }

      public AbstractTypeDescription GetTypeDefDescription( Int32 idx )
      {
         return this._typeDefDescriptions[idx];
      }

      public AbstractTypeDescription GetTypeRefDescription( Int32 idx )
      {
         return this._typeRefDescriptions[idx];
      }

      public AbstractTypeDescription GetTypeSpecDescription( Int32 idx )
      {
         return this._typeSpecDescriptions[idx];
      }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as ModuleStructureInfo );
      }

      public override Int32 GetHashCode()
      {
         return this.Name.GetHashCodeSafe();
      }

      public Boolean Equals( ModuleStructureInfo other )
      {
         var retVal = ReferenceEquals( this, other )
            || ( other != null
            && new HashSet<TypeDefDescription>( this.TypeDefinitions ).SetEquals( other.TypeDefinitions )
            && new HashSet<ExportedTypeStructureInfo>( this.ExportedTypes ).SetEquals( other.ExportedTypes )
            && this.CheckCAArgs( other )
            );
         if ( !retVal )
         {

         }

         return retVal;
      }

      private static MethodDefOrRefStructure FromMethodDefOrMemberRef( List<MethodStructureInfo> mDefList, List<MemberReferenceStructuralInfo> mRefList, TableIndex index )
      {
         switch ( index.Table )
         {
            case Tables.MethodDef:
               return mDefList[index.Index];
            case Tables.MemberRef:
               return mRefList[index.Index];
            default:
               return null;
         }
      }
   }

   public enum TypeDescriptionKind
   {
      TypeDef,
      TypeRef,
      TypeSpec
   }

   public abstract class AbstractTypeDescription : StructuralElementWithCustomAttributes
   {
      internal AbstractTypeDescription()
      {

      }

      public abstract TypeDescriptionKind TypeDescriptionKind { get; }

      public abstract override Boolean Equals( Object obj );
      public abstract override Int32 GetHashCode();

      // For TypeDefs: create type string & compare (need ModuleStructureInfo)
      // For TypeRefs: create type string & compare (don't need anything)
      // For TypeSpecs: equal signatures.
      public abstract Boolean EqualsTextual( AbstractTypeDescription other );
   }

   public sealed class LayoutInfo : IEquatable<LayoutInfo>
   {
      public Int32 ClassSize { get; set; }
      public Int32 PackingSize { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as LayoutInfo );
      }

      public override Int32 GetHashCode()
      {
         return ( 17 * 23 + this.ClassSize ) * 23 + this.PackingSize;
      }

      public Boolean Equals( LayoutInfo other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && this.ClassSize == other.ClassSize
            && this.PackingSize == other.PackingSize
            );
      }
   }

   public struct OverriddenMethodInfo : IEquatable<OverriddenMethodInfo>
   {
      private readonly MethodDefOrRefStructure _methodBody;
      private readonly MethodDefOrRefStructure _methodDeclaration;

      public OverriddenMethodInfo( MethodDefOrRefStructure methodBody, MethodDefOrRefStructure methodDeclaration )
      {
         this._methodBody = methodBody;
         this._methodDeclaration = methodDeclaration;
      }

      public MethodDefOrRefStructure MethodBody
      {
         get
         {
            return this._methodBody;
         }
      }

      public MethodDefOrRefStructure MethodDeclaration
      {
         get
         {
            return this._methodDeclaration;
         }
      }

      public override Boolean Equals( Object obj )
      {
         return obj is OverriddenMethodInfo && this.Equals( (OverriddenMethodInfo) obj );
      }

      public override Int32 GetHashCode()
      {
         return this.MethodBody.GetHashCodeSafe();
      }

      public Boolean Equals( OverriddenMethodInfo other )
      {
         return Equals( this.MethodBody, other.MethodBody )
            && Equals( this.MethodDeclaration, other.MethodDeclaration );
      }
   }

   public sealed class TypeDefDescription : AbstractTypeDescription, IEquatable<TypeDefDescription>
   {
      private readonly List<TypeDefDescription> _nestedTypes;
      private readonly List<FieldStructureInfo> _fields;
      private readonly List<MethodStructureInfo> _methods;
      private readonly List<PropertyStructuralInfo> _properties;
      private readonly List<EventStructuralInfo> _events;

      private readonly List<InterfaceImplStructuralInfo> _interfaces;
      private readonly List<GenericParameterStructuralInfo> _genericParameters;
      private readonly List<OverriddenMethodInfo> _overriddenMethods;

      internal TypeDefDescription()
      {
         this._nestedTypes = new List<TypeDefDescription>();
         this._fields = new List<FieldStructureInfo>();
         this._methods = new List<MethodStructureInfo>();
         this._properties = new List<PropertyStructuralInfo>();
         this._events = new List<EventStructuralInfo>();
         this._interfaces = new List<InterfaceImplStructuralInfo>();
         this._genericParameters = new List<GenericParameterStructuralInfo>();
         this._overriddenMethods = new List<OverriddenMethodInfo>();
      }

      internal TypeDefDescription( TypeDefinition tDef )
         : this()
      {
         this.Namespace = tDef.Namespace;
         this.Name = tDef.Name;
         this.Attributes = tDef.Attributes;
      }

      public override TypeDescriptionKind TypeDescriptionKind
      {
         get
         {
            return TypeDescriptionKind.TypeDef;
         }
      }

      public String Name { get; set; }
      public String Namespace { get; set; }
      public TypeAttributes Attributes { get; set; }
      public AbstractTypeDescription BaseType { get; set; }
      public LayoutInfo Layout { get; set; }
      public SecurityStructuralInfo SecurityInfo { get; set; }

      public List<TypeDefDescription> NestedTypes
      {
         get
         {
            return this._nestedTypes;
         }
      }

      public List<FieldStructureInfo> Fields
      {
         get
         {
            return this._fields;
         }
      }

      public List<MethodStructureInfo> Methods
      {
         get
         {
            return this._methods;
         }
      }

      public List<PropertyStructuralInfo> Properties
      {
         get
         {
            return this._properties;
         }
      }

      public List<EventStructuralInfo> Events
      {
         get
         {
            return this._events;
         }
      }

      public List<GenericParameterStructuralInfo> GenericParameters
      {
         get
         {
            return this._genericParameters;
         }
      }

      public List<InterfaceImplStructuralInfo> ImplementedInterfaces
      {
         get
         {
            return this._interfaces;
         }
      }

      public List<OverriddenMethodInfo> OverriddenMethods
      {
         get
         {
            return this._overriddenMethods;
         }
      }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as TypeDefDescription );
      }

      public override Int32 GetHashCode()
      {
         return ( 17 * 23 + this.Name.GetHashCodeSafe() ) * 23 + this.Namespace.GetHashCodeSafe();
      }

      public Boolean Equals( TypeDefDescription other )
      {
         var retVal = ReferenceEquals( this, other )
            || ( other != null
            && String.Equals( this.Name, other.Name )
            && String.Equals( this.Namespace, other.Namespace )
            && Equals( this.BaseType, other.BaseType )
            && this.Attributes == other.Attributes
            && ListEqualityComparer<List<FieldStructureInfo>, FieldStructureInfo>.DefaultListEqualityComparer.Equals( this._fields, other._fields )
            && ListEqualityComparer<List<MethodStructureInfo>, MethodStructureInfo>.DefaultListEqualityComparer.Equals( this._methods, other._methods )
            && ListEqualityComparer<List<GenericParameterStructuralInfo>, GenericParameterStructuralInfo>.DefaultListEqualityComparer.Equals( this._genericParameters, other._genericParameters )
            && new HashSet<PropertyStructuralInfo>( this.Properties ).SetEquals( other.Properties )
            && new HashSet<EventStructuralInfo>( this.Events ).SetEquals( other.Events )
            && new HashSet<InterfaceImplStructuralInfo>( this._interfaces ).SetEquals( other._interfaces )
            && this.SecurityInfo.EqualsTypedEquatable( other.SecurityInfo )
            && ListEqualityComparer<List<OverriddenMethodInfo>, OverriddenMethodInfo>.DefaultListEqualityComparer.Equals( this.OverriddenMethods, other.OverriddenMethods )
            && this.Layout.EqualsTypedEquatable( other.Layout )
            && new HashSet<TypeDefDescription>( this._nestedTypes ).SetEquals( other._nestedTypes )
            && this.CheckCAArgs( other )
            );
         if ( !retVal )
         {
            var kek = Equals( this.BaseType, other.BaseType );
            var lol = this.CheckCAArgs( other );
            var fug = ListEqualityComparer<List<FieldStructureInfo>, FieldStructureInfo>.DefaultListEqualityComparer.Equals( this._fields, other._fields );
            var fyg = ListEqualityComparer<List<MethodStructureInfo>, MethodStructureInfo>.DefaultListEqualityComparer.Equals( this._methods, other._methods );
         }
         return retVal;
      }

      public override String ToString()
      {
         return Miscellaneous.CombineTypeAndNamespace( this.Name, this.Namespace );
      }
   }

   public sealed class TypeRefDescription : AbstractTypeDescription, IEquatable<TypeRefDescription>
   {
      public TypeRefDescription()
      {

      }

      public TypeRefDescription( TypeReference tRef )
         : this()
      {
         this.Name = tRef.Name;
         this.Namespace = tRef.Namespace;
      }

      public String Name { get; set; }
      public String Namespace { get; set; }
      public TypeRefResolutionScope ResolutionScope { get; set; }

      public override TypeDescriptionKind TypeDescriptionKind
      {
         get
         {
            return TypeDescriptionKind.TypeRef;
         }
      }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as TypeRefDescription );
      }

      public override Int32 GetHashCode()
      {
         return ( ( 17 * 23 + this.Name.GetHashCodeSafe() ) * 23 + this.Namespace.GetHashCodeSafe() ) * 23 + this.ResolutionScope.GetHashCodeSafe();
      }

      public Boolean Equals( TypeRefDescription other )
      {
         return Object.ReferenceEquals( this, other )
            || ( other != null
            && String.Equals( this.Name, other.Name )
            && String.Equals( this.Namespace, other.Namespace )
            && Equals( this.ResolutionScope, other.ResolutionScope )
            && this.CheckCAArgs( other )
            );
      }

      public override String ToString()
      {
         return Miscellaneous.CombineTypeAndNamespace( this.Name, this.Namespace );
      }
   }

   public enum TypeRefResolutionScopeKind
   {
      Nested,
      ModuleRef,
      AssemblyRef,
      TypeDef,
      ExportedType
   }

   public abstract class TypeRefResolutionScope
   {
      internal TypeRefResolutionScope()
      {

      }

      public abstract TypeRefResolutionScopeKind ResolutionScopeKind { get; }

      public abstract override Boolean Equals( Object obj );
      public abstract override Int32 GetHashCode();
   }

   public sealed class TypeRefResolutionScopeTypeDef : TypeRefResolutionScope, IEquatable<TypeRefResolutionScopeTypeDef>
   {
      internal TypeRefResolutionScopeTypeDef()
      {

      }

      public TypeDefDescription TypeDef { get; set; }

      public override TypeRefResolutionScopeKind ResolutionScopeKind
      {
         get
         {
            return TypeRefResolutionScopeKind.TypeDef;
         }
      }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as TypeRefResolutionScopeTypeDef );
      }

      public override Int32 GetHashCode()
      {
         return this.TypeDef.GetHashCodeSafe();
      }

      public Boolean Equals( TypeRefResolutionScopeTypeDef other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && this.TypeDef.EqualsTypedEquatable( other.TypeDef )
            );
      }
   }

   public sealed class TypeRefResolutionScopeNested : TypeRefResolutionScope, IEquatable<TypeRefResolutionScopeNested>
   {
      internal TypeRefResolutionScopeNested()
      {

      }

      public override TypeRefResolutionScopeKind ResolutionScopeKind
      {
         get
         {
            return TypeRefResolutionScopeKind.Nested;
         }
      }

      public TypeRefDescription EnclosingTypeRef { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as TypeRefResolutionScopeNested );
      }

      public override Int32 GetHashCode()
      {
         return 17 * 23 + this.EnclosingTypeRef.GetHashCodeSafe();
      }

      public Boolean Equals( TypeRefResolutionScopeNested other )
      {
         return ReferenceEquals( this, other ) ||
            ( other != null
             && this.EnclosingTypeRef.EqualsTypedEquatable( other.EnclosingTypeRef )
            );
      }
   }

   public sealed class TypeRefResolutionScopeModuleRef : TypeRefResolutionScope, IEquatable<TypeRefResolutionScopeModuleRef>
   {
      internal TypeRefResolutionScopeModuleRef()
      {

      }

      public override TypeRefResolutionScopeKind ResolutionScopeKind
      {
         get
         {
            return TypeRefResolutionScopeKind.ModuleRef;
         }
      }

      public ModuleRefStructureInfo ModuleRef { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as TypeRefResolutionScopeModuleRef );
      }

      public override Int32 GetHashCode()
      {
         return 17 * 23 + this.ModuleRef.GetHashCodeSafe();
      }

      public Boolean Equals( TypeRefResolutionScopeModuleRef other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && this.ModuleRef.EqualsTypedEquatable( other.ModuleRef )
            );
      }
   }

   public sealed class TypeRefResolutionScopeAssemblyRef : TypeRefResolutionScope, IEquatable<TypeRefResolutionScopeAssemblyRef>
   {
      internal TypeRefResolutionScopeAssemblyRef()
      {

      }

      public override TypeRefResolutionScopeKind ResolutionScopeKind
      {
         get
         {
            return TypeRefResolutionScopeKind.AssemblyRef;
         }
      }

      public AssemblyRefStructureInfo AssemblyRef { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as TypeRefResolutionScopeAssemblyRef );
      }

      public override Int32 GetHashCode()
      {
         return 17 * 23 + this.AssemblyRef.GetHashCode();
      }

      public Boolean Equals( TypeRefResolutionScopeAssemblyRef other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && this.AssemblyRef.EqualsTypedEquatable( other.AssemblyRef )
            );
      }
   }

   public sealed class TypeRefResolutionScopeExportedType : TypeRefResolutionScope, IEquatable<TypeRefResolutionScopeExportedType>
   {

      public ExportedTypeStructureInfo ExportedType { get; set; }

      public override TypeRefResolutionScopeKind ResolutionScopeKind
      {
         get
         {
            return TypeRefResolutionScopeKind.ExportedType;
         }
      }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as TypeRefResolutionScopeExportedType );
      }

      public override Int32 GetHashCode()
      {
         return 17 * 23 + this.ExportedType.GetHashCode();
      }

      public Boolean Equals( TypeRefResolutionScopeExportedType other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && this.ExportedType.EqualsTypedEquatable( other.ExportedType )
            );
      }
   }

   public sealed class TypeSpecDescription : AbstractTypeDescription, IEquatable<TypeSpecDescription>
   {
      internal TypeSpecDescription()
      {
      }

      public override TypeDescriptionKind TypeDescriptionKind
      {
         get
         {
            return TypeDescriptionKind.TypeSpec;
         }
      }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as TypeSpecDescription );
      }

      public override Int32 GetHashCode()
      {
         return 17 * 23 + this.Signature.GetHashCodeSafe();
      }

      public Boolean Equals( TypeSpecDescription other )
      {
         return Object.ReferenceEquals( this, other )
            || ( other != null
            && this.Signature.EqualsTypedEquatable( other.Signature )
            && this.CheckCAArgs( other )
            );
      }

      public SignatureStructuralInfo Signature { get; set; }
   }

   public sealed class AssemblyRefStructureInfo : StructuralElementWithCustomAttributes, IEquatable<AssemblyRefStructureInfo>
   {
      internal AssemblyRefStructureInfo( AssemblyReference aRef )
      {
         this.AssemblyRef = new AssemblyInformation();
         aRef.AssemblyInformation.DeepCopyContentsTo( this.AssemblyRef );
         this.Attributes = aRef.Attributes;
         this.HashValue = aRef.HashValue.CreateBlockCopy();
      }

      public AssemblyInformation AssemblyRef { get; set; }
      public AssemblyFlags Attributes { get; set; }
      public Byte[] HashValue { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as AssemblyRefStructureInfo );
      }

      public override Int32 GetHashCode()
      {
         return this.AssemblyRef.GetHashCode();
      }

      public Boolean Equals( AssemblyRefStructureInfo other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && this.AssemblyRef.EqualsTypedEquatable( other.AssemblyRef )
            && this.Attributes == other.Attributes
            && ArrayEqualityComparer<Byte>.DefaultArrayEqualityComparer.Equals( this.HashValue, other.HashValue )
            && this.CheckCAArgs( other )
            );
      }
   }

   public sealed class ExportedTypeStructureInfo : StructuralElementWithCustomAttributes, IEquatable<ExportedTypeStructureInfo>
   {
      internal ExportedTypeStructureInfo()
      {

      }

      internal ExportedTypeStructureInfo( ExportedType eType )
      {
         this.Attributes = eType.Attributes;
         this.TypeDefID = eType.TypeDefinitionIndex;
         this.Name = eType.Name;
         this.Namespace = eType.Namespace;
      }

      public TypeAttributes Attributes { get; set; }
      public Int32 TypeDefID { get; set; }
      public String Name { get; set; }
      public String Namespace { get; set; }
      public ExportedTypeResolutionScope ResolutionScope { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as ExportedTypeStructureInfo );
      }

      public override Int32 GetHashCode()
      {
         return ( 17 * 23 + this.Name.GetHashCodeSafe() ) * 23 + this.Namespace.GetHashCodeSafe();
      }

      public Boolean Equals( ExportedTypeStructureInfo other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && String.Equals( this.Name, other.Name )
            && String.Equals( this.Namespace, other.Namespace )
            && this.Attributes == other.Attributes
            && this.TypeDefID == other.TypeDefID
            && Equals( this.ResolutionScope, other.ResolutionScope )
            );
      }
   }

   public enum ExportedTypeResolutionScopeKind
   {
      Nested,
      File,
      AssemblyRef
   }

   public abstract class ExportedTypeResolutionScope
   {
      internal ExportedTypeResolutionScope()
      {

      }

      public abstract ExportedTypeResolutionScopeKind ResolutionScopeKind { get; }

      public abstract override Boolean Equals( Object obj );
      public abstract override Int32 GetHashCode();
   }

   public sealed class ExportedTypeResolutionScopeNested : ExportedTypeResolutionScope, IEquatable<ExportedTypeResolutionScopeNested>
   {
      internal ExportedTypeResolutionScopeNested()
      {

      }

      public ExportedTypeStructureInfo EnclosingType { get; set; }

      public override ExportedTypeResolutionScopeKind ResolutionScopeKind
      {
         get
         {
            return ExportedTypeResolutionScopeKind.Nested;
         }
      }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as ExportedTypeResolutionScopeNested );
      }

      public override Int32 GetHashCode()
      {
         return 17 * 23 + this.EnclosingType.GetHashCodeSafe();
      }

      public Boolean Equals( ExportedTypeResolutionScopeNested other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && Equals( this.EnclosingType, other.EnclosingType )
            );
      }
   }

   public sealed class ExportedTypeResolutionScopeFile : ExportedTypeResolutionScope, IEquatable<ExportedTypeResolutionScopeFile>
   {
      internal ExportedTypeResolutionScopeFile()
      {

      }

      public FileReferenceStructureInfo File { get; set; }

      public override ExportedTypeResolutionScopeKind ResolutionScopeKind
      {
         get
         {
            return ExportedTypeResolutionScopeKind.File;
         }
      }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as ExportedTypeResolutionScopeFile );
      }

      public override Int32 GetHashCode()
      {
         return 17 * 23 + this.File.GetHashCodeSafe();
      }

      public Boolean Equals( ExportedTypeResolutionScopeFile other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && this.File.EqualsTypedEquatable( other.File )
            );
      }
   }

   public sealed class ExportedTypeResolutionScopeAssemblyRef : ExportedTypeResolutionScope, IEquatable<ExportedTypeResolutionScopeAssemblyRef>
   {
      internal ExportedTypeResolutionScopeAssemblyRef()
      {

      }

      public AssemblyRefStructureInfo AssemblyRef { get; set; }

      public override ExportedTypeResolutionScopeKind ResolutionScopeKind
      {
         get
         {
            return ExportedTypeResolutionScopeKind.AssemblyRef;
         }
      }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as ExportedTypeResolutionScopeAssemblyRef );
      }

      public override Int32 GetHashCode()
      {
         return 17 * 23 + this.AssemblyRef.GetHashCodeSafe();
      }

      public Boolean Equals( ExportedTypeResolutionScopeAssemblyRef other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && this.AssemblyRef.EqualsTypedEquatable( other.AssemblyRef )
            );
      }
   }

   public sealed class ModuleRefStructureInfo : StructuralElementWithCustomAttributes, IEquatable<ModuleRefStructureInfo>
   {
      internal ModuleRefStructureInfo( ModuleReference modRef )
      {
         this.ModuleName = modRef.ModuleName;
      }

      public String ModuleName { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as ModuleRefStructureInfo );
      }

      public override Int32 GetHashCode()
      {
         return this.ModuleName.GetHashCodeSafe();
      }

      public Boolean Equals( ModuleRefStructureInfo other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && String.Equals( this.ModuleName, other.ModuleName )
            && this.CheckCAArgs( other )
            );
      }
   }

   public sealed class FileReferenceStructureInfo : StructuralElementWithCustomAttributes, IEquatable<FileReferenceStructureInfo>
   {
      internal FileReferenceStructureInfo()
      {

      }

      internal FileReferenceStructureInfo( FileReference fRef )
      {
         this.Name = fRef.Name;
         this.Attributes = fRef.Attributes;
         this.HashValue = fRef.HashValue.CreateBlockCopy();
      }

      public String Name { get; set; }
      public CILAssemblyManipulator.Physical.FileAttributes Attributes { get; set; }
      public Byte[] HashValue { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as FileReferenceStructureInfo );
      }

      public override Int32 GetHashCode()
      {
         return this.Name.GetHashCodeSafe();
      }

      public Boolean Equals( FileReferenceStructureInfo other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && String.Equals( this.Name, other.Name )
            && this.Attributes == other.Attributes
            && ArrayEqualityComparer<Byte>.DefaultArrayEqualityComparer.Equals( this.HashValue, other.HashValue )
            && this.CheckCAArgs( other )
            );
      }
   }

   public sealed class FieldStructureInfo : StructuralElementWithCustomAttributes, IEquatable<FieldStructureInfo>
   {
      internal FieldStructureInfo()
      {

      }

      internal FieldStructureInfo( FieldDefinition fDef )
         : this()
      {
         this.Attributes = fDef.Attributes;
         this.Name = fDef.Name;
      }

      public String Name { get; set; }
      public FieldAttributes Attributes { get; set; }
      public SignatureStructuralInfo Signature { get; set; }
      public Object ConstantValue { get; set; }
      public MarshalingInfo MarshalingInfo { get; set; }
      public Int32 FieldOffset { get; set; }
      public Byte[] FieldData { get; set; }
      public PInvokeInfo PInvokeInfo { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as FieldStructureInfo );
      }

      public override Int32 GetHashCode()
      {
         return ( 17 * 23 + this.Name.GetHashCodeSafe() ) * 23 + this.Signature.GetHashCodeSafe();
      }

      public Boolean Equals( FieldStructureInfo other )
      {
         var retVal = ReferenceEquals( this, other )
            || ( other != null
            && this.Attributes == other.Attributes
            && String.Equals( this.Name, other.Name )
            && this.Signature.EqualsTypedEquatable( other.Signature )
            && Equals( this.ConstantValue, other.ConstantValue )
            && Comparers.MarshalingInfoEqualityComparer.Equals( this.MarshalingInfo, other.MarshalingInfo )
            && this.FieldOffset == other.FieldOffset
            && ArrayEqualityComparer<Byte>.DefaultArrayEqualityComparer.Equals( this.FieldData, other.FieldData )
            && this.PInvokeInfo.EqualsTypedEquatable( other.PInvokeInfo )
            && this.CheckCAArgs( other )
            );
         if ( !retVal )
         {

         }
         return retVal;
      }

      public override String ToString()
      {
         return this.Name;
      }
   }

   public abstract class MethodDefOrRefStructure : StructuralElementWithCustomAttributes
   {
      internal MethodDefOrRefStructure()
      {

      }
   }

   public sealed class MethodStructureInfo : MethodDefOrRefStructure, IEquatable<MethodStructureInfo>
   {
      private readonly List<ParameterStructureInfo> _parameters;
      private readonly List<GenericParameterStructuralInfo> _genericParameters;

      internal MethodStructureInfo()
      {
         this._parameters = new List<ParameterStructureInfo>();
         this._genericParameters = new List<GenericParameterStructuralInfo>();
      }

      internal MethodStructureInfo( MethodDefinition mDef )
         : this()
      {
         this.Attributes = mDef.Attributes;
         this.ImplementationAttributes = mDef.ImplementationAttributes;
         this.Name = mDef.Name;
      }

      public MethodAttributes Attributes { get; set; }
      public MethodImplAttributes ImplementationAttributes { get; set; }
      public String Name { get; set; }
      public SignatureStructuralInfo Signature { get; set; }
      public PInvokeInfo PInvokeInfo { get; set; }
      public MethodILStructureInfo IL { get; set; }
      public SecurityStructuralInfo SecurityInfo { get; set; }
      public List<ParameterStructureInfo> Parameters
      {
         get
         {
            return this._parameters;
         }
      }
      public List<GenericParameterStructuralInfo> GenericParameters
      {
         get
         {
            return this._genericParameters;
         }
      }


      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as MethodStructureInfo );
      }

      public override Int32 GetHashCode()
      {
         return ( 17 * 23 + this.Name.GetHashCodeSafe() ) * 23 + this.Signature.GetHashCodeSafe();
      }

      public Boolean Equals( MethodStructureInfo other )
      {
         var retVal = ReferenceEquals( this, other )
            || ( other != null
            && String.Equals( this.Name, other.Name )
            && this.Signature.EqualsTypedEquatable( other.Signature )
            && this.IL.EqualsTypedEquatable( other.IL )
            && this.Attributes == other.Attributes
            && this.ImplementationAttributes == other.ImplementationAttributes
            && this.PInvokeInfo.EqualsTypedEquatable( other.PInvokeInfo )
            && ListEqualityComparer<List<GenericParameterStructuralInfo>, GenericParameterStructuralInfo>.DefaultListEqualityComparer.Equals( this._genericParameters, other._genericParameters )
            && this.SecurityInfo.EqualsTypedEquatable( other.SecurityInfo )
            && this.CheckCAArgs( other )
            );

         if ( !retVal )
         {

         }
         return retVal;
      }

      public override String ToString()
      {
         return this.Name;
      }
   }

   public sealed class MethodILStructureInfo : IEquatable<MethodILStructureInfo>
   {

      public bool Equals( MethodILStructureInfo other )
      {
         throw new NotImplementedException();
      }
   }

   public sealed class ParameterStructureInfo : StructuralElementWithCustomAttributes, IEquatable<ParameterStructureInfo>
   {
      internal ParameterStructureInfo()
      {

      }

      internal ParameterStructureInfo( ParameterDefinition pDef )
         : this()
      {
         this.Attributes = pDef.Attributes;
         this.Name = pDef.Name;
         this.Sequence = pDef.Sequence;
      }

      public ParameterAttributes Attributes { get; set; }
      public Int32 Sequence { get; set; }
      public String Name { get; set; }
      public Object ConstantValue { get; set; }
      public MarshalingInfo MarshalingInfo { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as ParameterStructureInfo );
      }

      public override Int32 GetHashCode()
      {
         return ( 17 * 23 + this.Name.GetHashCodeSafe() ) * 23 + this.Sequence.GetHashCode();
      }

      public Boolean Equals( ParameterStructureInfo other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && this.Sequence == other.Sequence
            && String.Equals( this.Name, other.Name )
            && this.Attributes == other.Attributes
            && Comparers.MarshalingInfoEqualityComparer.Equals( this.MarshalingInfo, other.MarshalingInfo )
            && this.CheckCAArgs( other )
            );
      }
   }

   public abstract class StructuralInfoWithSemanticsMethods : StructuralElementWithCustomAttributes
   {
      private readonly List<Tuple<MethodSemanticsAttributes, MethodStructureInfo>> _semanticMethods;

      internal StructuralInfoWithSemanticsMethods()
      {
         this._semanticMethods = new List<Tuple<MethodSemanticsAttributes, MethodStructureInfo>>();
      }

      public List<Tuple<MethodSemanticsAttributes, MethodStructureInfo>> SemanticMethods
      {
         get
         {
            return this._semanticMethods;
         }
      }
   }

   public sealed class PropertyStructuralInfo : StructuralInfoWithSemanticsMethods, IEquatable<PropertyStructuralInfo>
   {
      internal PropertyStructuralInfo()
      {

      }

      internal PropertyStructuralInfo( PropertyDefinition pDef )
      {
         this.Attributes = pDef.Attributes;
         this.Name = pDef.Name;
      }

      public PropertyAttributes Attributes { get; set; }
      public String Name { get; set; }
      public SignatureStructuralInfo Signature { get; set; }
      public Object ConstantValue { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as PropertyStructuralInfo );
      }

      public override Int32 GetHashCode()
      {
         return ( 17 * 23 + this.Name.GetHashCodeSafe() ) * 23 + this.Signature.GetHashCodeSafe();
      }

      public Boolean Equals( PropertyStructuralInfo other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && String.Equals( this.Name, other.Name )
            && this.Attributes == other.Attributes
            && this.Signature.EqualsTypedEquatable( other.Signature )
            && Equals( this.ConstantValue, other.ConstantValue )
            && this.CheckCAArgs( other )
            );
      }

      public override String ToString()
      {
         return this.Name;
      }
   }

   public sealed class EventStructuralInfo : StructuralInfoWithSemanticsMethods, IEquatable<EventStructuralInfo>
   {
      internal EventStructuralInfo()
      {

      }

      internal EventStructuralInfo( EventDefinition pDef )
      {
         this.Attributes = pDef.Attributes;
         this.Name = pDef.Name;
      }

      public EventAttributes Attributes { get; set; }
      public String Name { get; set; }
      public AbstractTypeDescription EventType { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as EventStructuralInfo );
      }

      public override Int32 GetHashCode()
      {
         return ( 17 * 23 + this.Name.GetHashCodeSafe() ) * 23 + this.EventType.GetHashCodeSafe();
      }

      public Boolean Equals( EventStructuralInfo other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && String.Equals( this.Name, other.Name )
            && this.EventType.EqualsTyped( other.EventType )
            && this.Attributes == other.Attributes
            && this.CheckCAArgs( other )
            );
      }

      public override String ToString()
      {
         return this.Name;
      }
   }

   public sealed class InterfaceImplStructuralInfo : StructuralElementWithCustomAttributes, IEquatable<InterfaceImplStructuralInfo>
   {

      public AbstractTypeDescription InterfaceType { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as InterfaceImplStructuralInfo );
      }

      public override Int32 GetHashCode()
      {
         return this.InterfaceType.GetHashCodeSafe();
      }

      public Boolean Equals( InterfaceImplStructuralInfo other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && Equals( this.InterfaceType, other.InterfaceType )
            && this.CheckCAArgs( other )
            );
      }
   }

   public sealed class GenericParameterStructuralInfo : StructuralElementWithCustomAttributes, IEquatable<GenericParameterStructuralInfo>
   {
      private readonly List<GenericParameterConstraintStructuralInfo> _constraints;

      public GenericParameterStructuralInfo()
      {
         this._constraints = new List<GenericParameterConstraintStructuralInfo>();
      }

      public GenericParameterStructuralInfo( GenericParameterDefinition gParam )
         : this()
      {
         this.Name = gParam.Name;
         this.GenericParameterIndex = gParam.GenericParameterIndex;
         this.Attributes = gParam.Attributes;
      }

      public String Name { get; set; }
      public Int32 GenericParameterIndex { get; set; }
      public GenericParameterAttributes Attributes { get; set; }
      public List<GenericParameterConstraintStructuralInfo> Constraints
      {
         get
         {
            return this._constraints;
         }
      }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as GenericParameterStructuralInfo );
      }

      public override Int32 GetHashCode()
      {
         return ( 17 * 23 + this.Name.GetHashCodeSafe() ) * 23 + this.GenericParameterIndex.GetHashCode();
      }

      public Boolean Equals( GenericParameterStructuralInfo other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && String.Equals( this.Name, other.Name )
            && this.GenericParameterIndex == other.GenericParameterIndex
            && this.Attributes == other.Attributes
            && new HashSet<GenericParameterConstraintStructuralInfo>( this.Constraints ).SetEquals( other.Constraints )
            && this.CheckCAArgs( other )
            );
      }
   }

   public sealed class GenericParameterConstraintStructuralInfo : StructuralElementWithCustomAttributes, IEquatable<GenericParameterConstraintStructuralInfo>
   {
      public GenericParameterConstraintStructuralInfo()
      {

      }

      public AbstractTypeDescription Constraint { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as GenericParameterConstraintStructuralInfo );
      }

      public override Int32 GetHashCode()
      {
         return this.Constraint.GetHashCodeSafe();
      }

      public Boolean Equals( GenericParameterConstraintStructuralInfo other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && Equals( this.Constraint, other.Constraint )
            && this.CheckCAArgs( other )
            );
      }
   }

   public sealed class PInvokeInfo : IEquatable<PInvokeInfo>
   {
      public PInvokeAttributes Attributes { get; set; }
      public String PlatformInvokeName { get; set; }
      public String PlatformInvokeModuleName { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as PInvokeInfo );
      }

      public override Int32 GetHashCode()
      {
         return base.GetHashCode();
      }

      public Boolean Equals( PInvokeInfo other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && String.Equals( this.PlatformInvokeName, other.PlatformInvokeName )
            && String.Equals( this.PlatformInvokeModuleName, other.PlatformInvokeModuleName )
            && this.Attributes == other.Attributes
            );
      }
   }

   public sealed class SecurityStructuralInfo : StructuralElementWithCustomAttributes, IEquatable<SecurityStructuralInfo>
   {
      private readonly List<AbstractSecurityInformation> _permissionSets;

      public SecurityStructuralInfo( Int32 permissionSetCount = 0 )
      {
         this._permissionSets = new List<AbstractSecurityInformation>( permissionSetCount );
      }

      public SecurityStructuralInfo( SecurityDefinition secDef )
         : this( secDef.PermissionSets.Count )
      {
         this.SecurityAction = secDef.Action;
         this._permissionSets.AddRange( secDef.PermissionSets );
      }

      public SecurityAction SecurityAction { get; set; }
      public List<AbstractSecurityInformation> PermissionSets
      {
         get
         {
            return this._permissionSets;
         }
      }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as SecurityStructuralInfo );
      }

      public override Int32 GetHashCode()
      {
         return ListEqualityComparer<List<AbstractSecurityInformation>, AbstractSecurityInformation>.DefaultListEqualityComparer.GetHashCode( this.PermissionSets );
      }

      public Boolean Equals( SecurityStructuralInfo other )
      {

         return ReferenceEquals( this, other )
            || ( other != null
            && this.SecurityAction == other.SecurityAction
            && ListEqualityComparer<List<AbstractSecurityInformation>, AbstractSecurityInformation>.NewListEqualityComparer( Comparers.AbstractSecurityInformationEqualityComparer ).Equals( this.PermissionSets, other.PermissionSets )
            && this.CheckCAArgs( other )
            );
      }
   }

   public sealed class ManifestResourceStructuralInfo : StructuralElementWithCustomAttributes, IEquatable<ManifestResourceStructuralInfo>
   {
      internal ManifestResourceStructuralInfo()
      {

      }

      internal ManifestResourceStructuralInfo( ManifestResource mRes )
      {
         this.Name = mRes.Name;
         this.Attributes = mRes.Attributes;
         this.Offset = mRes.Offset;
      }

      public ManifestResourceData ManifestData { get; set; }
      public String Name { get; set; }
      public ManifestResourceAttributes Attributes { get; set; }
      public Int32 Offset { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as ManifestResourceStructuralInfo );
      }

      public override Int32 GetHashCode()
      {
         return this.Name.GetHashCodeSafe();
      }

      public Boolean Equals( ManifestResourceStructuralInfo other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && String.Equals( this.Name, other.Name )
            && Equals( this.ManifestData, other.ManifestData )
            && this.Attributes == other.Attributes
            && this.Offset == other.Offset
            && this.CheckCAArgs( other )
            );
      }
   }

   public enum ManifestResourceDataKind
   {
      Embedded,
      File,
      AssemblyRef
   }

   public abstract class ManifestResourceData
   {
      internal ManifestResourceData()
      {

      }

      public abstract ManifestResourceDataKind ManifestResourceDataKind { get; }
      public abstract override Boolean Equals( Object obj );
      public abstract override Int32 GetHashCode();
   }

   public sealed class ManifestResourceDataEmbedded : ManifestResourceData, IEquatable<ManifestResourceDataEmbedded>
   {

      public Byte[] Data { get; set; }

      public override ManifestResourceDataKind ManifestResourceDataKind
      {
         get
         {
            return ManifestResourceDataKind.Embedded;
         }
      }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as ManifestResourceDataEmbedded );
      }

      public override Int32 GetHashCode()
      {
         return ArrayEqualityComparer<Byte>.DefaultArrayEqualityComparer.GetHashCode( this.Data );
      }

      public Boolean Equals( ManifestResourceDataEmbedded other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && ArrayEqualityComparer<Byte>.DefaultArrayEqualityComparer.Equals( this.Data, other.Data )
            );
      }
   }

   public sealed class ManifestResourceDataFile : ManifestResourceData, IEquatable<ManifestResourceDataFile>
   {
      public FileReferenceStructureInfo FileReference { get; set; }

      public override ManifestResourceDataKind ManifestResourceDataKind
      {
         get
         {
            return ManifestResourceDataKind.Embedded;
         }
      }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as ManifestResourceDataFile );
      }

      public override Int32 GetHashCode()
      {
         return this.FileReference.GetHashCodeSafe();
      }

      public Boolean Equals( ManifestResourceDataFile other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && this.FileReference.EqualsTypedEquatable( other.FileReference )
            );
      }
   }

   public sealed class ManifestResourceDataAssemblyRef : ManifestResourceData, IEquatable<ManifestResourceDataAssemblyRef>
   {
      public AssemblyRefStructureInfo AssemblyRef { get; set; }

      public override ManifestResourceDataKind ManifestResourceDataKind
      {
         get
         {
            return ManifestResourceDataKind.Embedded;
         }
      }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as ManifestResourceDataAssemblyRef );
      }

      public override Int32 GetHashCode()
      {
         return this.AssemblyRef.GetHashCodeSafe();
      }

      public Boolean Equals( ManifestResourceDataAssemblyRef other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && this.AssemblyRef.EqualsTypedEquatable( other.AssemblyRef )
            );
      }
   }

   public sealed class MemberReferenceStructuralInfo : MethodDefOrRefStructure, IEquatable<MemberReferenceStructuralInfo>
   {
      internal MemberReferenceStructuralInfo()
      {

      }

      internal MemberReferenceStructuralInfo( MemberReference mRef )
         : this()
      {
         this.Name = mRef.Name;
      }

      public String Name { get; set; }
      public SignatureStructuralInfo Signature { get; set; }
      public MemberReferenceParent Parent { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as MemberReferenceStructuralInfo );
      }

      public override Int32 GetHashCode()
      {
         return ( 17 * 23 + this.Name.GetHashCodeSafe() ) * 23 + this.Parent.GetHashCodeSafe();
      }

      public Boolean Equals( MemberReferenceStructuralInfo other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && String.Equals( this.Name, other.Name )
            && Equals( this.Parent, other.Parent )
            && this.Signature.EqualsTypedEquatable( other.Signature )
            && this.CheckCAArgs( other )
            );
      }
   }

   public enum MemberReferenceParentKind
   {
      MethodDef,
      ModuleRef,
      Type
   }

   public abstract class MemberReferenceParent
   {
      internal MemberReferenceParent()
      {

      }

      public abstract MemberReferenceParentKind MemberReferenceParentKind { get; }
      public abstract override Boolean Equals( Object obj );
      public abstract override Int32 GetHashCode();
   }

   public sealed class MemberReferenceParentMethodDef : MemberReferenceParent, IEquatable<MemberReferenceParentMethodDef>
   {

      public MethodStructureInfo Method { get; set; }

      public override MemberReferenceParentKind MemberReferenceParentKind
      {
         get
         {
            return MemberReferenceParentKind.MethodDef;
         }
      }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as MemberReferenceParentMethodDef );
      }

      public override Int32 GetHashCode()
      {
         return this.Method.GetHashCodeSafe();
      }

      public Boolean Equals( MemberReferenceParentMethodDef other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && this.Method.EqualsTypedEquatable( other.Method )
            );
      }
   }

   public sealed class MemberReferenceParentModuleRef : MemberReferenceParent, IEquatable<MemberReferenceParentModuleRef>
   {

      public ModuleRefStructureInfo ModuleRef { get; set; }

      public override MemberReferenceParentKind MemberReferenceParentKind
      {
         get
         {
            return MemberReferenceParentKind.ModuleRef;
         }
      }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as MemberReferenceParentModuleRef );
      }

      public override Int32 GetHashCode()
      {
         return this.ModuleRef.GetHashCodeSafe();
      }

      public Boolean Equals( MemberReferenceParentModuleRef other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && this.ModuleRef.EqualsTypedEquatable( other.ModuleRef )
            );
      }
   }

   public sealed class MemberReferenceParentType : MemberReferenceParent, IEquatable<MemberReferenceParentType>
   {

      public AbstractTypeDescription Type { get; set; }

      public override MemberReferenceParentKind MemberReferenceParentKind
      {
         get
         {
            return MemberReferenceParentKind.Type;
         }
      }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as MemberReferenceParentType );
      }

      public override Int32 GetHashCode()
      {
         return this.Type.GetHashCodeSafe();
      }

      public Boolean Equals( MemberReferenceParentType other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && Equals( this.Type, other.Type )
            );
      }
   }

   public sealed class StandaloneSignatureStructure : StructuralElementWithCustomAttributes, IEquatable<StandaloneSignatureStructure>
   {
      internal StandaloneSignatureStructure()
      {

      }

      public SignatureStructuralInfo Signature { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as StandaloneSignatureStructure );
      }

      public override Int32 GetHashCode()
      {
         return this.Signature.GetHashCodeSafe();
      }

      public Boolean Equals( StandaloneSignatureStructure other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && this.Signature.EqualsTypedEquatable( other.Signature )
            && this.CheckCAArgs( other )
            );
      }
   }

   public sealed class MethodSpecificationStructure : StructuralElementWithCustomAttributes, IEquatable<MethodSpecificationStructure>
   {
      internal MethodSpecificationStructure()
      {

      }

      public MethodDefOrRefStructure Method { get; set; }
      public SignatureStructuralInfo Signature { get; set; }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as MethodSpecificationStructure );
      }

      public override Int32 GetHashCode()
      {
         return this.Method.GetHashCodeSafe();
      }

      public Boolean Equals( MethodSpecificationStructure other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && Equals( this.Method, other.Method )
            && this.Signature.EqualsTypedEquatable( other.Signature )
            && this.CheckCAArgs( other )
            );
      }
   }

   public sealed class SignatureStructuralInfo : IEquatable<SignatureStructuralInfo>
   {
      private readonly ModuleStructureInfo _module;

      internal SignatureStructuralInfo( ModuleStructureInfo module, AbstractSignature signature )
      {
         ArgumentValidator.ValidateNotNull( "Module", module );

         this._module = module;
         this.Signature = signature;
      }

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as SignatureStructuralInfo );
      }

      public override Int32 GetHashCode()
      {
         return this.Signature.GetHashCodeSafe();
      }

      public Boolean Equals( SignatureStructuralInfo other )
      {
         return ReferenceEquals( this, other )
            || ( other != null
            && (
               ( ReferenceEquals( this.Module, other.Module ) && ReferenceEquals( this.Signature, other.Signature ) )
               || this.SignaturesMatch( this.Signature, other.Signature, other.Module )
               )
            );
      }

      public ModuleStructureInfo Module
      {
         get
         {
            return this._module;
         }
      }

      public AbstractSignature Signature { get; set; }

      private Boolean SignaturesMatch( AbstractSignature thisSig, AbstractSignature otherSig, ModuleStructureInfo otherModule )
      {
         var retVal = ( thisSig == null ) == ( otherSig == null );
         if ( retVal && thisSig != null )
         {
            retVal = thisSig.SignatureKind == otherSig.SignatureKind;
            if ( retVal )
            {
               switch ( thisSig.SignatureKind )
               {
                  case SignatureKind.Field:
                     retVal = SignaturesMatch_Field( (FieldSignature) thisSig, (FieldSignature) otherSig, otherModule );
                     break;
                  case SignatureKind.GenericMethodInstantiation:
                     retVal = SignaturesMatch_GenericMethod( (GenericMethodSignature) thisSig, (GenericMethodSignature) otherSig, otherModule );
                     break;
                  case SignatureKind.LocalVariables:
                     retVal = SignaturesMatch_Locals( (LocalVariablesSignature) thisSig, (LocalVariablesSignature) otherSig, otherModule );
                     break;
                  case SignatureKind.MethodDefinition:
                     retVal = SignaturesMatch_MethodDef( (MethodDefinitionSignature) thisSig, (MethodDefinitionSignature) otherSig, otherModule );
                     break;
                  case SignatureKind.MethodReference:
                     retVal = SignaturesMatch_MethodRef( (MethodReferenceSignature) thisSig, (MethodReferenceSignature) otherSig, otherModule );
                     break;
                  case SignatureKind.Property:
                     retVal = SignaturesMatch_Property( (PropertySignature) thisSig, (PropertySignature) otherSig, otherModule );
                     break;
                  case SignatureKind.RawSignature:
                     retVal = ArrayEqualityComparer<Byte>.DefaultArrayEqualityComparer.Equals( ( (RawSignature) thisSig ).Bytes, ( (RawSignature) otherSig ).Bytes );
                     break;
                  case SignatureKind.Type:
                     retVal = SignaturesMatch_Type( (TypeSignature) thisSig, (TypeSignature) otherSig, otherModule );
                     break;
                  default:
                     retVal = false;
                     break;
               }
            }
         }

         return retVal;
      }

      private Boolean SignaturesMatch_Type( TypeSignature thisSig, TypeSignature otherSig, ModuleStructureInfo otherModule )
      {
         var retVal = ( thisSig == null ) == ( otherSig == null );
         if ( retVal && thisSig != null )
         {
            retVal = thisSig.TypeSignatureKind == otherSig.TypeSignatureKind;
            if ( retVal )
            {
               switch ( thisSig.TypeSignatureKind )
               {
                  case TypeSignatureKind.ClassOrValue:
                     retVal = SignaturesMatch_Type_ClassOrValue( (ClassOrValueTypeSignature) thisSig, (ClassOrValueTypeSignature) otherSig, otherModule );
                     break;
                  case TypeSignatureKind.ComplexArray:
                     retVal = SignaturesMatch_Type_ComplexArray( (ComplexArrayTypeSignature) thisSig, (ComplexArrayTypeSignature) otherSig, otherModule );
                     break;
                  case TypeSignatureKind.FunctionPointer:
                     retVal = SignaturesMatch_MethodRef( ( (FunctionPointerTypeSignature) thisSig ).MethodSignature, ( (FunctionPointerTypeSignature) otherSig ).MethodSignature, otherModule );
                     break;
                  case TypeSignatureKind.GenericParameter:
                     var thisG = (GenericParameterTypeSignature) thisSig;
                     var otherG = (GenericParameterTypeSignature) otherSig;
                     retVal = thisG.GenericParameterIndex == otherG.GenericParameterIndex
                        && thisG.IsTypeParameter == otherG.IsTypeParameter;
                     break;
                  case TypeSignatureKind.Pointer:
                     retVal = SignaturesMatch_Type_Pointer( (PointerTypeSignature) thisSig, (PointerTypeSignature) otherSig, otherModule );
                     break;
                  case TypeSignatureKind.Simple:
                     retVal = ( (SimpleTypeSignature) thisSig ).SimpleType == ( (SimpleTypeSignature) otherSig ).SimpleType;
                     break;
                  case TypeSignatureKind.SimpleArray:
                     retVal = SignaturesMatch_Type_SimpleArray( (SimpleArrayTypeSignature) thisSig, (SimpleArrayTypeSignature) otherSig, otherModule );
                     break;
                  default:
                     retVal = false;
                     break;
               }
            }
         }

         return retVal;
      }

      private Boolean SignaturesMatch_Type_ClassOrValue( ClassOrValueTypeSignature thisSig, ClassOrValueTypeSignature otherSig, ModuleStructureInfo otherModule )
      {
         var thisArgs = thisSig.GenericArguments;
         var otherArgs = thisSig.GenericArguments;
         var retVal = thisSig.IsClass == otherSig.IsClass
            && thisArgs.Count == otherArgs.Count
            && this.Module.FromTypeDefOrRefOrSpec( thisSig.Type ).EqualsTextual( otherModule.FromTypeDefOrRefOrSpec( otherSig.Type ) );
         if ( retVal && thisArgs.Count > 0 )
         {
            var i = 0;
            while ( i < thisArgs.Count && SignaturesMatch_Type( thisArgs[i], otherArgs[i], otherModule ) )
            {
               ++i;
            }
            retVal = i == thisArgs.Count;
         }

         return retVal;
      }

      private Boolean SignaturesMatch_Type_ComplexArray( ComplexArrayTypeSignature thisSig, ComplexArrayTypeSignature otherSig, ModuleStructureInfo otherModule )
      {
         return thisSig.Rank == otherSig.Rank
            && ListEqualityComparer<List<Int32>, Int32>.DefaultListEqualityComparer.Equals( thisSig.LowerBounds, otherSig.LowerBounds )
            && ListEqualityComparer<List<Int32>, Int32>.DefaultListEqualityComparer.Equals( thisSig.Sizes, otherSig.Sizes )
            && SignaturesMatch_Type( thisSig.ArrayType, otherSig.ArrayType, otherModule );
      }

      private Boolean SignaturesMatch_Type_SimpleArray( SimpleArrayTypeSignature thisSig, SimpleArrayTypeSignature otherSig, ModuleStructureInfo otherModule )
      {
         return SignaturesMatch_Mods( thisSig.CustomModifiers, otherSig.CustomModifiers, otherModule )
            && SignaturesMatch_Type( thisSig.ArrayType, otherSig.ArrayType, otherModule );
      }

      private Boolean SignaturesMatch_Type_Pointer( PointerTypeSignature thisSig, PointerTypeSignature otherSig, ModuleStructureInfo otherModule )
      {
         return SignaturesMatch_Mods( thisSig.CustomModifiers, otherSig.CustomModifiers, otherModule )
            && SignaturesMatch_Type( thisSig.PointerType, otherSig.PointerType, otherModule );
      }

      private Boolean SignaturesMatch_Mods( List<CustomModifierSignature> thisMods, List<CustomModifierSignature> otherMods, ModuleStructureInfo otherModule )
      {
         var retVal = thisMods.Count == otherMods.Count;
         if ( retVal && thisMods.Count > 0 )
         {
            var i = 0;
            while ( i < thisMods.Count && thisMods[i].IsOptional == otherMods[i].IsOptional && this.Module.FromTypeDefOrRefOrSpec( thisMods[i].CustomModifierType ).EqualsTextual( otherModule.FromTypeDefOrRefOrSpec( otherMods[i].CustomModifierType ) ) )
            {
               ++i;
            }
            retVal = i == thisMods.Count;
         }
         return retVal;
      }

      private Boolean SignaturesMatch_Field( FieldSignature thisSig, FieldSignature otherSig, ModuleStructureInfo otherModule )
      {
         return SignaturesMatch_Mods( thisSig.CustomModifiers, otherSig.CustomModifiers, otherModule )
            && SignaturesMatch_Type( thisSig.Type, otherSig.Type, otherModule );
      }

      private Boolean SignaturesMatch_GenericMethod( GenericMethodSignature thisSig, GenericMethodSignature otherSig, ModuleStructureInfo otherModule )
      {
         var thisArgs = thisSig.GenericArguments;
         var otherArgs = otherSig.GenericArguments;
         var retVal = thisArgs.Count == otherArgs.Count;
         if ( retVal && thisArgs.Count > 0 )
         {
            var i = 0;
            while ( i < thisArgs.Count && SignaturesMatch_Type( thisArgs[i], otherArgs[i], otherModule ) )
            {
               ++i;
            }
            retVal = i == thisArgs.Count;
         }

         return retVal;
      }

      private Boolean SignaturesMatch_MethodDef( MethodDefinitionSignature thisSig, MethodDefinitionSignature otherSig, ModuleStructureInfo otherModule )
      {
         return SignaturesMatch_AbstractMethodSig( thisSig, otherSig, otherModule );
      }

      private Boolean SignaturesMatch_MethodRef( MethodReferenceSignature thisSig, MethodReferenceSignature otherSig, ModuleStructureInfo otherModule )
      {
         return SignaturesMatch_AbstractMethodSig( thisSig, otherSig, otherModule )
            && SignaturesMatch_ParamOrLocalSigs( thisSig.VarArgsParameters, otherSig.VarArgsParameters, otherModule );
      }

      private Boolean SignaturesMatch_AbstractMethodSig( AbstractMethodSignature thisSig, AbstractMethodSignature otherSig, ModuleStructureInfo otherModule )
      {
         var retVal = ( thisSig == null ) == ( otherSig == null );
         if ( retVal && thisSig != null )
         {
            var thisParams = thisSig.Parameters;
            var otherParams = otherSig.Parameters;
            retVal = thisSig.SignatureStarter == otherSig.SignatureStarter
               && thisSig.GenericArgumentCount == otherSig.GenericArgumentCount
               && SignaturesMatch_ParamOrLocalSig( thisSig.ReturnType, otherSig.ReturnType, otherModule );

            if ( retVal && thisParams.Count > 0 )
            {
               retVal = SignaturesMatch_ParamOrLocalSigs( thisSig.Parameters, otherSig.Parameters, otherModule );
            }
         }
         return retVal;
      }

      private Boolean SignaturesMatch_ParamOrLocalSigs<TSig>( List<TSig> thisSigs, List<TSig> otherSigs, ModuleStructureInfo otherModule )
         where TSig : ParameterOrLocalVariableSignature
      {
         var retVal = thisSigs.Count == otherSigs.Count;
         if ( retVal && thisSigs.Count > 0 )
         {
            var i = 0;
            while ( i < thisSigs.Count && SignaturesMatch_ParamOrLocalSig( thisSigs[i], otherSigs[i], otherModule ) )
            {
               ++i;
            }
            retVal = i == thisSigs.Count;
         }
         return retVal;
      }

      private Boolean SignaturesMatch_ParamOrLocalSig( ParameterOrLocalVariableSignature thisSig, ParameterOrLocalVariableSignature otherSig, ModuleStructureInfo otherModule )
      {
         return thisSig.IsByRef == otherSig.IsByRef
            && SignaturesMatch_Mods( thisSig.CustomModifiers, otherSig.CustomModifiers, otherModule )
            && SignaturesMatch_Type( thisSig.Type, otherSig.Type, otherModule );
      }

      private Boolean SignaturesMatch_Property( PropertySignature thisSig, PropertySignature otherSig, ModuleStructureInfo otherModule )
      {
         return thisSig.HasThis == otherSig.HasThis
            && SignaturesMatch_Type( thisSig.PropertyType, otherSig.PropertyType, otherModule )
            && SignaturesMatch_ParamOrLocalSigs( thisSig.Parameters, otherSig.Parameters, otherModule )
            && SignaturesMatch_Mods( thisSig.CustomModifiers, otherSig.CustomModifiers, otherModule );
      }

      private Boolean SignaturesMatch_Locals( LocalVariablesSignature thisSig, LocalVariablesSignature otherSig, ModuleStructureInfo otherModule )
      {
         var thisLocals = thisSig.Locals;
         var otherLocals = otherSig.Locals;
         var retVal = SignaturesMatch_ParamOrLocalSigs( thisLocals, otherLocals, otherModule );
         if ( retVal && thisLocals.Count > 0 )
         {
            var i = 0;
            while ( i < thisLocals.Count && thisLocals[i].IsPinned == otherLocals[i].IsPinned )
            {
               ++i;
            }
            retVal = i == thisLocals.Count;
         }

         return retVal;
      }

   }

}

public static partial class E_CILTests
{
   public static AbstractTypeDescription FromTypeDefOrRefOrSpec( this ModuleStructureInfo moduleInfo, TableIndex index )
   {
      switch ( index.Table )
      {
         case Tables.TypeDef:
            return moduleInfo.GetTypeDefDescription( index.Index );
         case Tables.TypeRef:
            return moduleInfo.GetTypeRefDescription( index.Index );
         case Tables.TypeSpec:
            return moduleInfo.GetTypeSpecDescription( index.Index );
         default:
            throw new InvalidOperationException( "Unsupported TypeDef/Ref/Spec: " + index + "." );
      }
   }
}