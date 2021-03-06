﻿/*
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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CollectionsWithRoles.API;
using CommonUtils;
using CILAssemblyManipulator.Physical;

namespace CILAssemblyManipulator.Logical.Implementation
{
   internal class CILParameterSignatureImpl : AbstractSignatureElement, CILParameterSignature
   {
      private readonly ListQuery<CILCustomModifier> _mods;
      private readonly CILMethodSignature _method;
      private readonly Int32 _position;
      private readonly CILTypeBase _pType;

      internal CILParameterSignatureImpl( CILReflectionContext ctx, CILMethodSignature method, Int32 position, CILTypeBase pType, ListProxy<CILCustomModifier> mods )
         : base( ctx )
      {
         ArgumentValidator.ValidateNotNull( "Method", method );
         ArgumentValidator.ValidateNotNull( "Parameter type", pType );

         this._mods = ( mods ?? this._ctx.CollectionsFactory.NewListProxy<CILCustomModifier>() ).CQ;
         this._method = method;
         this._position = position;
         this._pType = pType;
      }

      #region CILParameterBase<CILMethodSignature> Members

      public CILTypeBase ParameterType
      {
         get
         {
            return this._pType;
         }
      }

      public CILMethodSignature Method
      {
         get
         {
            return this._method;
         }
      }

      public Int32 Position
      {
         get
         {
            return this._position;
         }
      }

      #endregion

      #region CILElementWithCustomModifiers Members

      public ListQuery<CILCustomModifier> CustomModifiers
      {
         get
         {
            return this._mods;
         }
      }

      #endregion

      public override Boolean Equals( Object obj )
      {
         return this.Equals( obj as CILParameterSignature, true );
      }

      public override Int32 GetHashCode()
      {
         return ( this._method.GetHashCode() << 3 ) | this._position;
      }

      internal Boolean Equals( CILParameterSignature other, Boolean compareMethod )
      {
         return Object.ReferenceEquals( this, other )
            || ( other != null
            && this._position == other.Position
            && Object.Equals( this._pType, other.ParameterType )
            && this._mods.SequenceEqual( other.CustomModifiers )
            && ( !compareMethod || this._method.Equals( other.Method ) ) );
      }

      public override String ToString()
      {
         // Don't cache .ToString behind lazy, since param type name might change
         var str = this._pType.ToString();
         if ( this._mods.Count > 0 )
         {
            str += " " + String.Join( " ", (IEnumerable<CILCustomModifier>) this._mods );
         }
         return str;
      }
   }

   internal class CILParameterImpl : CILCustomAttributeContainerImpl, CILParameter, CILParameterInternal
   {
      private readonly SettableValueForEnums<ParameterAttributes> paramAttributes;
      private readonly Int32 position;
      private readonly SettableValueForClasses<String> name;
      private readonly Lazy<CILMethodBase> method;
      private readonly IResettableLazy<CILTypeBase> parameterType;
      private readonly WriteableLazy<Object> defaultValue;
      private readonly Lazy<ListProxy<CILCustomModifier>> customModifiers;
      private readonly WriteableLazy<LogicalMarshalingInfo> marshalInfo;

      internal CILParameterImpl(
         CILReflectionContextImpl ctx,
         Int32 anID,
         System.Reflection.ParameterInfo parameter
         )
         : base( ctx, anID, CILElementKind.Parameter, cb => cb.GetCustomAttributesDataForOrThrow( parameter ) )
      {
         var member = parameter.Member;
         var isCtor = member is System.Reflection.ConstructorInfo;
         InitFields(
            ctx,
            ref this.paramAttributes,
            ref this.position,
            ref this.name,
            ref this.method,
            ref this.parameterType,
            ref this.defaultValue,
            ref this.customModifiers,
            ref this.marshalInfo,
            new SettableValueForEnums<ParameterAttributes>( (ParameterAttributes) parameter.Attributes ),
            parameter.Position,
            new SettableValueForClasses<String>( parameter.Name ),
            () => isCtor ? (CILMethodBase) ctx.Cache.GetOrAdd( (System.Reflection.ConstructorInfo) member ) : ctx.Cache.GetOrAdd( (System.Reflection.MethodInfo) member ),
            () => ctx.Cache.GetOrAdd( parameter.ParameterType ),
            LazyFactory.NewWriteableLazy( () => ctx.WrapperCallbacks.GetConstantValueForOrThrow( parameter ), ctx.LazyThreadSafetyMode ),
            ctx.LaunchEventAndCreateCustomModifiers( parameter, E_CILLogical.GetCustomModifiersForOrThrow ),
            LazyFactory.NewWriteableLazy<LogicalMarshalingInfo>( () =>
            {
#if CAM_LOGICAL_IS_SL
               throw new NotImplementedException( "Not yet implemented in this platform." );
#else
               return LogicalMarshalingInfo.FromAttribute( parameter.GetCustomAttributes( true ).OfType<System.Runtime.InteropServices.MarshalAsAttribute>().FirstOrDefault(), ctx );
#endif
            }, ctx.LazyThreadSafetyMode ),
            true
            );
      }

      internal CILParameterImpl( CILReflectionContextImpl ctx, Int32 anID, CILMethodBase ownerMethod, Int32 position, String name, ParameterAttributes attrs, CILTypeBase paramType )
         : this(
         ctx,
         anID,
         new Lazy<ListProxy<CILCustomAttribute>>( () => ctx.CollectionsFactory.NewListProxy<CILCustomAttribute>(), ctx.LazyThreadSafetyMode ),
         new SettableValueForEnums<ParameterAttributes>( attrs ),
         position,
         new SettableValueForClasses<String>( name ),
         () => ownerMethod,
         () => paramType,
         LazyFactory.NewWriteableLazy<Object>( () => null, ctx.LazyThreadSafetyMode ),
         new Lazy<ListProxy<CILCustomModifier>>( () => ctx.CollectionsFactory.NewListProxy<CILCustomModifier>(), ctx.LazyThreadSafetyMode ),
         LazyFactory.NewWriteableLazy<LogicalMarshalingInfo>( () => null, ctx.LazyThreadSafetyMode ),
         true
         )
      {

      }

      internal CILParameterImpl(
         CILReflectionContextImpl ctx,
         Int32 anID,
         Lazy<ListProxy<CILCustomAttribute>> cAttrDataFunc,
         SettableValueForEnums<ParameterAttributes> aParameterAttributes,
         Int32 aPosition,
         SettableValueForClasses<String> aName,
         Func<CILMethodBase> methodFunc,
         Func<CILTypeBase> parameterTypeFunc,
         WriteableLazy<Object> aDefaultValue,
         Lazy<ListProxy<CILCustomModifier>> customMods,
         WriteableLazy<LogicalMarshalingInfo> marshalInfoVal,
         Boolean resettablesAreSettable = false
         )
         : base( ctx, CILElementKind.Parameter, anID, cAttrDataFunc )
      {
         InitFields(
            ctx,
            ref this.paramAttributes,
            ref this.position,
            ref this.name,
            ref this.method,
            ref this.parameterType,
            ref this.defaultValue,
            ref this.customModifiers,
            ref this.marshalInfo,
            aParameterAttributes,
            aPosition,
            aName,
            methodFunc,
            parameterTypeFunc,
            aDefaultValue,
            customMods,
            marshalInfoVal,
            resettablesAreSettable
            );
      }

      private static void InitFields(
         CILReflectionContextImpl ctx,
         ref SettableValueForEnums<ParameterAttributes> paramAttributes,
         ref Int32 position,
         ref SettableValueForClasses<String> name,
         ref Lazy<CILMethodBase> method,
         ref IResettableLazy<CILTypeBase> parameterType,
         ref WriteableLazy<Object> defaultValue,
         ref Lazy<ListProxy<CILCustomModifier>> customMods,
         ref WriteableLazy<LogicalMarshalingInfo> marshalInfo,
         SettableValueForEnums<ParameterAttributes> aParameterAttributes,
         Int32 aPosition,
         SettableValueForClasses<String> aName,
         Func<CILMethodBase> methodFunc,
         Func<CILTypeBase> parameterTypeFunc,
         WriteableLazy<Object> aDefaultValue,
         Lazy<ListProxy<CILCustomModifier>> theCustomMods,
         WriteableLazy<LogicalMarshalingInfo> marshalInfoVal,
         Boolean resettablesAreSettable
         )
      {
         var lazyThreadSafety = ctx.LazyThreadSafetyMode;
         paramAttributes = aParameterAttributes;
         position = aPosition;
         name = aName;
         method = new Lazy<CILMethodBase>( methodFunc, lazyThreadSafety );
         parameterType = LazyFactory.NewResettableLazy( resettablesAreSettable, parameterTypeFunc, lazyThreadSafety );
         defaultValue = aDefaultValue;
         customMods = theCustomMods;
         marshalInfo = marshalInfoVal;
      }

      public override String ToString()
      {
         return this.parameterType.Value + " " + this.name;
      }

      #region CILParameter Members

      public CILMethodBase Method
      {
         get
         {
            return this.method.Value;
         }
      }

      public ParameterAttributes Attributes
      {
         set
         {
            this.paramAttributes.Value = value;
         }
         get
         {
            return this.paramAttributes.Value;
         }
      }

      public CILTypeBase ParameterType
      {
         set
         {
            var lazy = this.ThrowIfNotCapableOfChanging( this.parameterType );
            LogicalUtils.CheckTypeForMethodSig( this.method.Value.DeclaringType.Module, ref value );
            lazy.Value = value;
            this.context.Cache.ForAllGenericInstancesOf<CILMethodBase, CILMethodBaseInternal>( this.method.Value, method => ( (CILParameterInternal) ( this.position == E_CILLogical.RETURN_PARAMETER_POSITION ? ( (CILMethod) method ).ReturnParameter : ( (CILMethodBase) method ).Parameters[this.position] ) ).ResetParameterType() );
         }
         get
         {
            return this.parameterType.Value;
         }
      }

      public Int32 Position
      {
         get
         {
            return this.position;
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

      #region CILParameterInternal Members

      SettableValueForEnums<ParameterAttributes> CILParameterInternal.ParameterAttributesInternal
      {
         get
         {
            return this.paramAttributes;
         }
      }

      Int32 CILParameterInternal.ParameterPositionInternal
      {
         get
         {
            return this.position;
         }
      }

      void CILParameterInternal.ResetParameterType()
      {
         this.parameterType.Reset();
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

      #region CILElementWithConstant Members

      public Object ConstantValue
      {
         set
         {
            this.defaultValue.Value = value;
            this.paramAttributes.Value = this.paramAttributes.Value | ParameterAttributes.HasDefault;
         }
         get
         {
            return this.defaultValue.Value;
         }
      }

      #endregion

      #region CILElementWithConstantValueInternal Members

      WriteableLazy<Object> CILElementWithConstantValueInternal.ConstantValueInternal
      {
         get
         {
            return this.defaultValue;
         }
      }

      #endregion

      internal override String IsCapableOfChanging()
      {
         return ( (CommonFunctionality) this.method.Value ).IsCapableOfChanging();
      }

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
               this.paramAttributes.Value &= ~ParameterAttributes.HasFieldMarshal;
            }
            else
            {
               this.paramAttributes.Value |= ParameterAttributes.HasFieldMarshal;
            }
         }
      }

      #endregion

      #region CILElementWithMarshalInfoInternal Members

      WriteableLazy<LogicalMarshalingInfo> CILElementWithMarshalInfoInternal.MarshalingInfoInternal
      {
         get
         {
            return this.marshalInfo;
         }
      }

      #endregion
   }
}