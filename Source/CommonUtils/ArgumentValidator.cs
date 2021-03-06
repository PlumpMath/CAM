﻿/*
 * Copyright 2007 Niclas Hedhman.
 * (org.qi4j.api.util.NullArgumentException class)
 * See NOTICE file.
 * 
 * Copyright 2012 Stanislav Muhametsin. All rights Reserved.
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

namespace CommonUtils
{
   /// <summary>
   /// Helper class to easily verify whether some method parameter is <c>null</c> or empty.
   /// </summary>
   public static class ArgumentValidator
   {
      /// <summary>
      /// Checks whether a method parameter is <c>null</c>.
      /// </summary>
      /// <typeparam name="T">Type of parameter, must be class; to ensure that this method won't be called for struct parameters.</typeparam>
      /// <param name="parameterName">The name of the parameter.</param>
      /// <param name="value">The given parameter.</param>
      /// <exception cref="ArgumentNullException">If the <paramref name="value"/> is <c>null</c>.</exception>
      public static void ValidateNotNull<T>( String parameterName, T value )
         where T : class
      {
         if ( value == null )
         {
            throw new ArgumentNullException( parameterName );
         }
      }

      /// <summary>
      /// Checks whether given enumerable parameter has any elements.
      /// </summary>
      /// <typeparam name="T">The type of the enumerable element.</typeparam>
      /// <param name="parameterName">The name of the parameter.</param>
      /// <param name="value">The given parameter.</param>
      /// <exception cref="ArgumentNullException">If the <paramref name="value"/> is <c>null</c>.</exception>
      /// <exception cref="ArgumentException">If the <paramref name="value"/> is empty.</exception>
      public static void ValidateNotEmpty<T>( String parameterName, IEnumerable<T> value )
      {
         ValidateNotNull( parameterName, value );
         if ( !value.Any() )
         {
            throw new ArgumentException( parameterName + " was empty." );
         }
      }

      /// <summary>
      /// Checks whether given array parameter has any elements. Is somewhat faster than the <see cref="ArgumentValidator.ValidateNotEmpty{T}(System.String, IEnumerable{T})"/>.
      /// </summary>
      /// <typeparam name="T">The type of the array element.</typeparam>
      /// <param name="parameterName">The name of the parameter</param>
      /// <param name="value">The given parameter</param>
      /// <exception cref="ArgumentNullException">If the <paramref name="value"/> is <c>null</c>.</exception>
      /// <exception cref="ArgumentException">If the <paramref name="value"/> is empty.</exception>
      public static void ValidateNotEmpty<T>( String parameterName, T[] value )
      {
         ValidateNotNull( parameterName, value );
         if ( value.Length <= 0 )
         {
            throw new ArgumentException( parameterName + " was empty." );
         }
      }

      /// <summary>
      /// Checks whether given string parameter contains any characters.
      /// </summary>
      /// <param name="parameterName">The name of the parameter</param>
      /// <param name="value">The given parameter</param>
      /// <exception cref="ArgumentNullException">If the <paramref name="value"/> is <c>null</c>.</exception>
      /// <exception cref="ArgumentException">If the <paramref name="value"/> is empty.</exception>
      public static void ValidateNotEmpty( String parameterName, String value )
      {
         ValidateNotNull( parameterName, value );
         if ( value.Length == 0 )
         {
            throw new ArgumentException( parameterName + " was empty string." );
         }
      }
   }
}
