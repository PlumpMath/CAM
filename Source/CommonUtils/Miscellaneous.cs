﻿/*
 * Copyright 2014 Stanislav Muhametsin. All rights Reserved.
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
using System.Text;
using CommonUtils;
using System.Threading;

namespace CommonUtils
{

#if SILVERLIGHT
   /// <summary>
   /// This is helper class to provide similar functionality to <see cref="T:System.Threading.ThreadLocal`1"/>, which is missing in Silverlight.
   /// </summary>
   /// <typeparam name="T">The type of the values to hold.</typeparam>
   public sealed class ThreadLocal<T>
   {
      // Helper class to hold values, so T typeparam wouldn't have any generic constraints.
      private sealed class ValueHolder
      {
         internal T _value;
         internal ValueHolder( T value )
         {
            this._value = value;
         }
      }

      private static readonly Func<T> _defaultFactory = () => default( T );

      // Table holding all instances of ThreadLocals in this thread. Since they are weak references, they should get GC'd without big issues.
      [ThreadStatic]
      private static System.Runtime.CompilerServices.ConditionalWeakTable<ThreadLocal<T>, ValueHolder> _table;

      // Factory callback
      private readonly Func<T> _factory;

      /// <summary>
      /// Creates a new instance of <see cref="ThreadLocal{T}"/> with optional factory callback.
      /// </summary>
      /// <param name="factory">The optional factory callback. If not supplied (i.e. is <c>null</c>), then a factory callback will return default value ofr type <typeparamref name="T"/>.</param>
      public ThreadLocal( Func<T> factory = null )
      {
         this._factory = factory ?? _defaultFactory;
      }

      /// <summary>
      /// Gets or sets value that this <see cref="ThreadLocal{T}"/> holds in current thread.
      /// </summary>
      /// <value>The value that this <see cref="ThreadLocal{T}"/> holds in current thread.</value>
      public T Value
      {
         get
         {
            ValueHolder holder;
            T retVal;
            if ( _table != null && _table.TryGetValue( this, out holder ) )
            {
               retVal = holder._value;
            }
            else
            {
               retVal = this._factory();
               this.Value = retVal;
            }
            return retVal;
         }
         set
         {
            if ( _table == null )
            {
               _table = new System.Runtime.CompilerServices.ConditionalWeakTable<ThreadLocal<T>, ValueHolder>();
            }
            _table.GetOrCreateValue( this )._value = value;
         }
      }
   }
#endif

}

public static partial class E_CommonUtils
{
   private const Int32 NO_TIMEOUT = -1;
   private const Int32 DEFAULT_TICK = 50;

   /// <summary>
   /// Disposes the given <paramref name="disposable"/> without leaking any exceptions.
   /// </summary>
   /// <param name="disposable">The <see cref="IDisposable"/> to call <see cref="IDisposable.Dispose"/> method on. May be <c>null</c>, in which case, nothing is done.</param>
   public static void DisposeSafely( this IDisposable disposable )
   {
      if ( disposable != null )
      {
         try
         {
            disposable.Dispose();
         }
         catch
         {
            // Ignore
         }
      }
   }

   /// <summary>
   /// Disposes the given <paramref name="disposable"/> without leaking any exceptions, but giving out occurred exception, if any.
   /// </summary>
   /// <param name="disposable">The <see cref="IDisposable"/> to call <see cref="IDisposable.Dispose"/> method on. May be <c>null</c>, in which case, nothing is done.</param>
   /// <param name="exception">Will hold an exception thrown by <see cref="IDisposable.Dispose"/> method, if method is invoked and it throws.</param>
   /// <returns><c>true</c> if NO exception occurred; <c>false</c> otherwise.</returns>
   public static Boolean DisposeSafely( this IDisposable disposable, out Exception exception )
   {
      exception = null;
      if ( disposable != null )
      {
         try
         {
            disposable.Dispose();
         }
         catch ( Exception exc )
         {
            exception = exc;
         }
      }

      return exception == null;
   }

   /// <summary>
   /// Checks whether given nullable boolean has value and that value is <c>true</c>.
   /// </summary>
   /// <param name="nullable">Nullable boolean.</param>
   /// <returns><c>true</c> if <paramref name="nullable"/> has value and that value is <c>true</c>; <c>false</c> otherwise.</returns>
   public static Boolean IsTrue( this Boolean? nullable )
   {
      return nullable.HasValue && nullable.Value;
   }

   /// <summary>
   /// Checks whether given nullable boolean has value and that value is <c>false</c>.
   /// </summary>
   /// <param name="nullable">Nullable boolean.</param>
   /// <returns><c>true</c> if <paramref name="nullable"/> has value and that value is <c>false</c>; <c>false</c> otherwise.</returns>
   public static Boolean IsFalse( this Boolean? nullable )
   {
      return nullable.HasValue && !nullable.Value;
   }

   /// <summary>
   /// Checks that string is non-<c>null</c> and equivalent to <see cref="Boolean.TrueString"/>.
   /// </summary>
   /// <param name="str">The string to check.</param>
   /// <returns><c>true</c> if <paramref name="str"/> is equivalent to <see cref="Boolean.TrueString"/>; <c>false</c> otherwise.</returns>
   /// <remarks>
   /// This should be a no-throw method.
   /// </remarks>
   private static Boolean ParseAsBooleanSafe( this String str )
   {
      Boolean parsedBoolean;
      return Boolean.TryParse( str, out parsedBoolean ) && parsedBoolean;
   }

   /// <summary>
   /// Helper method to return result of <see cref="Object.ToString"/> or other string if object is <c>null</c>.
   /// </summary>
   /// <param name="obj">The object.</param>
   /// <param name="nullString">The string to return if <paramref name="obj"/> is <c>null</c>.</param>
   /// <returns>The result of <see cref="Object.ToString"/> if <paramref name="obj"/> is not <c>null</c>, <paramref name="nullString"/> otherwise.</returns>
   public static String ToStringSafe<T>( this T obj, String nullString = "" )
      where T : class
   {
      return obj == null ? nullString : obj.ToString();
   }

   /// <summary>
   /// Helper method to return string value of <see cref="Nullable{T}.Value"/> or custom string if the nullable does not have a value.
   /// </summary>
   /// <typeparam name="T">The nullable value type.</typeparam>
   /// <param name="obj">The nullable struct.</param>
   /// <param name="nullString">The string to return if <paramref name="obj"/> does not have a value.</param>
   /// <returns>The string of the nullable value or <paramref name="nullString"/> if <paramref name="obj"/> does not have a value.</returns>
   public static String ToStringSafe<T>( this T? obj, String nullString = "" )
      where T : struct
   {
      return obj.HasValue ? obj.Value.ToString() : nullString;
   }

   /// <summary>
   /// Helper method to return result of <see cref="Object.GetHashCode"/> or custom hash code if object is <c>null</c>.
   /// </summary>
   /// <param name="obj">The object.</param>
   /// <param name="nullHashCode">The hash code to return if <paramref name="obj"/> is <c>null</c>.</param>
   /// <returns>The result of <see cref="Object.GetHashCode"/> if <paramref name="obj"/> is not <c>null</c>, <paramref name="nullHashCode"/> otherwise.</returns>
   public static Int32 GetHashCodeSafe<T>( this T obj, Int32 nullHashCode = 0 )
      where T : class
   {
      return obj == null ? nullHashCode : obj.GetHashCode();
   }

   /// <summary>
   /// Helper method to return hash code of <see cref="Nullable{T}.Value"/> or custom hash code if the nullable does not have a value.
   /// </summary>
   /// <typeparam name="T">The nullable value type.</typeparam>
   /// <param name="obj">The nullable struct.</param>
   /// <param name="nullHashCode">The hash code to return if <paramref name="obj"/> does not have a value.</param>
   /// <returns>The hash code of the nullable value or <paramref name="nullHashCode"/> if <paramref name="obj"/> does not have a value.</returns>
   public static Int32 GetHashCodeSafe<T>( this T? obj, Int32 nullHashCode = 0 )
      where T : struct
   {
      return obj.HasValue ? obj.Value.GetHashCode() : nullHashCode;
   }

   /// <summary>
   /// Helper method to get the type of object or <c>null</c> if object is <c>null</c>.
   /// </summary>
   /// <typeparam name="T">The type of object reference.</typeparam>
   /// <param name="obj">The object.</param>
   /// <returns>The type of <paramref name="obj"/>, or <c>null</c> if <paramref name="obj"/> is <c>null</c>.</returns>
   public static Type GetTypeSafe<T>( this T obj )
      where T : class
   {
      return obj == null ? null : obj.GetType();
   }

   /// <summary>
   /// Helper method to get the type of <see cref="Nullable{T}.Value"/> or <c>null</c> if the nullable does not have a value.
   /// </summary>
   /// <typeparam name="T">The nullable value type.</typeparam>
   /// <param name="obj">The nullable struct.</param>
   /// <returns>The type of nullable value, or <c>null</c> if <paramref name="obj"/> does not have a value.</returns>
   public static Type GetTypeSafe<T>( this T? obj )
      where T : struct
   {
      return obj.HasValue ? obj.Value.GetType() : null;
   }

   ///// <summary>
   ///// Helper method to get the value from nullable, if it has a value, or return default value for type, if the nullable does not have a value.
   ///// </summary>
   ///// <typeparam name="T">The nullable type.</typeparam>
   ///// <param name="nullable">The nullable value.</param>
   ///// <returns><see cref="Nullable{T}.Value"/> if <see cref="Nullable{T}.HasValue"/> is <c>true</c> for <paramref name="nullable"/>; otherwise default value for <typeparamref name="T"/>.</returns>
   //public static T GetValueOrDefault<T>( this T? nullable )
   //   where T : struct
   //{
   //   return nullable.HasValue ? nullable.Value : default( T );
   //}


   /// <summary>
   /// Gets the multiplexed value for given key.
   /// </summary>
   /// <param name="multiplexer">The <see cref="Multiplexer{T, U}"/>.</param>
   /// <param name="key">The key.</param>
   /// <returns>The multiplexed value for <paramref name="key"/>, or default value of type <typeparamref name="TValue"/> nothing is multiplexed for <paramref name="key"/>.</returns>
   /// <typeparam name="TKey">The key type of the <see cref="Multiplexer{T, U}"/>.</typeparam>
   /// <typeparam name="TValue">The value type of the <see cref="Multiplexer{T, U}"/>.</typeparam>
   public static TValue GetMultiplexedValueOrDefault<TKey, TValue>( this Multiplexer<TKey, TValue> multiplexer, TKey key )
   {
      TValue retVal;
      return multiplexer.TryGetMultiplexedValue( key, out retVal ) ? retVal : default( TValue );
   }

   /// <summary>
   /// This is helper method to wait for specific <see cref="WaitHandle"/>, while keeping an eye for cancellation signalled through optional <see cref="CancellationToken"/>.
   /// </summary>
   /// <param name="evt">The <see cref="WaitHandle"/> to wait for.</param>
   /// <param name="token">The optional <see cref="CancellationToken"/> to use when checking for cancellation.</param>
   /// <param name="timeout">The optional maximum time to wait. By default no timeout.</param>
   /// <param name="tick">The optional tick, in milliseconds, between checks for <paramref name="evt"/>. By default is 50 milliseconds.</param>
   /// <returns><c>true</c> if given <paramref name="evt"/> was set during waiting perioud; <c>false</c> otherwise.</returns>
   public static Boolean WaitWhileKeepingEyeForCancel( this WaitHandle evt, CancellationToken token = default( CancellationToken ), Int32 timeout = NO_TIMEOUT, Int32 tick = DEFAULT_TICK )
   {
      Int32 timeWaited = 0;
      while ( !token.IsCancellationRequested && !evt.WaitOne( tick ) && ( timeout < 0 || timeWaited < timeout ) )
      {
         timeWaited += tick;
      }
      return !token.IsCancellationRequested && ( timeout < 0 || timeWaited < timeout );
   }
#if !SILVERLIGHT

   /// <summary>
   /// This is helper method to wait for specific <see cref="ManualResetEventSlim"/>, while keeping an eye for cancellation signalled through optional <see cref="CancellationToken"/>.
   /// </summary>
   /// <param name="evt">The <see cref="ManualResetEventSlim"/> to wait for.</param>
   /// <param name="token">The optional <see cref="CancellationToken"/> to use when checking for cancellation.</param>
   /// <param name="timeout">The optional maximum time to wait. By default no timeout.</param>
   /// <param name="tick">The optional tick, in milliseconds, between checks for <paramref name="evt"/>. By default is 50 milliseconds.</param>
   /// <returns><c>true</c> if given <paramref name="evt"/> was set during waiting perioud; <c>false</c> otherwise.</returns>
   /// <remarks>
   /// Unlike <see cref="ManualResetEventSlim.Wait(Int32, CancellationToken)"/>, this method will not throw <see cref="OperationCanceledException"/> if cancellation is requested. Instead, this method will return <c>false</c>.
   /// </remarks>
   public static Boolean WaitWhileKeepingEyeForCancel( this ManualResetEventSlim evt, CancellationToken token, Int32 timeout = NO_TIMEOUT, Int32 tick = DEFAULT_TICK )
   {
      Int32 timeWaited = 0;
      while ( !token.IsCancellationRequested && !evt.Wait( tick ) && ( timeout < 0 || timeWaited < timeout ) )
      {
         timeWaited += tick;
      }
      return !token.IsCancellationRequested && ( timeout < 0 || timeWaited < timeout );
   }

#endif

   /// <summary>
   /// This is utility method to truncate given <see cref="DateTime"/> to a certain precision.
   /// </summary>
   /// <param name="dateTime">The <see cref="DateTime"/> to truncate.</param>
   /// <param name="timeSpan">The precision to use.</param>
   /// <returns>Truncated <see cref="DateTime"/>.</returns>
   /// <remarks>
   /// The code is from <see href="http://stackoverflow.com/questions/1004698/how-to-truncate-milliseconds-off-of-a-net-datetime"/>.
   /// </remarks>
   public static DateTime Truncate( this DateTime dateTime, TimeSpan timeSpan )
   {
      return TimeSpan.Zero == timeSpan ? dateTime : dateTime.AddTicks( -( dateTime.Ticks % timeSpan.Ticks ) );
   }

   /// <summary>
   /// Helper method to call <see cref="Truncate"/> with required argument for truncating given <see cref="DateTime"/> to whole milliseconds.
   /// </summary>
   /// <param name="dateTime">The <see cref="DateTime"/> to truncate.</param>
   /// <returns>a <see cref="DateTime"/> truncated to whole milliseconds.</returns>
   public static DateTime TruncateToWholeMilliseconds( this DateTime dateTime )
   {
      return dateTime.Truncate( TimeSpan.FromTicks( TimeSpan.TicksPerMillisecond ) );
   }

   /// <summary>
   /// Helper method to call <see cref="Truncate"/> with required argument for truncating given <see cref="DateTime"/> to whole seconds.
   /// </summary>
   /// <param name="dateTime">The <see cref="DateTime"/> to truncate.</param>
   /// <returns>a <see cref="DateTime"/> truncated to whole seconds.</returns>
   public static DateTime TruncateToWholeSeconds( this DateTime dateTime )
   {
      return dateTime.Truncate( TimeSpan.FromTicks( TimeSpan.TicksPerSecond ) );
   }

   /// <summary>
   /// Helper method to call <see cref="Truncate"/> with required argument for truncating given <see cref="DateTime"/> to whole minutes.
   /// </summary>
   /// <param name="dateTime">The <see cref="DateTime"/> to truncate.</param>
   /// <returns>a <see cref="DateTime"/> truncated to whole minutes.</returns>
   public static DateTime TruncateToWholeMinutes( this DateTime dateTime )
   {
      return dateTime.Truncate( TimeSpan.FromTicks( TimeSpan.TicksPerMinute ) );
   }

   /// <summary>
   /// Helper method to call <see cref="Truncate"/> with required argument for truncating given <see cref="DateTime"/> to whole hours.
   /// </summary>
   /// <param name="dateTime">The <see cref="DateTime"/> to truncate.</param>
   /// <returns>a <see cref="DateTime"/> truncated to whole hours.</returns>
   public static DateTime TruncateToWholeHours( this DateTime dateTime )
   {
      return dateTime.Truncate( TimeSpan.FromTicks( TimeSpan.TicksPerHour ) );
   }

   /// <summary>
   /// Helper method to call <see cref="Truncate"/> with required argument for truncating given <see cref="DateTime"/> to whole days.
   /// </summary>
   /// <param name="dateTime">The <see cref="DateTime"/> to truncate.</param>
   /// <returns>a <see cref="DateTime"/> truncated to whole days.</returns>
   public static DateTime TruncateToWholeDays( this DateTime dateTime )
   {
      return dateTime.Truncate( TimeSpan.FromTicks( TimeSpan.TicksPerDay ) );
   }
}