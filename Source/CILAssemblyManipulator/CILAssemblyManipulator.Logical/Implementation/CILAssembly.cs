/*
 * Copyright 2013 Stanislav Muhametsin. All rights Reserved.
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
using System.Linq;
using System.Threading;
using CollectionsWithRoles.API;
using CommonUtils;
using CILAssemblyManipulator.Physical;

namespace CILAssemblyManipulator.Logical.Implementation
{
   internal class CILAssemblyImpl : CILCustomAttributeContainerImpl, CILAssembly
   {
      private readonly WriteableLazy<CILAssemblyName> name;
      private readonly Lazy<ListProxy<CILModule>> modules;
      private readonly Lazy<DictionaryProxy<Tuple<String, String>, TypeForwardingInfo>> forwardedTypes;
      private readonly WriteableLazy<CILModule> mainModule;

      internal CILAssemblyImpl(
         CILReflectionContextImpl ctx,
         Int32 anID,
         System.Reflection.Assembly ass
         )
         : base( ctx, anID, CILElementKind.Assembly, cb => cb.GetCustomAttributesDataForOrThrow( ass ) )
      {
         ArgumentValidator.ValidateNotNull( "Assembly", ass );
         InitFields(
            ctx,
            ref this.name,
            ref this.modules,
            ref this.forwardedTypes,
            ref this.mainModule,
            () =>
            {
               var result = CILAssemblyName.Parse( ass.FullName );

               AssemblyHashAlgorithm algo; AssemblyFlags flags; Byte[] publicKey;
               ctx.WrapperCallbacks.GetAssemblyNameInformationOrThrow( ass, out algo, out flags, out publicKey );
               result.HashAlgorithm = algo;
               result.Flags = flags;
               result.PublicKey = publicKey;
               if ( result.PublicKey.IsNullOrEmpty() )
               {
                  // .NET for some reason returns PublicKey-flag set, even with no public key...
                  result.Flags = result.Flags & ~( AssemblyFlags.PublicKey );
               }
               return result;
            },
            () => ctx.CollectionsFactory.NewListProxy<CILModule>( ass
            .GetModules()
            .Select( module => ctx.Cache.GetOrAdd( module ) )
            .ToList() ),
            () =>
            {
               // TODO seems that getting type forward info is not really possible via C# managed reflection API? 
               throw new NotImplementedException();
            },
            () => ctx.Cache.GetOrAdd( ass.ManifestModule )
            );
      }

      internal CILAssemblyImpl(
         CILReflectionContextImpl ctx,
         Int32 anID
         )
         : base( ctx, CILElementKind.Assembly, anID, new Lazy<ListProxy<CILCustomAttribute>>( () => ctx.CollectionsFactory.NewListProxy<CILCustomAttribute>(), ctx.LazyThreadSafetyMode ) )
      {
         InitFields(
            ctx,
            ref this.name,
            ref this.modules,
            ref this.forwardedTypes,
            ref this.mainModule,
            () => new CILAssemblyName(),
            () => ctx.CollectionsFactory.NewListProxy<CILModule>(),
            () => ctx.CollectionsFactory.NewDictionaryProxy<Tuple<String, String>, TypeForwardingInfo>(),
            () => null
            );
      }

      private static void InitFields(
         CILReflectionContextImpl ctx,
         ref WriteableLazy<CILAssemblyName> name,
         ref Lazy<ListProxy<CILModule>> modules,
         ref Lazy<DictionaryProxy<Tuple<String, String>, TypeForwardingInfo>> forwardedTypes,
         ref WriteableLazy<CILModule> mainModule,
         Func<CILAssemblyName> nameFunc,
         Func<ListProxy<CILModule>> moduleFunc,
         Func<DictionaryProxy<Tuple<String, String>, TypeForwardingInfo>> forwardedTypesFunc,
         Func<CILModule> mainModuleFunc
         )
      {
         var lazyThreadSafety = ctx.LazyThreadSafetyMode;
         name = LazyFactory.NewWriteableLazy( nameFunc, lazyThreadSafety );
         modules = new Lazy<ListProxy<CILModule>>( moduleFunc, lazyThreadSafety );
         forwardedTypes = new Lazy<DictionaryProxy<Tuple<String, String>, TypeForwardingInfo>>( forwardedTypesFunc, lazyThreadSafety );
         mainModule = LazyFactory.NewWriteableLazy( mainModuleFunc, lazyThreadSafety );
      }

      internal override String IsCapableOfChanging()
      {
         // Can always modify assembly
         return null;
      }

      #region CILAssembly Members

      public CILModule AddModule( String name )
      {
         var result = this.context.Cache.NewBlankModule( this, name );
         this.modules.Value.Add( result );
         if ( this.mainModule.Value == null )
         {
            this.mainModule.Value = result;
         }
         return result;
      }

      // TODO if RemoveModule method is added, rememember to clear mainModule if removing same value as main module

      public CILAssemblyName Name
      {
         get
         {
            return this.name.Value;
         }
      }

      public ListQuery<CILModule> Modules
      {
         get
         {
            return this.modules.Value.CQ;
         }
      }

      public CILModule MainModule
      {
         get
         {
            return this.mainModule.Value;
         }
         set
         {
            if ( this.modules.Value.CQ.IndexOf( value ) < 0 )
            {
               throw new ArgumentException( "The given module " + value + " is not part of this assembly." );
            }
            this.mainModule.Value = value;
         }
      }

      public DictionaryQuery<Tuple<String, String>, TypeForwardingInfo> ForwardedTypeInfos
      {
         get
         {
            return this.forwardedTypes.Value.CQ;
         }
      }

      public Boolean TryAddForwardedType( TypeForwardingInfo info )
      {
         return this.forwardedTypes.Value.TryAdd( Tuple.Create( info.Name, info.Namespace ), info );
      }

      public Boolean RemoveForwardedType( String name, String ns )
      {
         return this.forwardedTypes.Value.Remove( Tuple.Create( name, ns ) );
      }

      #endregion

      public override String ToString()
      {
         return this.name.Value.ToString();
      }

      internal Lazy<ListProxy<CILModule>> InternalModules
      {
         get
         {
            return this.modules;
         }
      }
   }
}