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
using CollectionsWithRoles.API;
using CommonUtils;
using CILAssemblyManipulator.Physical;
using System.Threading;

namespace CILAssemblyManipulator.Logical.Implementation
{
   internal class CILPropertyImpl : CILCustomAttributeContainerImpl, CILProperty, CILPropertyInternal
   {
      private readonly SettableValueForClasses<String> name;
      private readonly SettableValueForEnums<PropertyAttributes> propertyAttributes;
      private readonly IResettableLazy<CILMethod> setMethod;
      private readonly IResettableLazy<CILMethod> getMethod;
      private readonly WriteableLazy<CILType> declaringType;
      private readonly WriteableLazy<Object> constValue;
      private readonly Lazy<ListProxy<CILCustomModifier>> customModifiers;

      internal CILPropertyImpl(
         CILReflectionContextImpl ctx,
         Int32 anID,
         System.Reflection.PropertyInfo pInfo )
         : base( ctx, anID, CILElementKind.Property, cb => cb.GetCustomAttributesDataForOrThrow( pInfo ) )
      {
         ArgumentValidator.ValidateNotNull( "Property", pInfo );

         if ( pInfo.DeclaringType
#if WINDOWS_PHONE_APP
            .GetTypeInfo()
#endif
.IsGenericType && !pInfo.DeclaringType
#if WINDOWS_PHONE_APP
            .GetTypeInfo()
#endif
.IsGenericTypeDefinition )
         {
            throw new ArgumentException( "This constructor may be used only on properties declared in genericless types or generic type definitions." );
         }

         InitFields(
            ctx,
            ref this.name,
            ref this.propertyAttributes,
            ref this.setMethod,
            ref this.getMethod,
            ref this.declaringType,
            ref this.constValue,
            ref this.customModifiers,
            new SettableValueForClasses<String>( pInfo.Name ),
            new SettableValueForEnums<PropertyAttributes>( (PropertyAttributes) pInfo.Attributes ),
            () => ctx.Cache.GetOrAdd( pInfo.GetSetMethod( true ) ),
            () => ctx.Cache.GetOrAdd( pInfo.GetGetMethod( true ) ),
            () => (CILType) ctx.Cache.GetOrAdd( pInfo.DeclaringType ),
            LazyFactory.NewWriteableLazy( () => ctx.WrapperCallbacks.GetConstantValueForOrThrow( pInfo ), ctx.LazyThreadSafetyMode ),
            ctx.LaunchEventAndCreateCustomModifiers( pInfo, E_CILLogical.GetCustomModifiersForOrThrow ),
            true
            );
      }

      internal CILPropertyImpl( CILReflectionContextImpl ctx, Int32 anID, CILType declaringType, String aName, PropertyAttributes aPropertyAttributes )
         : this(
         ctx,
         anID,
         new Lazy<ListProxy<CILCustomAttribute>>( () => ctx.CollectionsFactory.NewListProxy<CILCustomAttribute>(), ctx.LazyThreadSafetyMode ),
         new SettableValueForClasses<String>( aName ),
         new SettableValueForEnums<PropertyAttributes>( aPropertyAttributes ),
         () => null,
         () => null,
         () => declaringType,
         LazyFactory.NewWriteableLazy<Object>( () => null, ctx.LazyThreadSafetyMode ),
         new Lazy<ListProxy<CILCustomModifier>>( () => ctx.CollectionsFactory.NewListProxy<CILCustomModifier>(), ctx.LazyThreadSafetyMode ),
         true
         )
      {

      }


      internal CILPropertyImpl(
         CILReflectionContextImpl ctx,
         Int32 anID,
         Lazy<ListProxy<CILCustomAttribute>> cAttrDataFunc,
         SettableValueForClasses<String> aName,
         SettableValueForEnums<PropertyAttributes> aPropertyAttributes,
         Func<CILMethod> setMethodFunc,
         Func<CILMethod> getMethodFunc,
         Func<CILType> declaringTypeFunc,
         WriteableLazy<Object> aConstValue,
         Lazy<ListProxy<CILCustomModifier>> customMods,
         Boolean resettablesAreSettable = false
         )
         : base( ctx, CILElementKind.Property, anID, cAttrDataFunc )
      {
         InitFields(
            ctx,
            ref this.name,
            ref this.propertyAttributes,
            ref this.setMethod,
            ref this.getMethod,
            ref this.declaringType,
            ref this.constValue,
            ref this.customModifiers,
            aName,
            aPropertyAttributes,
            setMethodFunc,
            getMethodFunc,
            declaringTypeFunc,
            aConstValue,
            customMods,
            resettablesAreSettable
            );
      }

      private static void InitFields(
         CILReflectionContextImpl ctx,
         ref SettableValueForClasses<String> name,
         ref SettableValueForEnums<PropertyAttributes> propertyAttributes,
         ref IResettableLazy<CILMethod> setMethod,
         ref IResettableLazy<CILMethod> getMethod,
         ref WriteableLazy<CILType> declaringType,
         ref WriteableLazy<Object> constValue,
         ref Lazy<ListProxy<CILCustomModifier>> customModifiers,
         SettableValueForClasses<String> aName,
         SettableValueForEnums<PropertyAttributes> aPropertyAttributes,
         Func<CILMethod> setMethodFunc,
         Func<CILMethod> getMethodFunc,
         Func<CILType> declaringTypeFunc,
         WriteableLazy<Object> aConstValue,
         Lazy<ListProxy<CILCustomModifier>> customMods,
         Boolean resettablesAreSettable
         )
      {
         var lazyThreadSafety = ctx.LazyThreadSafetyMode;
         name = aName;
         propertyAttributes = aPropertyAttributes;
         setMethod = LazyFactory.NewResettableLazy( resettablesAreSettable, setMethodFunc, lazyThreadSafety );
         getMethod = LazyFactory.NewResettableLazy( resettablesAreSettable, getMethodFunc, lazyThreadSafety );
         declaringType = LazyFactory.NewWriteableLazy( declaringTypeFunc, lazyThreadSafety );
         constValue = aConstValue;
         customModifiers = customMods;
      }

      #region CILProperty Members

      public CILMethod GetMethod
      {
         set
         {
            var lazy = this.ThrowIfNotCapableOfChanging( this.getMethod );
            value.ThrowIfNotTrueDefinition();
            lazy.Value = value;
            this.context.Cache.ForAllGenericInstancesOf( this, prop => prop.ResetGetMethod() );
         }
         get
         {
            return this.getMethod.Value;
         }
      }

      public CILMethod SetMethod
      {
         set
         {
            var lazy = this.ThrowIfNotCapableOfChanging( this.setMethod );
            value.ThrowIfNotTrueDefinition();
            lazy.Value = value;
            this.context.Cache.ForAllGenericInstancesOf( this, prop => prop.ResetSetMethod() );
         }
         get
         {
            return this.setMethod.Value;
         }
      }

      #endregion

      #region CILElementWithAttributes<PropertyAttributes> Members

      public PropertyAttributes Attributes
      {
         set
         {
            this.propertyAttributes.Value = value;
         }
         get
         {
            return this.propertyAttributes.Value;
         }
      }

      #endregion

      #region CILElementOwnedByType Members

      public CILType DeclaringType
      {
         get
         {
            return this.declaringType.Value;
         }
      }

      #endregion

      #region CILElementWithSimpleName Members

      public String Name
      {
         set
         {
            this.name.Value = value;
         }
         get
         {
            return this.name.Value;
         }
      }

      #endregion

      #region CILPropertyInternal Members

      SettableValueForEnums<PropertyAttributes> CILPropertyInternal.PropertyAttributesInternal
      {
         get
         {
            return this.propertyAttributes;
         }
      }

      void CILPropertyInternal.ResetGetMethod()
      {
         this.getMethod.Reset();
      }

      void CILPropertyInternal.ResetSetMethod()
      {
         this.setMethod.Reset();
      }

      #endregion

      #region CILElementWithSimpleNameInternal Members

      SettableValueForClasses<String> CILElementWithSimpleNameInternal.NameInternal
      {
         get
         {
            return this.name;
         }
      }

      #endregion

      #region CILElementOwnedByChangeableType<CILProperty> Members

      public CILProperty ChangeDeclaringType( params CILTypeBase[] args )
      {
         LogicalUtils.ThrowIfDeclaringTypeNotGeneric( this, args );
         CILProperty propToGive = this;
         CILType dt = this.declaringType.Value;
         if ( dt.GenericDefinition != null )
         {
            propToGive = dt.GenericDefinition.DeclaredProperties[dt.DeclaredProperties.IndexOf( this )];
         }
         return this.context.Cache.MakePropertyWithGenericType( propToGive, args );
      }

      #endregion

      #region CILElementWithConstant Members

      public Object ConstantValue
      {
         set
         {
            this.constValue.Value = value;
         }
         get
         {
            return this.constValue.Value;
         }
      }

      #endregion

      #region CILElementWithConstantValueInternal Members

      WriteableLazy<Object> CILElementWithConstantValueInternal.ConstantValueInternal
      {
         get
         {
            return this.constValue;
         }
      }

      #endregion

      internal override String IsCapableOfChanging()
      {
         return ( (CommonFunctionality) this.declaringType.Value ).IsCapableOfChanging();
      }

      #region CILElementInstantiable Members

      public Boolean IsTrueDefinition
      {
         get
         {
            return this.IsCapableOfChanging() == null;
         }
      }

      #endregion

      #region CILElementWithCustomModifiersInternal Members

      Lazy<ListProxy<CILCustomModifier>> CILElementWithCustomModifiersInternal.CustomModifierList
      {
         get
         {
            return this.customModifiers;
         }
      }

      #endregion

      #region CILElementWithCustomModifiers Members

      public CILCustomModifier AddCustomModifier( CILType type, Boolean isOptional )
      {
         var result = new CILCustomModifierImpl( isOptional, type );
         this.customModifiers.Value.Add( result );
         return result;
      }

      public Boolean RemoveCustomModifier( CILCustomModifier modifier )
      {
         return this.customModifiers.Value.Remove( modifier );
      }

      public ListQuery<CILCustomModifier> CustomModifiers
      {
         get
         {
            return this.customModifiers.Value.CQ;
         }
      }

      #endregion

   }
}