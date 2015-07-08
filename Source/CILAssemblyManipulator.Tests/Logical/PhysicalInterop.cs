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
using CILAssemblyManipulator.Logical;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CILAssemblyManipulator.Tests.Logical
{
   public class PhysicalInteropTest : AbstractCAMTest
   {

      [Test]
      public void TestPhysicalInteropWithWrapperAssemblies()
      {
         PerformTest( ctx =>
         {
            var logical = CAMLogical.NewWrapper( ctx );
            var physical = logical.MainModule.CreatePhysicalRepresentation();
            Console.WriteLine( "Hmz" );
         } );
      }

      private static void PerformTest( Action<CILReflectionContext> test )
      {
         using ( var ctx = DotNETReflectionContext.CreateDotNETContext() )
         {
            test( ctx );
         }
      }
   }
}
