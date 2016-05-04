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
using CILAssemblyManipulator.Physical.IO;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CILAssemblyManipulator.Tests.Physical
{
   public class LoadingTests : AbstractCAMTest
   {
      [Test]
      public void TestResolving()
      {
         var loader = new CILMetaDataLoaderNotThreadSafeForFiles();
         var md = loader.LoadAndResolve( Path.Combine( Path.GetDirectoryName( CAMLogicalLocation ), "CILMerge.Library.dll" ) );
         ValidateAllIsResolved( md );
      }
   }
}