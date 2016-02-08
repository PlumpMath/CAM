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
extern alias CAMPhysical;
using CAMPhysical;
using CAMPhysical::CILAssemblyManipulator.Physical;
using CAMPhysical::CILAssemblyManipulator.Physical.Meta;

using CILAssemblyManipulator.Physical;
using CILAssemblyManipulator.Physical.IO;
using CommonUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TabularMetaData;

namespace CILAssemblyManipulator.Physical.IO
{
   /// <summary>
   /// The sole purpose of this class is to resolve <see cref="RawCustomAttributeSignature"/>s and <see cref="RawSecurityInformation"/>s into <see cref="ResolvedCustomAttributeSignature"/>s and <see cref="SecurityInformation"/>, respectively.
   /// </summary>
   /// <remarks>
   /// <para>
   /// This class is rarely used directly, as e.g. <see cref="T:CILAssemblyManipulator.Physical.IO.CILMetaDataLoader"/> will use this by default in <see cref="T:CILAssemblyManipulator.Physical.IO.CILMetaDataLoader.ResolveMetaData()"/> method.
   /// In order to fully utilize this class, one should register to <see cref="AssemblyReferenceResolveEvent"/> and <see cref="ModuleReferenceResolveEvent"/> events.
   /// </para>
   /// <para>
   /// The custom attribute signatures are serialized in meta data (see ECMA-335 spec for more info) in such way that enum values have their type names present, but the underlying enum value type (e.g. integer) is not present.
   /// Therefore, the custom attribute signatures, and security signatures (which share some serialization functionality with custom attribute signatures) require dynamic resolving of what is the underlying enum value type.
   /// This class encapsulates this resolving process, which may be complicated and involve loading of several dependant assemblies.
   /// </para>
   /// </remarks>
   public sealed class MetaDataResolver
   {
      private sealed class MDSpecificCache
      {
         private static readonly Object NULL = new Object();

         private readonly MetaDataResolver _owner;
         private readonly CILMetaData _md;

         private readonly IDictionary<Object, MDSpecificCache> _assemblyResolveFreeFormCache;
         private readonly IDictionary<Int32, MDSpecificCache> _assembliesByInfoCache;
         private readonly IDictionary<Int32, MDSpecificCache> _modulesCache;

         private readonly IDictionary<KeyValuePair<String, String>, Int32> _topLevelTypeCache; // Key: ns + type pair, Value: TypeDef index
         private readonly IDictionary<Int32, CustomAttributeArgumentTypeSimple> _typeDefCache; // Key: TypeDef index, Value: CA type
         private readonly IDictionary<Int32, Tuple<MDSpecificCache, Int32>> _typeRefCache; // Key: TypeRef index, Value: TypeDef index in another metadata
         private readonly IDictionary<String, Int32> _typeNameCache; // Key - type name (ns + enclosing classes + type name), Value - TypeDef index
         private readonly IDictionary<Int32, String> _typeNameReverseCache; // Key - typeDefIndex, Value - type name
         private readonly IDictionary<Int32, String> _typeRefReverseCache;

         internal MDSpecificCache( MetaDataResolver owner, CILMetaData md )
         {
            ArgumentValidator.ValidateNotNull( "Owner", owner );
            ArgumentValidator.ValidateNotNull( "Metadata", md );

            this._owner = owner;
            this._md = md;

            this._assemblyResolveFreeFormCache = new Dictionary<Object, MDSpecificCache>();
            this._assembliesByInfoCache = new Dictionary<Int32, MDSpecificCache>();
            this._modulesCache = new Dictionary<Int32, MDSpecificCache>();

            this._topLevelTypeCache = new Dictionary<KeyValuePair<String, String>, Int32>();
            this._typeDefCache = new Dictionary<Int32, CustomAttributeArgumentTypeSimple>();
            this._typeRefCache = new Dictionary<Int32, Tuple<MDSpecificCache, Int32>>();
            this._typeNameCache = new Dictionary<String, Int32>();
            this._typeNameReverseCache = new Dictionary<Int32, String>();
            this._typeRefReverseCache = new Dictionary<Int32, String>();
         }

         internal CILMetaData MD
         {
            get
            {
               return this._md;
            }
         }

         internal MDSpecificCache ResolveCacheByAssemblyString( String assemblyString )
         {
            var parseSuccessful = false;
            Object key;
            if ( String.IsNullOrEmpty( assemblyString ) )
            {
               key = NULL;
            }
            else
            {
               AssemblyInformation aInfo;
               Boolean isFullPublicKey;
               parseSuccessful = AssemblyInformation.TryParse( assemblyString, out aInfo, out isFullPublicKey );
               key = parseSuccessful ?
                  (Object) new AssemblyInformationForResolving( aInfo, isFullPublicKey ) :
                  assemblyString;
            }

            return this._assemblyResolveFreeFormCache.GetOrAdd_NotThreadSafe(
               key,
               kkey => this._owner.ResolveAssemblyReferenceWithEvent(
                  this._md,
                  parseSuccessful ? null : assemblyString,
                  parseSuccessful ? (AssemblyInformationForResolving) kkey : null
                  )
               );
         }

         internal CustomAttributeArgumentTypeSimple ResolveTypeFromTypeName( String typeName )
         {
            Int32 tDefIdx;
            this.ResolveTypeFromTypeName( typeName, out tDefIdx );

            return this.ResolveTypeFromTypeDef( tDefIdx );
         }

         private void ResolveTypeFromTypeName( String typeName, out Int32 tDefIndexParam )
         {
            tDefIndexParam = this._typeNameCache.GetOrAdd_NotThreadSafe( typeName, tn =>
            {
               Int32 tDefIndex;
               String enclosingType, nestedType;
               var isNestedType = typeName.ParseTypeNameStringForNestedType( out enclosingType, out nestedType );
               if ( isNestedType )
               {
                  this.ResolveTypeFromTypeName( enclosingType, out tDefIndex );
                  tDefIndex = this.FindNestedTypeIndex( tDefIndex, nestedType );
               }
               else
               {
                  String ns;
                  var hasNS = typeName.ParseTypeNameStringForNamespace( out ns, out typeName );
                  tDefIndex = this.ResolveTopLevelType( typeName, ns );
               }

               return tDefIndex;
            } );
         }

         internal CustomAttributeArgumentTypeSimple ResolveTypeFromTypeDef( Int32 index )
         {
            return index < 0 ?
               null :
               this._typeDefCache
                  .GetOrAdd_NotThreadSafe( index, idx =>
                  {
                     var md = this._md;
                     Int32 enumFieldIndex;
                     CustomAttributeArgumentTypeSimple retVal = null;
                     if ( md.TryGetEnumValueFieldIndex( idx, out enumFieldIndex ) )
                     {
                        var sig = md.FieldDefinitions.TableContents[enumFieldIndex].Signature.Type;
                        if ( sig != null && sig.TypeSignatureKind == TypeSignatureKind.Simple )
                        {
                           retVal = this.ResolveCATypeSimple( ( (SimpleTypeSignature) sig ).SimpleType );
                        }
                     }

                     return retVal;
                  } );
         }

         private CustomAttributeArgumentTypeSimple ResolveCATypeSimple( SimpleTypeSignatureKind elementType )
         {
            return this._md.SignatureProvider.GetSimpleCATypeOrNull( (CustomAttributeArgumentTypeSimpleKind) elementType );
         }

         internal String ResolveTypeNameFromTypeDef( Int32 index )
         {
            return index < 0 ?
               null :
               this._typeNameReverseCache
                  .GetOrAdd_NotThreadSafe( index, idx =>
                  {
                     var md = this._md;
                     var tDef = md.TypeDefinitions.GetOrNull( idx );
                     String retVal;
                     if ( tDef != null )
                     {
                        var nestedDef = md.NestedClassDefinitions.TableContents.FirstOrDefault( nc => nc != null && nc.NestedClass.Index == idx );
                        if ( nestedDef == null )
                        {
                           // This is top-level class
                           retVal = Miscellaneous.CombineNamespaceAndType( tDef.Namespace, tDef.Name );
                        }
                        else
                        {
                           // Nested type - recursion
                           // TODO get rid of recursion

                           retVal = Miscellaneous.CombineEnclosingAndNestedType( this.ResolveTypeNameFromTypeDef( nestedDef.EnclosingClass.Index ), tDef.Name );
                        }
                     }
                     else
                     {
                        retVal = null;
                     }

                     return retVal;
                  } );
         }

         internal String ResolveTypeNameFromTypeRef( Int32 index )
         {
            return this._typeRefReverseCache.GetOrAdd_NotThreadSafe( index, idx =>
            {
               MDSpecificCache otherMD; Int32 tDefIndex;
               this.ResolveTypeNameFromTypeRef( index, out otherMD, out tDefIndex );
               var typeRefString = otherMD == null ? null : otherMD.ResolveTypeNameFromTypeDef( tDefIndex );
               if ( typeRefString != null && !ReferenceEquals( this, otherMD ) && otherMD._md.AssemblyDefinitions.GetRowCount() > 0 )
               {
                  typeRefString = Miscellaneous.CombineAssemblyAndType( otherMD._md.AssemblyDefinitions.TableContents[0].ToString(), typeRefString );
               }

               return typeRefString;
            } );
         }

         private void ResolveTypeNameFromTypeRef( Int32 index, out MDSpecificCache otherMDParam, out Int32 tDefIndexParam )
         {
            var tuple = this._typeRefCache.GetOrAdd_NotThreadSafe( index, idx =>
            {
               var md = this._md;
               var tRef = md.TypeReferences.GetOrNull( idx );

               var tDefIndex = -1;
               MDSpecificCache otherMD;
               if ( tRef == null )
               {
                  otherMD = null;
               }
               else
               {
                  otherMD = this;
                  if ( tRef.ResolutionScope.HasValue )
                  {
                     var resScope = tRef.ResolutionScope.Value;
                     var resIdx = resScope.Index;
                     switch ( resScope.Table )
                     {
                        case Tables.TypeRef:
                           // Nested type
                           this.ResolveTypeNameFromTypeRef( resIdx, out otherMD, out tDefIndex );
                           if ( otherMD != null )
                           {
                              tDefIndex = otherMD.FindNestedTypeIndex( tDefIndex, tRef.Name );
                           }
                           break;
                        case Tables.ModuleRef:
                           // Same assembly, different module
                           otherMD = this.ResolveModuleReference( resIdx );
                           if ( otherMD != null )
                           {
                              tDefIndex = otherMD.ResolveTopLevelType( tRef.Name, tRef.Namespace );
                           }
                           break;
                        case Tables.Module:
                           // Same as type-def
                           tDefIndex = resIdx;
                           break;
                        case Tables.AssemblyRef:
                           // Different assembly
                           otherMD = this.ResolveAssemblyReference( resIdx );
                           if ( otherMD != null )
                           {
                              tDefIndex = otherMD.ResolveTopLevelType( tRef.Name, tRef.Namespace );
                           }
                           break;
                     }
                  }
                  else
                  {
                     // Seek exported type table for this type, and check its implementation field
                     throw new NotImplementedException( "Exported type in type reference row." );
                  }
               }

               return Tuple.Create( otherMD, tDefIndex );
            } );

            otherMDParam = tuple.Item1;
            tDefIndexParam = tuple.Item2;
         }

         private Int32 ResolveTopLevelType( String typeName, String typeNamespace )
         {
            return this._topLevelTypeCache
               .GetOrAdd_NotThreadSafe( new KeyValuePair<String, String>( typeNamespace, typeName ), kvp =>
               {
                  var md = this._md;
                  var ns = kvp.Key;
                  var tn = kvp.Value;

                  var hasNS = !String.IsNullOrEmpty( ns );
                  var suitableIndex = md.TypeDefinitions.TableContents.FindIndex( tDef =>
                     String.Equals( tDef.Name, tn )
                     && (
                        ( hasNS && String.Equals( tDef.Namespace, ns ) )
                        || ( !hasNS && String.IsNullOrEmpty( tDef.Namespace ) )
                     ) );

                  // Check that this is not nested type
                  if ( suitableIndex >= 0
                     && md.NestedClassDefinitions.TableContents.Any( nc => nc.NestedClass.Index == suitableIndex ) // TODO cache this? //.GetReferencingRowsFromOrdered( Tables.TypeDef, suitableIndex, nc => nc.NestedClass ).Any() // this will be true if the type definition at index 'suitableIndex' has declaring type, i.e. it is nested type
                     )
                  {
                     suitableIndex = -1;
                  }

                  return suitableIndex;
               } );
         }

         private Int32 FindNestedTypeIndex( Int32 enclosingTypeIndex, String nestedTypeName )
         {
            Int32 retVal;
            var md = this._md;
            if ( md == null )
            {
               retVal = -1;
            }
            else
            {
               var otherTDList = md.TypeDefinitions;
               var otherTD = otherTDList.GetOrNull( enclosingTypeIndex );
               NestedClassDefinition nestedTD = null;
               if ( otherTD != null )
               {
                  // Find nested type, which has this type as its declaring type and its name equal to tRef's
                  // Skip to the first row where nested class index is greater than type def index (since in typedef table, all nested class definitions must follow encloding class definition)
                  nestedTD = md.NestedClassDefinitions.TableContents
                     //.SkipWhile( nc => nc.NestedClass.Index <= enclosingTypeIndex )
                     .Where( nc =>
                     {
                        var match = nc.EnclosingClass.Index == enclosingTypeIndex;
                        if ( match )
                        {
                           var ncTD = otherTDList.GetOrNull( nc.NestedClass.Index );
                           match = ncTD != null
                              && String.Equals( ncTD.Name, nestedTypeName );
                        }
                        return match;
                     } )
                     .FirstOrDefault();
               }

               retVal = nestedTD == null ?
                  -1 :
                  nestedTD.NestedClass.Index;
            }

            return retVal;
         }

         private MDSpecificCache ResolveModuleReference( Int32 modRefIdx )
         {
            return this._modulesCache.GetOrAdd_NotThreadSafe(
               modRefIdx,
               idx =>
               {
                  var mRef = this._md.ModuleReferences.GetOrNull( idx );
                  return mRef == null ? null : this._owner.ResolveModuleReferenceWithEvent( this._md, mRef.ModuleName );
               } );
         }

         private MDSpecificCache ResolveAssemblyReference( Int32 aRefIdx )
         {
            return this._assembliesByInfoCache.GetOrAdd_NotThreadSafe(
               aRefIdx,
               idx =>
               {
                  var aRef = this._md.AssemblyReferences.GetOrNull( idx );
                  return aRef == null ? null : this._owner.ResolveAssemblyReferenceWithEvent( this._md, null, new AssemblyInformationForResolving( aRef ) );
               } );
         }
      }

      private readonly IDictionary<CILMetaData, MDSpecificCache> _mdCaches;
      private readonly Func<CILMetaData, MDSpecificCache> _mdCacheFactory;

      /// <summary>
      /// Creates a new instance of <see cref="MetaDataResolver"/> with an empty cache.
      /// </summary>
      public MetaDataResolver()
      {
         this._mdCaches = new Dictionary<CILMetaData, MDSpecificCache>();
         this._mdCacheFactory = this.MDSpecificCacheFactory;
      }

      /// <summary>
      /// Clears all cached information of this <see cref="MetaDataResolver"/>.
      /// </summary>
      public void ClearCache()
      {
         this._mdCaches.Clear();
      }

      /// <summary>
      /// This event will be fired when an assembly reference will need to be resolved.
      /// </summary>
      /// <seealso cref="AssemblyReferenceResolveEventArgs"/>
      public event EventHandler<AssemblyReferenceResolveEventArgs> AssemblyReferenceResolveEvent;

      /// <summary>
      /// This event will be fired when a module reference will need to be resolved.
      /// </summary>
      /// <seealso cref="ModuleReferenceResolveEventArgs"/>
      public event EventHandler<ModuleReferenceResolveEventArgs> ModuleReferenceResolveEvent;

      /// <summary>
      /// Tries to resolve a custom attribute signature in a given <see cref="CILMetaData"/> at given index.
      /// </summary>
      /// <param name="md">The <see cref="CILMetaData"/>.</param>
      /// <param name="index">The index in <see cref="CILMetaData.CustomAttributeDefinitions"/> to resolve signature.</param>
      /// <returns>non-<c>null</c> resolved signature, or <c>null</c> if resolving was unsuccessful.</returns>
      /// <exception cref="ArgumentNullException">If <paramref name="md"/> is <c>null</c>.</exception>
      public ResolvedCustomAttributeSignature ResolveCustomAttributeSignature(
         CILMetaData md,
         Int32 index
         )
      {
         ArgumentValidator.ValidateNotNull( "Metadata", md );

         var customAttribute = md.CustomAttributeDefinitions.GetOrNull( index );
         ResolvedCustomAttributeSignature signature = null;

         if ( customAttribute != null )
         {
            var caSig = customAttribute.Signature as RawCustomAttributeSignature;
            if ( caSig != null )
            {
               signature = this.TryResolveCustomAttributeSignature( md, caSig.Bytes, 0, customAttribute.Type );
               if ( signature != null )
               {
                  customAttribute.Signature = signature;
               }
            }
         }

         return signature;
      }

      /// <summary>
      /// Tries to resolve a security declaration signatures in a given <see cref="CILMetaData"/> at a given index.
      /// </summary>
      /// <param name="md">The <see cref="CILMetaData"/>.</param>
      /// <param name="index">The index in <see cref="CILMetaData.SecurityDefinitions"/> to resolve signature.</param>
      /// <returns><c>true</c> if all <see cref="SecurityDefinition.PermissionSets"/> have been resolved; <c>false</c> otherwise.</returns>
      /// <exception cref="ArgumentNullException">If <paramref name="md"/> is <c>null</c>.</exception>
      public Boolean ResolveSecurityDeclaration(
         CILMetaData md,
         Int32 index
         )
      {
         ArgumentValidator.ValidateNotNull( "Metadata", md );

         var sec = md.SecurityDefinitions.GetOrNull( index );
         var retVal = sec != null;
         if ( retVal )
         {
            var permissions = sec.PermissionSets;
            for ( var i = 0; i < permissions.Count; ++i )
            {
               var permission = permissions[i] as RawSecurityInformation;
               if ( permission != null )
               {
                  var idx = 0;
                  var bytes = permission.Bytes;
                  var argCount = permission.ArgumentCount;
                  var secInfo = new SecurityInformation( argCount ) { SecurityAttributeType = permission.SecurityAttributeType };
                  var success = true;
                  for ( var j = 0; j < argCount && success; ++j )
                  {
                     var arg = md.SignatureProvider.ReadCANamedArgument( bytes, ref idx, typeStr => this.ResolveTypeFromFullName( md, typeStr ) );
                     if ( arg == null )
                     {
                        success = false;
                     }
                     else
                     {
                        secInfo.NamedArguments.Add( arg );
                     }
                  }

                  if ( success )
                  {
                     permissions[i] = secInfo;
                  }
                  else
                  {
                     retVal = false;
                  }
               }
            }
         }

         return retVal;
      }

      private ResolvedCustomAttributeSignature TryResolveCustomAttributeSignature(
         CILMetaData md,
         Byte[] blob,
         Int32 idx,
         TableIndex caTypeTableIndex
         )
      {

         AbstractMethodSignature ctorSig;
         switch ( caTypeTableIndex.Table )
         {
            case Tables.MethodDef:
               ctorSig = caTypeTableIndex.Index < md.MethodDefinitions.GetRowCount() ?
                  md.MethodDefinitions.TableContents[caTypeTableIndex.Index].Signature :
                  null;
               break;
            case Tables.MemberRef:
               ctorSig = caTypeTableIndex.Index < md.MemberReferences.GetRowCount() ?
                  md.MemberReferences.TableContents[caTypeTableIndex.Index].Signature as AbstractMethodSignature :
                  null;
               break;
            default:
               ctorSig = null;
               break;
         }

         var success = ctorSig != null;
         ResolvedCustomAttributeSignature retVal = null;
         if ( success )
         {
            var startIdx = idx;
            retVal = new ResolvedCustomAttributeSignature( typedArgsCount: ctorSig.Parameters.Count );

            idx += 2; // Skip prolog

            for ( var i = 0; i < ctorSig.Parameters.Count; ++i )
            {
               var caType = md.TypeSignatureToCustomAttributeArgumentType( ctorSig.Parameters[i].Type, tIdx => this.ResolveCATypeFromTableIndex( md, tIdx ) );
               if ( caType == null )
               {
                  // We don't know the size of the type -> stop
                  retVal.TypedArguments.Clear();
                  break;
               }
               else
               {
                  retVal.TypedArguments.Add( md.SignatureProvider.ReadCAFixedArgument( blob, ref idx, caType, typeStr => this.ResolveTypeFromFullName( md, typeStr ) ) );
               }
            }

            // Check if we had failed to resolve ctor type before.
            success = retVal.TypedArguments.Count == ctorSig.Parameters.Count;
            if ( success )
            {
               var namedCount = blob.ReadUInt16LEFromBytes( ref idx );
               for ( var i = 0; i < namedCount && success; ++i )
               {
                  var caNamedArg = md.SignatureProvider.ReadCANamedArgument( blob, ref idx, typeStr => this.ResolveTypeFromFullName( md, typeStr ) );

                  if ( caNamedArg == null )
                  {
                     // We don't know the size of the type -> stop
                     success = false;
                  }
                  else
                  {
                     retVal.NamedArguments.Add( caNamedArg );
                  }
               }
            }
         }
         return success ? retVal : null;
      }

      private MDSpecificCache ResolveAssemblyReferenceWithEvent( CILMetaData thisMD, String assemblyName, AssemblyInformationForResolving assemblyInfo ) //, Boolean isRetargetable )
      {
         var args = new AssemblyReferenceResolveEventArgs( thisMD, assemblyName, assemblyInfo ); //, isRetargetable );
         this.AssemblyReferenceResolveEvent?.Invoke( this, args );
         return this.GetCacheFor( args.ResolvedMetaData );
      }

      private MDSpecificCache ResolveModuleReferenceWithEvent( CILMetaData thisMD, String moduleName )
      {
         var args = new ModuleReferenceResolveEventArgs( thisMD, moduleName );
         this.ModuleReferenceResolveEvent?.Invoke( this, args );
         return this.GetCacheFor( args.ResolvedMetaData );
      }

      private MDSpecificCache ResolveAssemblyByString( CILMetaData md, String assemblyString )
      {
         return this.GetCacheFor( md ).ResolveCacheByAssemblyString( assemblyString );
      }

      private CustomAttributeArgumentTypeSimple ResolveTypeFromFullName( CILMetaData md, String typeString )
      {
         // 1. See if there is assembly name present
         // 2. If present, then resolve assembly by name
         // 3. If not present, then try this assembly and then 'mscorlib'
         // 4. Resolve table index by string
         // 5. Resolve return value by CILMetaData + TableIndex pair

         String typeName, assemblyName;
         var assemblyNamePresent = typeString.ParseAssemblyQualifiedTypeString( out typeName, out assemblyName );
         var targetModule = assemblyNamePresent ? this.ResolveAssemblyByString( md, assemblyName ) : this.GetCacheFor( md );

         var retVal = targetModule == null ? null : targetModule.ResolveTypeFromTypeName( typeName );
         if ( retVal == null && !assemblyNamePresent )
         {
            // TODO try 'mscorlib' unless this is mscorlib
         }

         return retVal;
      }


      private String ResolveTypeNameFromTypeDef( CILMetaData md, Int32 index )
      {
         return this.UseMDCache( md, c => c.ResolveTypeNameFromTypeDef( index ) );
      }

      private String ResolveTypeNameFromTypeRef( CILMetaData md, Int32 index )
      {
         return this.UseMDCache( md, c => c.ResolveTypeNameFromTypeRef( index ) );
      }

      private MDSpecificCache MDSpecificCacheFactory( CILMetaData md )
      {
         return new MDSpecificCache( this, md );
      }

      private MDSpecificCache GetCacheFor( CILMetaData otherMD )
      {
         return otherMD == null ?
            null :
            this._mdCaches.GetOrAdd_NotThreadSafe( otherMD, this._mdCacheFactory );
      }


      private T UseMDCache<T>( CILMetaData md, Func<MDSpecificCache, T> func )
         where T : class
      {
         var cache = this.GetCacheFor( md );
         return cache == null ? null : func( cache );
      }




      private CustomAttributeArgumentTypeEnum ResolveCATypeFromTableIndex(
         CILMetaData md,
         TableIndex tIdx
         )
      {
         var idx = tIdx.Index;
         String retVal;
         switch ( tIdx.Table )
         {
            case Tables.TypeDef:
               retVal = this.ResolveTypeNameFromTypeDef( md, tIdx.Index );
               break;
            case Tables.TypeRef:
               retVal = this.ResolveTypeNameFromTypeRef( md, idx );
               break;
            //case Tables.TypeSpec:
            //   // Should never happen but one never knows...
            //   // Recursion within same metadata:
            //   var tSpec = md.TypeSpecifications.GetOrNull( idx );
            //   retVal = tSpec == null ?
            //      null :
            //      ConvertTypeSignatureToCustomAttributeType( md, tSpec.Signature );
            //   break;
            default:
               retVal = null;
               break;
         }

         return retVal == null ? null : new CustomAttributeArgumentTypeEnum()
         {
            TypeString = retVal
         };
      }


   }

}

#pragma warning disable 1591
public static partial class E_CILPhysical
#pragma warning restore 1591
{
   /// <summary>
   /// Helper method to resolve all custom attributes of given <see cref="CILMetaData"/> using <see cref="MetaDataResolver.ResolveCustomAttributeSignature(CILMetaData, int)"/>.
   /// </summary>
   /// <param name="resolver">This <see cref="MetaDataResolver"/>.</param>
   /// <param name="md">The <see cref="CILMetaData"/>.</param>
   /// <exception cref="NullReferenceException">If this <see cref="MetaDataResolver"/> is <c>null</c>.</exception>
   /// <exception cref="ArgumentNullException">If <paramref name="md"/> is <c>null</c>.</exception>
   public static void ResolveAllCustomAttributes( this MetaDataResolver resolver, CILMetaData md )
   {
      if ( resolver == null )
      {
         throw new NullReferenceException();
      }
      ArgumentValidator.ValidateNotNull( "Meta data", md );
      resolver.UseResolver( md, md.CustomAttributeDefinitions, ( r, m, i ) => r.ResolveCustomAttributeSignature( m, i ) );
   }

   /// <summary>
   /// Helper method to resolve all security signatures of given <see cref="CILMetaData"/> using <see cref="MetaDataResolver.ResolveSecurityDeclaration(CILMetaData, int)"/>.
   /// </summary>
   /// <param name="resolver">This <see cref="MetaDataResolver"/>.</param>
   /// <param name="md">The <see cref="CILMetaData"/>.</param>
   /// <exception cref="NullReferenceException">If this <see cref="MetaDataResolver"/> is <c>null</c>.</exception>
   /// <exception cref="ArgumentNullException">If <paramref name="md"/> is <c>null</c>.</exception>
   public static void ResolveAllSecurityInformation( this MetaDataResolver resolver, CILMetaData md )
   {
      if ( resolver == null )
      {
         throw new NullReferenceException();
      }
      ArgumentValidator.ValidateNotNull( "Meta data", md );
      resolver.UseResolver( md, md.SecurityDefinitions, ( r, m, i ) => r.ResolveSecurityDeclaration( m, i ) );
   }

   /// <summary>
   /// Helper method to resolve custom attribute signatures and security signatures in given <see cref="CILMetaData"/>.
   /// </summary>
   /// <param name="resolver">This <see cref="MetaDataResolver"/>.</param>
   /// <param name="md">The <see cref="CILMetaData"/>.</param>
   /// <exception cref="NullReferenceException">If this <see cref="MetaDataResolver"/> is <c>null</c>.</exception>
   /// <exception cref="ArgumentNullException">If <paramref name="md"/> is <c>null</c>.</exception>
   public static void ResolveEverything( this MetaDataResolver resolver, CILMetaData md )
   {
      resolver.ResolveAllCustomAttributes( md );
      resolver.ResolveAllSecurityInformation( md );
   }

   private static void UseResolver<T>( this MetaDataResolver resolver, CILMetaData md, MetaDataTable<T> list, Action<MetaDataResolver, CILMetaData, Int32> action )
      where T : class
   {

      var max = list.GetRowCount();
      for ( var i = 0; i < max; ++i )
      {
         action( resolver, md, i );
      }
   }

   /// <summary>
   /// This is helper method to search for custom attribute of type <see cref="System.Runtime.Versioning.TargetFrameworkAttribute"/> attribute applied to the assembly, and creates a <see cref="TargetFrameworkInfo"/> based on the information in the custom attribute signature.
   /// </summary>
   /// <param name="md">The <see cref="CILMetaData"/>.</param>
   /// <param name="fwInfo">This parameter will contain the <see cref="TargetFrameworkInfo"/> created based on the information in the assembly.</param>
   /// <param name="resolverToUse">The <see cref="MetaDataResolver"/> to use, if the <see cref="AbstractCustomAttributeSignature"/> of the custom attribute is <see cref="RawCustomAttributeSignature"/>.</param>
   /// <returns><c>true</c> if suitable attribute is found, and the information in the signature is enough to create <see cref="TargetFrameworkInfo"/>; <c>false</c> otherwise.</returns>
   /// <remarks>
   /// <para>
   /// In case of multiple matching custom attributes, the first one in <see cref="CILMetaData.CustomAttributeDefinitions"/> table is used.
   /// </para>
   /// <para>
   /// The assemblies in target framework directory usually don't have the <see cref="System.Runtime.Versioning.TargetFrameworkAttribute"/> on them.
   /// </para>
   /// </remarks>
   /// <exception cref="NullReferenceException">If <paramref name="md"/> is <c>null</c>.</exception>
   public static Boolean TryGetTargetFrameworkInformation( this CILMetaData md, out TargetFrameworkInfo fwInfo, MetaDataResolver resolverToUse = null )
   {
      fwInfo = md.CustomAttributeDefinitions.TableContents
         .Where( ( ca, caIdx ) =>
         {
            var isTargetFWAttribute = false;
            if ( ca.Parent.Table == Tables.Assembly
            && md.AssemblyDefinitions.GetOrNull( ca.Parent.Index ) != null
            && ca.Type.Table == Tables.MemberRef ) // Remember that framework assemblies don't have TargetFrameworkAttribute defined
            {
               var memberRef = md.MemberReferences.GetOrNull( ca.Type.Index );
               if ( memberRef != null
                  && memberRef?.Signature?.SignatureKind == SignatureKind.MethodReference
                  && memberRef.DeclaringType.Table == Tables.TypeRef
                  && String.Equals( memberRef.Name, Miscellaneous.INSTANCE_CTOR_NAME )
                  )
               {
                  var typeRef = md.TypeReferences.GetOrNull( memberRef.DeclaringType.Index );
                  if ( typeRef != null
                     && typeRef.ResolutionScope.HasValue
                     && typeRef.ResolutionScope.Value.Table == Tables.AssemblyRef
                     && String.Equals( typeRef.Namespace, "System.Runtime.Versioning" )
                     && String.Equals( typeRef.Name, "TargetFrameworkAttribute" )
                     )
                  {
                     if ( ca.Signature is RawCustomAttributeSignature )
                     {
                        // Use resolver with no events, so nothing additional will be loaded (and is not required, as both arguments are strings
                        ( resolverToUse ?? new MetaDataResolver() ).ResolveCustomAttributeSignature( md, caIdx );
                     }

                     var caSig = ca.Signature as ResolvedCustomAttributeSignature;
                     if ( caSig != null
                        && caSig.TypedArguments.Count > 0
                        )
                     {
                        // Resolving succeeded
                        isTargetFWAttribute = true;
                     }
#if DEBUG
                     else
                     {
                        // Breakpoint (resolving failed, even though it should have succeeded
                     }
#endif
                  }
               }
            }
            return isTargetFWAttribute;
         } )
         .Select( ca =>
         {

            var fwInfoString = ( (ResolvedCustomAttributeSignature) ca.Signature ).TypedArguments[0].Value.ToStringSafe( null );
            //var displayName = caSig.NamedArguments.Count > 0
            //   && String.Equals( caSig.NamedArguments[0].Name, "FrameworkDisplayName" )
            //   && caSig.NamedArguments[0].Value.Type.IsSimpleTypeOfKind( SignatureElementTypes.String ) ?
            //   caSig.NamedArguments[0].Value.Value.ToStringSafe( null ) :
            //   null;
            TargetFrameworkInfo thisFWInfo;
            return TargetFrameworkInfo.TryParse( fwInfoString, out thisFWInfo ) ? thisFWInfo : null;

         } )
         .FirstOrDefault();

      return fwInfo != null;
   }

   /// <summary>
   /// Wrapper around <see cref="TryGetTargetFrameworkInformation"/>, that will always return <see cref="TargetFrameworkInfo"/>, but it will be <c>null</c> if <see cref="TryGetTargetFrameworkInformation"/> will return <c>false</c>.
   /// </summary>
   /// <param name="md">The <see cref="CILMetaData"/>.</param>
   /// <param name="resolverToUse">The <see cref="MetaDataResolver"/> to use, if the <see cref="AbstractCustomAttributeSignature"/> of the custom attribute is <see cref="RawCustomAttributeSignature"/>.</param>
   /// <returns>The parsed <see cref="TargetFrameworkInfo"/> object, or <c>null</c> if such information could not be found from <paramref name="md"/>.</returns>
   /// <exception cref="NullReferenceException">If <paramref name="md"/> is <c>null</c>.</exception>
   public static TargetFrameworkInfo GetTargetFrameworkInformationOrNull( this CILMetaData md, MetaDataResolver resolverToUse = null )
   {
      TargetFrameworkInfo retVal;
      return md.TryGetTargetFrameworkInformation( out retVal, resolverToUse ) ?
         retVal :
         null;
   }
}