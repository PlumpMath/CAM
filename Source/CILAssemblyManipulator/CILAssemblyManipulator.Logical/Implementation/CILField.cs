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
   internal class CILFieldImpl : CILCustomAttributeContainerImpl, CILField, CILFieldInternal
   {
      internal const Int32 NO_OFFSET = -1;

      private readonly SettableValueForEnums<FieldAttributes> fieldAttributes;
      private readonly SettableValueForClasses<String> name;
      private readonly SettableLazy<Object> constValue;
      private readonly Lazy<CILType> declaringType;
      private readonly ResettableLazy<CILTypeBase> fieldType;
      private readonly SettableValueForClasses<Byte[]> initialValue;
      private readonly Lazy<ListProxy<CILCustomModifier>> customModifiers;
      private readonly SettableLazy<Int32> fieldOffset;
      private readonly SettableLazy<LogicalMarshalingInfo> marshalInfo;

      internal CILFieldImpl(
         CILReflectionContextImpl ctx,
         Int32 anID,
         System.Reflection.FieldInfo field
         )
         : base( ctx, anID, CILElementKind.Field, cb => cb.GetCustomAttributesDataForOrThrow( field ) )
      {
         ArgumentValidator.ValidateNotNull( "Field", field );
         if ( field.DeclaringType
#if WINDOWS_PHONE_APP
            .GetTypeInfo()
#endif
.IsGenericType && !field.DeclaringType
#if WINDOWS_PHONE_APP
            .GetTypeInfo()
#endif
.IsGenericTypeDefinition )
         {
            throw new ArgumentException( "This constructor may be used only on fields declared in genericless types or generic type definitions." );
         }
         Byte[] rvaValue = null;
         if ( ( (FieldAttributes) field.Attributes ).HasRVA() )
         {
            rvaValue = LogicalUtils.ObjectToByteArray( field.GetValue( null ) );
         }

         InitFields(
            ctx,
            ref this.fieldAttributes,
            ref this.name,
            ref this.declaringType,
            ref this.fieldType,
            ref this.constValue,
            ref this.initialValue,
            ref this.customModifiers,
            ref this.fieldOffset,
            ref this.marshalInfo,
            new SettableValueForEnums<FieldAttributes>( (FieldAttributes) field.Attributes ),
            new SettableValueForClasses<String>( field.Name ),
            () => (CILType) ctx.Cache.GetOrAdd( field.DeclaringType ),
            () => ctx.Cache.GetOrAdd( field.FieldType ),
            new SettableLazy<Object>( () => ctx.WrapperCallbacks.GetConstantValueForOrThrow( field ), ctx.LazyThreadSafetyMode ),
            new SettableValueForClasses<Byte[]>( rvaValue ),
            ctx.LaunchEventAndCreateCustomModifiers( field, E_CILLogical.GetCustomModifiersForOrThrow ),
            new SettableLazy<Int32>( () =>
            {
               var offset = field.GetCustomAttributes( true ).OfType<System.Runtime.InteropServices.FieldOffsetAttribute>().FirstOrDefault();
               return offset == null ? -1 : offset.Value;
            }, ctx.LazyThreadSafetyMode ),
            new SettableLazy<LogicalMarshalingInfo>( () =>
            {
#if CAM_LOGICAL_IS_SL
               throw new NotImplementedException( "Not yet implemented in this platform." );
#else
               return LogicalMarshalingInfo.FromAttribute( field.GetCustomAttributes( true ).OfType<System.Runtime.InteropServices.MarshalAsAttribute>().FirstOrDefault(), ctx );
#endif
            }, ctx.LazyThreadSafetyMode ),
            true
            );
      }

      internal CILFieldImpl(
         CILReflectionContextImpl ctx,
         Int32 anID,
         CILType declaringType,
         String name,
         CILTypeBase fieldType,
         FieldAttributes attrs
         )
         : this(
         ctx,
         anID,
         new Lazy<ListProxy<CILCustomAttribute>>( () => ctx.CollectionsFactory.NewListProxy<CILCustomAttribute>(), ctx.LazyThreadSafetyMode ),
         new SettableValueForEnums<FieldAttributes>( attrs ),
         new SettableValueForClasses<String>( name ),
         () => declaringType,
         () => fieldType,
         new SettableLazy<Object>( () => null, ctx.LazyThreadSafetyMode ),
         new SettableValueForClasses<Byte[]>( null ),
         new Lazy<ListProxy<CILCustomModifier>>( () => ctx.CollectionsFactory.NewListProxy<CILCustomModifier>(), ctx.LazyThreadSafetyMode ),
         new SettableLazy<Int32>( () => NO_OFFSET, ctx.LazyThreadSafetyMode ),
         new SettableLazy<LogicalMarshalingInfo>( () => null, ctx.LazyThreadSafetyMode ),
         true
         )
      {

      }

      internal CILFieldImpl(
         CILReflectionContextImpl ctx,
         Int32 anID,
         Lazy<ListProxy<CILCustomAttribute>> cAttrDataFunc,
         SettableValueForEnums<FieldAttributes> fAttributes,
         SettableValueForClasses<String> aName,
         Func<CILType> declaringTypeFunc,
         Func<CILTypeBase> fieldTypeFunc,
         SettableLazy<Object> aConstValue,
         SettableValueForClasses<Byte[]> anInitialValue,
         Lazy<ListProxy<CILCustomModifier>> customModsFunc,
         SettableLazy<Int32> aFieldOffset,
         SettableLazy<LogicalMarshalingInfo> marshalInfoVal,
         Boolean resettablesAreSettable = false
         )
         : base( ctx, CILElementKind.Field, anID, cAttrDataFunc )
      {
         InitFields(
            ctx,
            ref this.fieldAttributes,
            ref this.name,
            ref this.declaringType,
            ref this.fieldType,
            ref this.constValue,
            ref this.initialValue,
            ref this.customModifiers,
            ref this.fieldOffset,
            ref this.marshalInfo,
            fAttributes,
            aName,
            declaringTypeFunc,
            fieldTypeFunc,
            aConstValue,
            anInitialValue,
            customModsFunc,
            aFieldOffset,
            marshalInfoVal,
            resettablesAreSettable
            );
      }

      private static void InitFields(
         CILReflectionContextImpl ctx,
         ref SettableValueForEnums<FieldAttributes> attributes,
         ref SettableValueForClasses<String> name,
         ref Lazy<CILType> declaringType,
         ref ResettableLazy<CILTypeBase> fieldType,
         ref SettableLazy<Object> constValue,
         ref SettableValueForClasses<Byte[]> initialValue,
         ref Lazy<ListProxy<CILCustomModifier>> customModifiers,
         ref SettableLazy<Int32> fieldOffset,
         ref SettableLazy<LogicalMarshalingInfo> marshalInfo,
         SettableValueForEnums<FieldAttributes> fAttributes,
         SettableValueForClasses<String> aName,
         Func<CILType> declaringTypeFunc,
         Func<CILTypeBase> fieldTypeFunc,
         SettableLazy<Object> aConstValue,
         SettableValueForClasses<Byte[]> anInitialValue,
         Lazy<ListProxy<CILCustomModifier>> customModifiersFunc,
         SettableLazy<Int32> aFieldOffset,
         SettableLazy<LogicalMarshalingInfo> marshalInfoVal,
         Boolean resettablesAreSettable
         )
      {
         var lazyThreadSafety = ctx.LazyThreadSafetyMode;
         attributes = fAttributes;
         name = aName;
         declaringType = new Lazy<CILType>( declaringTypeFunc, lazyThreadSafety );
         fieldType = resettablesAreSettable ? new ResettableAndSettableLazy<CILTypeBase>( fieldTypeFunc, lazyThreadSafety ) : new ResettableLazy<CILTypeBase>( fieldTypeFunc, lazyThreadSafety );
         constValue = aConstValue;
         initialValue = anInitialValue;
         customModifiers = customModifiersFunc;
         fieldOffset = aFieldOffset;
         marshalInfo = marshalInfoVal;
      }

      public override String ToString()
      {
         return this.fieldType.Value + " " + this.name;
      }

      #region CILField Members

      public FieldAttributes Attributes
      {
         set
         {
            this.fieldAttributes.Value = value;
         }
         get
         {
            return this.fieldAttributes.Value;
         }
      }

      public CILTypeBase FieldType
      {
         set
         {
            this.ThrowIfNotCapableOfChanging();
            LogicalUtils.CheckTypeForMethodSig( this.declaringType.Value.Module, ref value );
            this.fieldType.Value = value;
            this.context.Cache.ForAllGenericInstancesOf( this, field => field.ResetFieldType() );
         }
         get
         {
            return this.fieldType.Value;
         }
      }

      public Byte[] InitialValue
      {
         set
         {
            this.ThrowIfNotCapableOfChanging();
            if ( value == null )
            {
               this.fieldAttributes.Value &= ~( FieldAttributes.HasFieldRVA );
            }
            else
            {
               this.fieldAttributes.Value |= FieldAttributes.HasFieldRVA;
            }
            this.initialValue.Value = value;
         }
         get
         {
            return this.initialValue.Value;
         }
      }

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

      public Int32 FieldOffset
      {
         set
         {
            this.fieldOffset.Value = value;
         }
         get
         {
            return this.fieldOffset.Value;
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

      #region CILElementOwnedByType Members

      public CILType DeclaringType
      {
         get
         {
            return this.declaringType.Value;
         }
      }

      #endregion

      #region CILFieldInternal Members

      SettableValueForEnums<FieldAttributes> CILFieldInternal.FieldAttributesInternal
      {
         get
         {
            return this.fieldAttributes;
         }
      }

      SettableValueForClasses<String> CILElementWithSimpleNameInternal.NameInternal
      {
         get
         {
            return this.name;
         }
      }

      SettableValueForClasses<byte[]> CILFieldInternal.FieldRVAValue
      {
         get
         {
            return this.initialValue;
         }
      }

      SettableLazy<Int32> CILFieldInternal.FieldOffsetInternal
      {
         get
         {
            return this.fieldOffset;
         }
      }

      void CILFieldInternal.ResetFieldType()
      {
         this.fieldType.Reset();
      }

      SettableLazy<LogicalMarshalingInfo> CILElementWithMarshalInfoInternal.MarshalingInfoInternal
      {
         get
         {
            return this.marshalInfo;
         }
      }

      #endregion

      #region CILElementOwnedByChangeableType<CILField> Members

      public CILField ChangeDeclaringType( params CILTypeBase[] args )
      {
         LogicalUtils.ThrowIfDeclaringTypeNotGeneric( this, args );
         CILField fieldToGive = this;
         CILType dt = this.declaringType.Value;
         if ( dt.GenericDefinition != null )
         {
            fieldToGive = dt.GenericDefinition.DeclaredFields[dt.DeclaredFields.IndexOf( this )];
         }
         return this.context.Cache.MakeFieldWithGenericDeclaringType( fieldToGive, args );
      }

      #endregion

      #region CILElementWithConstant Members

      public object ConstantValue
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

      SettableLazy<Object> CILElementWithConstantValueInternal.ConstantValueInternal
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


      #region CILElementWithMarshalingInfo Members

      public LogicalMarshalingInfo MarshalingInformation
      {
         get
         {
            return this.marshalInfo.Value;
         }
         set
         {
            this.marshalInfo.Value = value;
            if ( value == null )
            {
               this.fieldAttributes.Value &= ~FieldAttributes.HasFieldMarshal;
            }
            else
            {
               this.fieldAttributes.Value |= FieldAttributes.HasFieldMarshal;
            }
         }
      }

      #endregion

      public CILElementWithinILCode ElementTypeKind
      {
         get
         {
            return CILElementWithinILCode.Field;
         }
      }
   }
}