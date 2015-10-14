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

//using TRVA = System.UInt32;

namespace CILAssemblyManipulator.Physical.IO
{
   public interface ReaderFunctionalityProvider
   {
      ReaderFunctionality GetFunctionality(
         Stream stream,
         out Stream newStream
         );
   }

   public interface ReaderFunctionality
   {
      RVAConverter CreateRVAConverter( PEInformation peImage );
   }

   public interface RVAConverter
   {
      Int64 ToRVA( Int64 offset );

      Int64 ToOffset( Int64 rva );
   }

   public interface ReaderILHandler
   {

   }
}