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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CommonUtils;

namespace CILAssemblyManipulator.Physical
{
   public interface CILModuleData
   {
      HeadersData Headers { get; }
      CILMetaData MetaData { get; }
   }

   public interface HeadersData
   {

   }

   public interface CILMetaData
   {
      IList<ModuleDefinition> ModuleDefinitions { get; }

      IList<TypeReference> TypeReferences { get; }

      IList<TypeDefinition> TypeDefinitions { get; }

      IList<FieldDefinition> FieldDefinitions { get; }

      IList<MethodDefinition> MethodDefinitions { get; }

      IList<ParameterDefinition> ParameterDefinitions { get; }

      IList<InterfaceImplementation> InterfaceImplementations { get; }

      IList<MemberReference> MemberReferences { get; }

      IList<ConstantDefinition> ConstantDefinitions { get; }

      IList<CustomAttributeDefinition> CustomAttributeDefinitions { get; }

      IList<FieldMarshal> FieldMarshals { get; }

      IList<SecurityDefinition> SecurityDefinitions { get; }

      IList<ClassLayout> ClassLayouts { get; }

      IList<FieldLayout> FieldLayouts { get; }

      IList<StandaloneSignature> StandaloneSignatures { get; }

      IList<EventMap> EventMaps { get; }

      IList<EventDefinition> EventDefinitions { get; }

      IList<PropertyMap> PropertyMaps { get; }

      IList<PropertyDefinition> PropertyDefinitions { get; }

      IList<MethodSemantics> MethodSemantics { get; }

      IList<MethodImplementation> MethodImplementations { get; }

      IList<ModuleReference> ModuleReferences { get; }

      IList<TypeSpecification> TypeSpecifications { get; }

      IList<MethodImplementationMap> MethodImplementationMaps { get; }

      IList<FieldRVA> FieldRVAs { get; }

      IList<AssemblyDefinition> AssemblyDefinitions { get; }

      IList<AssemblyReference> AssemblyReferences { get; }

      IList<FileReference> FileReferences { get; }

      IList<ExportedType> ExportedTypess { get; }

      IList<ManifestResource> ManifestResources { get; }

      IList<NestedClassDefinition> NestedClassDefinitions { get; }

      IList<GenericParameterDefinition> GenericParameterDefinitions { get; }

      IList<MethodSpecification> MethodSpecifications { get; }

      IList<GenericParameterConstraintDefinition> GenericParameterConstraintDefinitions { get; }
   }

   public sealed class ModuleLoadingArguments
   {
      public event EventHandler<CustomAttributeConstructorTypeResolveEventArgs> CustomAttributeConstructorResolveEvent;

      internal CustomAttributeArgumentType ResolveCustomAttributeConstructorArgumentType( ClassOrValueTypeSignature type )
      {
         var args = new CustomAttributeConstructorTypeResolveEventArgs( type );
         this.CustomAttributeConstructorResolveEvent.InvokeEventIfNotNull( evt => evt( this, args ) );
         return args.ResolvedReference;
      }
   }

   public sealed class CustomAttributeConstructorTypeResolveEventArgs : EventArgs
   {
      private readonly ClassOrValueTypeSignature _type;

      internal CustomAttributeConstructorTypeResolveEventArgs( ClassOrValueTypeSignature referenceToResolve )
      {
         ArgumentValidator.ValidateNotNull( "Member reference", referenceToResolve );

         this._type = referenceToResolve;
      }

      public ClassOrValueTypeSignature ReferenceToResolve
      {
         get
         {
            return this._type;
         }
      }

      public CustomAttributeArgumentType ResolvedReference { get; set; }
   }

   public static class CILModuleIO
   {
      public static CILMetaData ReadModule( ModuleLoadingArguments loadingArgs, Stream stream )
      {
         HeadersData headers;
         return ReadModule( loadingArgs, stream, out headers );
      }

      public static CILMetaData ReadModule( ModuleLoadingArguments loadingArgs, Stream stream, out HeadersData headers )
      {
         return CILAssemblyManipulator.Physical.Implementation.ModuleReader.ReadFromStream( loadingArgs, stream, out headers );
      }
   }
}
