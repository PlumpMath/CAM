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
using System.IO;
using System.Linq;
using System.Text;

namespace CILAssemblyManipulator.MResources
{
   public static partial class MResourcesIO
   {
      private static readonly Encoding UTF8 = new UTF8Encoding( false, true );
      private static readonly Encoding UTF16LE = new UnicodeEncoding( false, false, true );

      private enum ResourceTypeCode
      {
         Null = 0,
         String = 1,
         Boolean = 2,
         Char = 3,
         Byte = 4,
         SByte = 5,
         Int16 = 6,
         UInt16 = 7,
         Int32 = 8,
         UInt32 = 9,
         Int64 = 10,
         UInt64 = 11,
         Single = 12,
         Double = 13,
         Decimal = 14,
         DateTime = 15,
         LastPrimitive = 16,
         TimeSpan = 16,
         ByteArray = 32,
         Stream = 33,
         StartOfUserTypes = 64,
      }

      // MS-NRBF, pp. 15-17
      private enum RecordTypeEnumeration : byte
      {
         SerializedStreamHeader = 0,
         ClassWithID = 1,
         SystemClassWithMembers = 2,
         ClassWithMembers = 3,
         SystemClassWithMembersAndTypes = 4,
         ClassWithMembersAndTypes = 5,
         BinaryObjectString = 6,
         BinaryArray = 7,
         MemberPrimitiveTyped = 8,
         MemberReference = 9,
         ObjectNull = 10,
         MessageEnd = 11,
         BinaryLibrary = 12,
         ObjectNullMultiple256 = 13,
         ObjectNullMultiple = 14,
         ArraySinglePrimitive = 15,
         ArraySingleObject = 16,
         ArraySingleString = 17,
         // 18 - unused ( CrossAppDomainMap? )
         // 19 - unused ( CrossAppDomainString? )
         // 20 - unused ( CrossAppDomainAssembly? )
         MethodCall = 21,
         MethodReturn = 22
      }

      // MS-NRBF, p. 17
      private enum BinaryTypeEnumeration : byte
      {
         Primitive = 0,
         String = 1,
         Object = 2,
         SystemClass = 3,
         Class = 4,
         ObjectArray = 5,
         StringArray = 6,
         PrimitiveArray = 7
      }

      // MS-NRBF, pp. 18-19
      private enum PrimitiveTypeEnumeration : byte
      {
         Boolean = 1,
         Byte = 2,
         Char = 3,
         // 4 is unused
         Decimal = 5,
         Double = 6,
         Int16 = 7,
         Int32 = 8,
         Int64 = 9,
         SByte = 10,
         Single = 11,
         TimeSpan = 12,
         DateTime = 13,
         UInt16 = 14,
         UInt32 = 15,
         UInt64 = 16,
         Null = 17,
         String = 18
      }
   }
}
