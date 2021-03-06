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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using System.Xml.XPath;
using CILAssemblyManipulator.MResources;
using CILAssemblyManipulator.PDB;
using CommonUtils;
using CILAssemblyManipulator.Physical;
using System.Runtime.InteropServices;
using System.Diagnostics;
using CILAssemblyManipulator.Physical.IO;

namespace CILMerge
{

   public class CILMerger
   {
      private readonly CILMergeOptions _options;
      private readonly CILMergeLogCallback _log;

      public CILMerger( CILMergeOptions options, CILMergeLogCallback logCallback )
      {
         if ( options == null )
         {
            throw new ArgumentNullException( "Options" );
         }
         this._options = options;

         if ( this._options.DoLogging )
         {
            this._log = logCallback;
         }
      }

      public void PerformMerge()
      {
         // 1. Merge assemblies
         CILModuleMergeResult assemblyMergeResult;
         using ( var assMerger = new CILAssemblyMerger( this, this._options, Environment.CurrentDirectory ) )
         {
            assemblyMergeResult = assMerger.MergeModules();
         }

         // 2. Merge PDBs
         var pdbHelper = assemblyMergeResult.PDBHelper;
         if ( pdbHelper != null )
         {
            try
            {
               this.DoWithStopWatch( "Merging PDB files to " + pdbHelper.PDBFileLocation, () =>
               {
                  using ( pdbHelper )
                  {
                     this.MergePDBs( pdbHelper, assemblyMergeResult );
                  }
               } );
            }
            catch ( Exception exc )
            {
               this.Log( MessageLevel.Warning, "Error when creating PDB file for {0}. Error:\n{1}", this._options.OutPath, exc );
            }
         }

         if ( this._options.XmlDocs )
         {
            // 3. Merge documentation
            this.MergeXMLDocs( assemblyMergeResult );
         }
      }

      private void MergePDBs( PDBHelper pdbHelper, CILModuleMergeResult mergeResult )
      {
         // Merge PDBs
         var bag = new ConcurrentBag<PDBInstance>();
         this.DoPotentiallyInParallel( mergeResult.InputMergeResults, ( isRunningInParallel, iResult ) =>
         {

            var eArg = iResult.ReadingArguments;
            var debugInfo = eArg.ImageInformation.DebugInformation;
            if ( debugInfo != null && debugInfo.DebugType == 2 ) // CodeView
            {
               var pdbFNStartIdx = 24;
               var pdbFN = debugInfo.DebugData.ToArray().ReadZeroTerminatedStringFromBytes( ref pdbFNStartIdx, Encoding.UTF8 );
               if ( !String.IsNullOrEmpty( pdbFN ) )
               {
                  PDBInstance iPDB = null;
                  try
                  {
                     using ( var fs = File.Open( pdbFN, FileMode.Open, FileAccess.Read, FileShare.Read ) )
                     {
                        iPDB = PDBIO.FromStream( fs );
                     }
                  }
                  catch ( Exception exc )
                  {
                     this.Log( MessageLevel.Warning, "Could not open file {0} because of {1}.", pdbFN, exc );
                  }

                  if ( iPDB != null )
                  {
                     bag.Add( iPDB );
                     // Translate all tokens
                     foreach ( var func in iPDB.Modules.SelectMany( m => m.Functions ) )
                     {
                        func.Token = this.MapMethodToken( iResult, func.Token );
                        func.ForwardingMethodToken = this.MapMethodToken( iResult, func.ForwardingMethodToken );
                        func.ModuleForwardingMethodToken = this.MapMethodToken( iResult, func.ModuleForwardingMethodToken );
                        this.MapPDBScopeOrFunction( func, iResult );
                        //if ( func.AsyncMethodInfo != null )
                        //{
                        //   func.AsyncMethodInfo.KickoffMethodToken = this.MapMethodToken( targetEArgs, eArg, thisMethod, func.AsyncMethodInfo.KickoffMethodToken );
                        //   foreach ( var syncP in func.AsyncMethodInfo.SynchronizationPoints )
                        //   {
                        //      syncP.ContinuationMethodToken = this.MapMethodToken( targetEArgs, eArg, thisMethod, syncP.ContinuationMethodToken );
                        //   }
                        //}
                     }
                  }
               }
            }
         } );

         var pdb = new PDBInstance();
         //pdb.Age = (UInt32) targetEArgs.DebugInformation.DebugFileAge;
         //pdb.DebugGUID = targetEArgs.DebugInformation.DebugFileGUID;
         // Add all information. Should be ok, since all IL offsets remain unchanged, and all tokens are translated.
         foreach ( var currentPDB in bag )
         {
            // Merge sources
            foreach ( var src in currentPDB.Sources )
            {
               pdb.TryAddSource( src.Name, src );
            }
            // Merge modules
            foreach ( var mod in currentPDB.Modules )
            {
               var curMod = pdb.GetOrAddModule( mod.Name );
               foreach ( var func in mod.Functions )
               {
                  curMod.Functions.Add( func );
               }
            }
         }

         pdbHelper.ProcessPDB( pdb );
      }

      private void MapPDBScopeOrFunction( PDBScopeOrFunction scp, InputModuleMergeResult mergeResult )
      {
         foreach ( var slot in scp.Slots )
         {
            slot.TypeToken = this.MapTypeToken( mergeResult, slot.TypeToken );
         }
         foreach ( var subScope in scp.Scopes )
         {
            this.MapPDBScopeOrFunction( subScope, mergeResult );
         }
      }

      private UInt32 MapMethodToken( InputModuleMergeResult mergeResult, UInt32 token )
      {
         var inputToken = TableIndex.FromOneBasedTokenNullable( (Int32) token );
         TableIndex targetToken;
         return inputToken.HasValue && mergeResult.InputToTargetMapping.TryGetValue( inputToken.Value, out targetToken ) ?
            (UInt32) targetToken.OneBasedToken :
            0u;
      }

      private UInt32 MapTypeToken( InputModuleMergeResult mergeResult, UInt32 token )
      {
         var inputToken = TableIndex.FromOneBasedTokenNullable( (Int32) token );
         TableIndex targetToken;
         return inputToken.HasValue && mergeResult.InputToTargetMapping.TryGetValue( inputToken.Value, out targetToken ) ?
            (UInt32) targetToken.OneBasedToken :
            0u;
      }

      private void MergeXMLDocs( CILModuleMergeResult assemblyMergeResult )
      {
         var xmlDocs = new ConcurrentDictionary<InputModuleMergeResult, XDocument>( ReferenceEqualityComparer<InputModuleMergeResult>.ReferenceBasedComparer );

         this.DoWithStopWatch( "Merging XML document files", () =>
         {

            // TODO on *nix, comparison should maybe be case-sensitive.
            var outXmlPath = Path.ChangeExtension( this._options.OutPath, ".xml" );
            var libPaths = this._options.LibPaths;
            this.DoPotentiallyInParallel( assemblyMergeResult.InputMergeResults, ( isRunningInParallel, mResult ) =>
            {
               XDocument xDoc = null;
               var path = mResult.ModulePath;
               var xfn = Path.ChangeExtension( path, ".xml" );
               if ( !String.Equals( Path.GetFullPath( xfn ), outXmlPath, StringComparison.OrdinalIgnoreCase ) )
               {
                  try
                  {
                     xDoc = XDocument.Load( xfn );
                  }
                  catch
                  {
                     // Ignore
                  }
               }
               if ( xDoc == null && !libPaths.IsNullOrEmpty() )
               {
                  // Try load from lib paths
                  xfn = Path.GetFileName( xfn );
                  foreach ( var p in libPaths )
                  {
                     var curXfn = Path.Combine( p, xfn );
                     if ( !String.Equals( Path.GetFullPath( curXfn ), outXmlPath, StringComparison.OrdinalIgnoreCase ) )
                     {
                        try
                        {
                           xDoc = XDocument.Load( curXfn );
                           xfn = curXfn;
                        }
                        catch
                        {
                           // Ignore
                        }
                        if ( xDoc != null )
                        {
                           break;
                        }
                     }
                  }
               }
               if ( xDoc != null )
               {
                  xmlDocs.TryAdd( mResult, xDoc );
               }
            } );


            using ( var fs = File.Open( outXmlPath, FileMode.Create, FileAccess.Write, FileShare.Read ) )
            {
               // Create and save document (Need to use XmlWriter if 4-space indent needs to be preserved, the XElement saves using 2-space indent and it is not customizable).
               new XElement( "doc",
                  new XElement( "assembly", new XElement( "name", Path.GetFileNameWithoutExtension( this._options.OutPath ) ) ), // Assembly name
                  new XElement( "members", xmlDocs
                     .Select( kvp => Tuple.Create( kvp.Key, kvp.Value.XPathSelectElements( "/doc/members/*" ) ) )
                     .SelectMany( tuple =>
                     {
                        var renameDic = tuple.Item1.TypeRenames;
                        if ( renameDic.Count > 0 )
                        {
                           foreach ( var el in tuple.Item2 )
                           {
                              var nameAttr = el.Attribute( "name" );
                              String typeName;
                              if ( nameAttr != null && nameAttr.Value.StartsWith( "T:" ) && renameDic.TryGetValue( el.Attribute( "name" ).Value.Substring( 2 ), out typeName ) )
                              {
                                 // The name was changed during merge.
                                 // TODO need to do same for members, etc.
                                 nameAttr.SetValue( typeName );
                              }
                           }
                        }
                        return tuple.Item2;
                     } )
                  )
               ).Save( fs );
            }
         } );
      }

      internal CILMergeException NewCILMergeException( ExitCode code, String message, Exception inner = null )
      {
         this.Log( MessageLevel.Error, message );
         return new CILMergeException( code, message, inner );
      }

      internal void Log( MessageLevel mLevel, String formatString, params Object[] args )
      {
         if ( this._log != null )
         {
            this._log.Log( mLevel, formatString, args );
         }
      }

      internal void DoPotentiallyInParallel<T>( IEnumerable<T> enumerable, Action<Boolean, T> action )
      {
         if ( this._options.Parallel && enumerable.Skip( 1 ).Any() )
         {
            System.Threading.Tasks.Parallel.ForEach( enumerable, item => action( true, item ) );
         }
         else
         {
            foreach ( var item in enumerable )
            {
               action( false, item );
            }
         }
      }

      internal void DoWithStopWatch( String what, Action action )
      {
         var sw = new Stopwatch();
         sw.Start();

         action();

         sw.Stop();
         this.Log( MessageLevel.Info, what + " took {0} ms.", sw.ElapsedMilliseconds );
      }

   }

   public enum MessageLevel { Warning, Error, Info }

   public enum ExitCode
   {
      Success = 0,
      ExceptionDuringStartup,
      ExceptionDuringMerge,
      NoInputAssembly,
      DuplicateTypeName,
      FailedToProduceCorrectResult,
      DebugInfoNotEasilyMergeable,
      NonILOnlyModule,
      AttrFileSpecifiedButDoesNotExist,
      ErrorAccessingInputFile,
      ErrorAccessingReferencedAssembly,
      ErrorAccessingExcludeFile,
      ErrorAccessingTargetFile,
      ErrorAccessingSNFile,
      ErrorAccessingLogFile,
      PCL2TargetTypeFail,
      PCL2TargetMethodFail,
      PCL2TargetFieldFail,
      ErrorAccessingInputPDB,
      ErrorAccessingTargetPDB,
      ErrorReadingPDB,
      ErrorWritingPDB,
      UnresolvedAssemblyReference,
      NoTargetFrameworkMoniker,
      FailedToDeduceTargetFramework,
      FailedToReadTargetFrameworkMonikerInformation,
      FailedToMapPDBType,
      VariableTypeGenericParameterCount,
      NoTargetFrameworkSpecified,
      //UnresolvableMemberReferenceToAnotherInputModule,
      //ErrorMatchingMemberReferenceSignature
   }

   internal class CILModuleMergeResult
   {
      private readonly InputModuleMergeResult[] _inputMergeResults;
      private readonly CILMetaData _targetModule;
      private readonly WritingArguments _emittingArguments;
      private readonly PDBHelper _pdbHelper;

      internal CILModuleMergeResult(
         CILMetaData targetModule,
         WritingArguments emittingArguments,
         PDBHelper pdbHelper,
         IEnumerable<InputModuleMergeResult> inputMergeResults
         )
      {
         ArgumentValidator.ValidateNotNull( "Target module", targetModule );
         ArgumentValidator.ValidateNotNull( "Emitting arguments", emittingArguments );
         ArgumentValidator.ValidateNotNull( "Input merge results", inputMergeResults );

         this._targetModule = targetModule;
         this._emittingArguments = emittingArguments;
         this._inputMergeResults = inputMergeResults.ToArray();
         this._pdbHelper = pdbHelper;
         ArgumentValidator.ValidateNotEmpty( "Input merge results", this._inputMergeResults );
      }

      public CILMetaData TargetModule
      {
         get
         {
            return this._targetModule;
         }
      }

      public WritingArguments EmittingArguments
      {
         get
         {
            return this._emittingArguments;
         }
      }

      public InputModuleMergeResult[] InputMergeResults
      {
         get
         {
            return this._inputMergeResults;
         }
      }

      public PDBHelper PDBHelper
      {
         get
         {
            return this._pdbHelper;
         }
      }

   }


   internal class InputModuleMergeResult
   {
      private readonly CILMetaData _inputModule;
      private readonly String _modulePath;
      private readonly ReadingArguments _readingArguments;
      private readonly Lazy<IDictionary<String, String>> _typeRenames;
      private readonly Lazy<IDictionary<TableIndex, TableIndex>> _inputToTargetMapping;

      public InputModuleMergeResult(
         CILMetaData inputModule,
         String modulePath,
         ReadingArguments readingArguments,
         Func<IDictionary<String, String>> typeRenames,
         Func<IDictionary<TableIndex, TableIndex>> inputToTarget
         )
      {
         ArgumentValidator.ValidateNotNull( "Input module", inputModule );
         ArgumentValidator.ValidateNotEmpty( "Module path", modulePath );
         ArgumentValidator.ValidateNotNull( "Reading arguments", readingArguments );
         ArgumentValidator.ValidateNotNull( "Type renames", typeRenames );
         ArgumentValidator.ValidateNotNull( "Input to target mapping", inputToTarget );

         this._inputModule = inputModule;
         this._modulePath = modulePath;
         this._readingArguments = readingArguments;
         this._typeRenames = new Lazy<IDictionary<String, String>>( typeRenames, LazyThreadSafetyMode.ExecutionAndPublication );
         this._inputToTargetMapping = new Lazy<IDictionary<TableIndex, TableIndex>>( inputToTarget, LazyThreadSafetyMode.ExecutionAndPublication );
      }

      public String ModulePath
      {
         get
         {
            return this._modulePath;
         }
      }

      public ReadingArguments ReadingArguments
      {
         get
         {
            return this._readingArguments;
         }
      }

      public IDictionary<String, String> TypeRenames
      {
         get
         {
            return this._typeRenames.Value;
         }
      }

      public IDictionary<TableIndex, TableIndex> InputToTargetMapping
      {
         get
         {
            return this._inputToTargetMapping.Value;
         }
      }
   }

   internal class CILAssemblyMerger : AbstractDisposable, IDisposable
   {

      private static readonly Type ATTRIBUTE_USAGE_TYPE = typeof( AttributeUsageAttribute );
      private static readonly System.Reflection.PropertyInfo ALLOW_MULTIPLE_PROP = typeof( AttributeUsageAttribute ).GetProperty( "AllowMultiple" );

      private readonly CILMerger _merger;
      private readonly CILMergeOptions _options;
      private readonly CILMetaDataLoaderResourceCallbacksForFiles _loaderCallbacks;
      private readonly CILMetaDataLoader _moduleLoader;
      private readonly CryptoCallbacks _cryptoCallbacks;

      private readonly List<CILMetaData> _inputModules;
      private readonly IDictionary<AssemblyReference, CILMetaData> _inputModulesAsAssemblyReferences;
      private readonly IDictionary<CILMetaData, IDictionary<String, CILMetaData>> _inputModulesAsModuleReferences;
      private CILMetaData _primaryModule;
      private CILMetaData _targetModule;

      // Key: one of input modules. Value: dictionary; Key: table index in input module. Value: table index in output module
      private readonly IDictionary<CILMetaData, IDictionary<TableIndex, TableIndex>> _tableIndexMappings;
      // Key: table index in target module, Value: input module, and corresponding index in corresponding table in the input module
      private readonly IDictionary<TableIndex, Tuple<CILMetaData, Int32>> _targetTableIndexMappings;
      // Key: one of input modules. Value: dictionary; Key: full type name, value: type def index in TARGET module
      private readonly IDictionary<CILMetaData, IDictionary<String, Int32>> _inputModuleTypeNamesInTargetModule;
      // Key: on of input modules. Value: dictionary; Key: full type name, value: type def index in INPUT module
      private readonly IDictionary<CILMetaData, IDictionary<String, Int32>> _inputModuleTypeNamesInInputModule;


      private readonly IList<String> _targetTypeNames;
      private readonly Lazy<Regex[]> _excludeRegexes;
      private readonly String _inputBasePath;

      private readonly IEqualityComparer<AssemblyReference> _assemblyReferenceEqualityComparer;

      internal CILAssemblyMerger( CILMerger merger, CILMergeOptions options, String inputBasePath )
      {
         this._merger = merger;
         this._options = options;

         this._inputModules = new List<CILMetaData>();
         this._loaderCallbacks = new CILMetaDataLoaderResourceCallbacksForFiles(
            referenceAssemblyBasePath: options.ReferenceAssembliesDirectory
            );
         this._cryptoCallbacks = new CryptoCallbacksDotNET();
         this._moduleLoader = options.Parallel ?
            (CILMetaDataLoader) new CILMetaDataLoaderThreadSafeConcurrentForFiles( crypto: this._cryptoCallbacks, callbacks: this._loaderCallbacks ) :
            new CILMetaDataLoaderNotThreadSafeForFiles( crypto: this._cryptoCallbacks, callbacks: this._loaderCallbacks );
         this._assemblyReferenceEqualityComparer = ComparerFromFunctions.NewEqualityComparer<AssemblyReference>(
            ( x, y ) =>
            {
               Boolean retVal;
               var xa = x.AssemblyInformation;
               var ya = y.AssemblyInformation;
               if ( x.Attributes.IsFullPublicKey() == y.Attributes.IsFullPublicKey() )
               {
                  retVal = xa.Equals( ya );
               }
               else
               {
                  retVal = xa.Equals( ya, false );
                  if ( retVal
                     && !xa.PublicKeyOrToken.IsNullOrEmpty()
                     && !ya.PublicKeyOrToken.IsNullOrEmpty()
                     )
                  {
                     Byte[] xBytes, yBytes;
                     var computer = this._moduleLoader.PublicKeyComputer.Value;
                     if ( x.Attributes.IsFullPublicKey() )
                     {
                        // Create public key token for x and compare with y
                        xBytes = computer.ComputePublicKeyToken( xa.PublicKeyOrToken );
                        yBytes = ya.PublicKeyOrToken;
                     }
                     else
                     {
                        // Create public key token for y and compare with x
                        xBytes = xa.PublicKeyOrToken;
                        yBytes = computer.ComputePublicKeyToken( ya.PublicKeyOrToken );
                     }
                     retVal = ArrayEqualityComparer<Byte>.DefaultArrayEqualityComparer.Equals( xBytes, yBytes );
                  }
               }
               return retVal;
            },
            x => x.AssemblyInformation.GetHashCode()
            );
         this._inputModulesAsAssemblyReferences = new Dictionary<AssemblyReference, CILMetaData>( this._assemblyReferenceEqualityComparer );
         this._inputModulesAsModuleReferences = new Dictionary<CILMetaData, IDictionary<String, CILMetaData>>( ReferenceEqualityComparer<CILMetaData>.ReferenceBasedComparer );
         this._tableIndexMappings = new Dictionary<CILMetaData, IDictionary<TableIndex, TableIndex>>( ReferenceEqualityComparer<CILMetaData>.ReferenceBasedComparer );
         this._targetTableIndexMappings = new Dictionary<TableIndex, Tuple<CILMetaData, Int32>>();
         this._inputModuleTypeNamesInTargetModule = new Dictionary<CILMetaData, IDictionary<String, Int32>>( ReferenceEqualityComparer<CILMetaData>.ReferenceBasedComparer );
         this._inputModuleTypeNamesInInputModule = new Dictionary<CILMetaData, IDictionary<String, Int32>>( ReferenceEqualityComparer<CILMetaData>.ReferenceBasedComparer );
         this._targetTypeNames = new List<String>();


         this._excludeRegexes = new Lazy<Regex[]>( () =>
         {
            var excl = options.ExcludeFile;
            if ( options.Internalize && !String.IsNullOrEmpty( excl ) )
            {
               try
               {
                  return File.ReadAllLines( excl ).Select( line => new Regex( line ) ).ToArray();
               }
               catch ( Exception exc )
               {
                  throw this.NewCILMergeException( ExitCode.ErrorAccessingExcludeFile, "Error accessing exclude file " + excl + ".", exc );
               }
            }
            else
            {
               return Empty<Regex>.Array;
            }
         }, LazyThreadSafetyMode.None );
         this._inputBasePath = inputBasePath ?? Environment.CurrentDirectory;
      }

      internal CILModuleMergeResult MergeModules()
      {
         WritingArguments eArgs = null;
         Int32[][] reorderResult = null;
         PDBHelper pdbHelper = null;
         this._merger.DoWithStopWatch( "Merging modules and assemblies as a whole", () =>
         {

            // First of all, load all input modules
            this._merger.DoWithStopWatch( "Phase 1: Loading input assemblies and modules", () =>
            {
               this.LoadAllInputModules();
            } );


            // Then, create target module
            eArgs = this.CreateEmittingArgumentsForTargetModule();
            this.CreateTargetAssembly( eArgs );

            this._merger.DoWithStopWatch( "Phase 2: Populating target module", () =>
            {
               // 1. Create structural tables
               var typeDefInfo = this.ConstructStructuralTables();
               // 2. Create tables used in signatures and IL tokens
               this.ConstructTablesUsedInSignaturesAndILTokens( typeDefInfo, eArgs );
               // 3. Create signatures and IL
               this.ConstructSignaturesAndMethodIL();
               // 4. Create the rest of the tables
               this.ConstructTheRestOfTheTables();
               // 5. Create assembly & module custom attributes
               this.ApplyAssemblyAndModuleCustomAttributes();
               // 6. Fix regargetable assembly references if needed
               // Do it here, after TargetFrameworkAttribute has been applied specified
               this.FixTargetAssemblyReferences();
            } );
            // Process target module

            this._merger.DoWithStopWatch( "Phase 3: Reordering target module tables and removing duplicates", () =>
            {
               // 7. Re-order and remove duplicates from tables
               reorderResult = this._targetModule.OrderTablesAndRemoveDuplicates();
            } );

            // Prepare PDB

            // Create PDB helper here, so it would modify EmittingArguments *before* actual emitting
            pdbHelper = !this._options.NoDebug && this._inputModules.Any( m => this._moduleLoader.GetReadingArgumentsForMetaData( m ).ImageInformation.DebugInformation != null ) ?
               new PDBHelper( this._targetModule, eArgs, this._options.OutPath ) :
               null;

            this._merger.DoWithStopWatch( "Phase 4: Writing target module", () =>
            {
               // 8. Emit module
               try
               {
                  this._targetModule.WriteModuleTo( this._options.OutPath, eArgs );
               }
               catch ( Exception exc )
               {
                  throw this.NewCILMergeException( ExitCode.ErrorAccessingTargetFile, "Error accessing target file " + this._options.OutPath + "(" + exc + ").", exc );
               }

            } );
         } );

         return new CILModuleMergeResult(
            this._targetModule,
            eArgs,
            pdbHelper,
            this._inputModules.Select( m => new InputModuleMergeResult(
               m,
               this._moduleLoader.GetResourceFor( m ),
               this._moduleLoader.GetReadingArgumentsForMetaData( m ),
               () => this.CreateRenameDictionaryForInputModule( m ),
               () => this.CreateTableIndexMappingForInputModule( m, reorderResult )
               ) )
            );
      }

      private void FixTargetAssemblyReferences()
      {
         // We have to fix target assembly references, because if we are merging non-PCL library with PCL library, and non-PCL lib implements interface from PCL lib, their signatures may differ if assembly refs are not processed.
         // This causes PEVerify errors.
         var retargetableInfos = new HashSet<Int32>();

         if ( !this._options.SkipFixingAssemblyReferences )
         {
            var targetFW = new Lazy<TargetFrameworkInfo>( () =>
            {
               var fw = this._targetModule.GetTargetFrameworkInformationOrNull( this._moduleLoader.CreateNewResolver() );
               if ( fw == null )
               {
                  throw this.NewCILMergeException( ExitCode.NoTargetFrameworkSpecified, "TODO: allow specifying target framework info (id, version, profile) through options." );
               }
               return fw;
            }, LazyThreadSafetyMode.None );

            foreach ( var inputModule in this._inputModules )
            {
               var aRefs = inputModule.AssemblyReferences.TableContents;
               var inputMappings = this._tableIndexMappings[inputModule];
               for ( var i = 0; i < aRefs.Count; ++i )
               {
                  TableIndex targetAssemblyRefIndex;
                  if ( inputMappings.TryGetValue( new TableIndex( Tables.AssemblyRef, i ), out targetAssemblyRefIndex ) )
                  {
                     var aRef = aRefs[i];
                     if ( aRef.Attributes.IsRetargetable() )
                     {
                        var aInfo = aRef.AssemblyInformation;
                        var correspondingNewAssembly = this._moduleLoader.GetOrLoadMetaData( Path.Combine( this._loaderCallbacks.GetTargetFrameworkPathForFrameworkInfo( targetFW.Value ), aInfo.Name + ".dll" ) );
                        var targetARef = this._targetModule.AssemblyReferences.TableContents[targetAssemblyRefIndex.Index];
                        if ( !targetFW.Value.AreFrameworkAssemblyReferencesRetargetable )
                        {
                           targetARef.Attributes &= ( ~AssemblyFlags.Retargetable );
                        }
                        var aDefInfo = correspondingNewAssembly.AssemblyDefinitions.TableContents[0].AssemblyInformation;
                        aDefInfo.DeepCopyContentsTo( targetARef.AssemblyInformation );
                        if ( this._options.UseFullPublicKeyForRefs )
                        {
                           targetARef.Attributes |= AssemblyFlags.PublicKey;
                        }
                        else if ( !targetARef.Attributes.IsFullPublicKey() )
                        {
                           targetARef.AssemblyInformation.PublicKeyOrToken = this._moduleLoader.PublicKeyComputer.Value.ComputePublicKeyToken( aDefInfo.PublicKeyOrToken );
                        }
                        retargetableInfos.Add( targetAssemblyRefIndex.Index );
                     }
                  }
               }
            }
         }

         if ( this._options.UseFullPublicKeyForRefs )
         {
            foreach ( var inputModule in this._inputModules )
            {
               var aRefs = inputModule.AssemblyReferences.TableContents;
               var inputModulePath = this._moduleLoader.GetResourceFor( inputModule );
               var inputMappings = this._tableIndexMappings[inputModule];
               for ( var i = 0; i < aRefs.Count; ++i )
               {
                  TableIndex targetAssemblyRefIndex;
                  if ( inputMappings.TryGetValue( new TableIndex( Tables.AssemblyRef, i ), out targetAssemblyRefIndex ) )
                  {
                     var aRef = aRefs[i];
                     if ( !aRef.Attributes.IsFullPublicKey() && !retargetableInfos.Contains( targetAssemblyRefIndex.Index ) )
                     {
                        var aRefModule = this.GetPossibleResourcesForAssemblyReference( inputModule, aRef )
                        .Where( p => File.Exists( p ) )
                        .Select( p => this._moduleLoader.GetOrLoadMetaData( p ) )
                        .Where( m => m.AssemblyDefinitions.RowCount > 0 )
                        .FirstOrDefault();

                        if ( aRefModule != null )
                        {
                           var targetARef = this._targetModule.AssemblyReferences.TableContents[targetAssemblyRefIndex.Index];
                           targetARef.AssemblyInformation.PublicKeyOrToken = aRefModule.AssemblyDefinitions.TableContents[0].AssemblyInformation.PublicKeyOrToken.CreateBlockCopy();
                           targetARef.Attributes |= AssemblyFlags.PublicKey;
                        }
                        else
                        {
                           this.Log( MessageLevel.Warning, "Failed to get referenced assembly {0} in {1}, target module most likely won't have full public key in all of its assembly references.", aRef, inputModulePath );
                        }
                     }
                  }
               }
            }
         }
      }

      private IDictionary<String, String> CreateRenameDictionaryForInputModule( CILMetaData inputModule )
      {
         var retVal = new Dictionary<String, String>();
         foreach ( var kvp in this._inputModuleTypeNamesInTargetModule[inputModule] )
         {
            var oldName = kvp.Key;
            var newName = this._targetTypeNames[kvp.Value];
            if ( !String.Equals( oldName, newName ) )
            {
               retVal.Add( oldName, newName );
            }
         }

         return retVal;
      }

      private IDictionary<TableIndex, TableIndex> CreateTableIndexMappingForInputModule( CILMetaData inputModule, Int32[][] reorderingResult )
      {
         var mapping = this._tableIndexMappings[inputModule];
         return mapping.ToDictionary(
            kvp => kvp.Key,
            kvp =>
            {
               var targetIndex = kvp.Value;
               var reorderingMapping = reorderingResult[(Int32) targetIndex.Table];
               return reorderingMapping == null ?
                  targetIndex :
                  new TableIndex( targetIndex.Table, reorderingMapping[targetIndex.Index] );
            } );
      }



      private void LoadAllInputModules()
      {

         var paths = this._options.InputAssemblies;
         if ( paths == null || paths.Length == 0 )
         {
            throw this.NewCILMergeException( ExitCode.NoInputAssembly, "Input assembly list must contain at least one assembly." );
         }

         if ( this._options.AllowWildCards )
         {
            paths = paths.SelectMany( path =>
            {
               return path.IndexOf( '*' ) == -1 && path.IndexOf( '?' ) == -1 ?
                  (IEnumerable<String>) new[] { path } :
                  Directory.EnumerateFiles( Path.IsPathRooted( path ) ? Path.GetDirectoryName( path ) : this._inputBasePath, Path.GetFileName( path ) );
            } ).Select( path => Path.GetFullPath( path ) ).ToArray();
         }
         else
         {
            paths = paths.Select( path => Path.IsPathRooted( path ) ? path : Path.Combine( this._inputBasePath, path ) ).ToArray();
         }

         paths = paths.Distinct().ToArray();

         this.DoPotentiallyInParallel( paths, ( isRunningInParallel, path ) =>
         {
            var mod = this._moduleLoader.LoadAndResolve( path );
            var rArgs = this._moduleLoader.GetReadingArgumentsForMetaData( mod );
            if ( !rArgs.ImageInformation.CLIInformation.CLIHeader.Flags.IsILOnly() && !this._options.ZeroPEKind )
            {
               throw this.NewCILMergeException( ExitCode.NonILOnlyModule, "The module in " + path + " is not IL-only." );
            }
         } );

         this._inputModules.AddRange( paths.Select( p => this._moduleLoader.GetOrLoadMetaData( p ) ) );

         this._primaryModule = this._inputModules[0];

         if ( this._options.Closed )
         {
            var set = new HashSet<CILMetaData>( this._inputModules, ReferenceEqualityComparer<CILMetaData>.ReferenceBasedComparer );
            foreach ( var mod in this._inputModules.ToArray() )
            {
               this.LoadModuleForClosedSet( mod, set );
            }
            set.UnionWith( this._inputModules );
            this._inputModules.Clear();

            // Add primary module as first one
            this._inputModules.Add( this._primaryModule );

            // Add the rest
            set.Remove( this._primaryModule );
            this._inputModules.AddRange( set );
         }

         // Build helper data structures for input modules
         foreach ( var inputModule in this._inputModules.Where( m => m.AssemblyDefinitions.RowCount > 0 ) )
         {
            var aRef = new AssemblyReference();
            inputModule.AssemblyDefinitions.TableContents[0].AssemblyInformation.DeepCopyContentsTo( aRef.AssemblyInformation );
            aRef.Attributes = AssemblyFlags.PublicKey;
            CILMetaData existing;
            if ( this._inputModulesAsAssemblyReferences.TryGetValue( aRef, out existing ) )
            {
               this.Log( MessageLevel.Warning, "Duplicate assembly based on full assembly name, paths: {0} and {1}.", this._moduleLoader.GetResourceFor( existing ), this._moduleLoader.GetResourceFor( inputModule ) );
            }
            else
            {
               this._inputModulesAsAssemblyReferences.Add( aRef, inputModule );
            }
         }

         this.Log( MessageLevel.Info, "The following input modules are being merged: {0}.", String.Join( ", ", this._inputModules.Select( m => this._moduleLoader.GetResourceFor( m ) ) ) );

         foreach ( var inputModule in this._inputModules )
         {
            var dic = new Dictionary<String, CILMetaData>();
            foreach ( var f in inputModule.GetModuleFileReferences() )
            {
               var modRefPath = Path.Combine( Path.GetDirectoryName( this._moduleLoader.GetResourceFor( inputModule ) ), f.Name );
               var targetInputModule = this._inputModules
                  .Where( md => md.AssemblyDefinitions.RowCount == 0 )
                  .FirstOrDefault( md => String.Equals( this._moduleLoader.GetResourceFor( md ), modRefPath ) ); // TODO maybe case-insensitive match??
               if ( targetInputModule != null )
               {
                  dic[f.Name] = targetInputModule;
               }
            }

            this._inputModulesAsModuleReferences.Add( inputModule, dic );

         }
      }

      private void LoadModuleForClosedSet( CILMetaData md, ISet<CILMetaData> curLoaded )
      {
         var thisPath = this._moduleLoader.GetResourceFor( md );

         // Load all modules
         foreach ( var moduleRef in md.GetModuleFileReferences() )
         {
            var path = this._loaderCallbacks
               .GetPossibleResourcesForModuleReference( thisPath, md, moduleRef.Name )
               .FirstOrDefault( p => this._loaderCallbacks.IsValidResource( p ) );
            if ( String.IsNullOrEmpty( path ) )
            {
               this.Log( MessageLevel.Warning, "Failed to find module referenced in " + thisPath + "." );
            }
            else
            {
               var otherModule = this._moduleLoader.LoadAndResolve( path );
               if ( curLoaded.Add( otherModule ) )
               {
                  this.LoadModuleForClosedSet( otherModule, curLoaded );
               }
            }
         }

         // Load all referenced assemblies, except target framework ones.
         var fwDir = this._loaderCallbacks.GetTargetFrameworkPathFor( md );
         foreach ( var aRef in md.AssemblyReferences.TableContents )
         {
            var path = this.GetPossibleResourcesForAssemblyReference( md, aRef )
               .Where( p => fwDir == null || !p.StartsWith( fwDir ) )
               .FirstOrDefault( p => this._loaderCallbacks.IsValidResource( p ) );
            if ( !String.IsNullOrEmpty( path ) )
            {
               var otherAssembly = this._moduleLoader.LoadAndResolve( path );
               if ( curLoaded.Add( otherAssembly ) )
               {
                  this.LoadModuleForClosedSet( otherAssembly, curLoaded );
               }
            }
         }
      }

      private IEnumerable<String> GetPossibleResourcesForAssemblyReference( CILMetaData inputModule, AssemblyReference assemblyRef )
      {
         var aRefInfo = assemblyRef.NewInformationForResolving();
         var inputModulePath = this._moduleLoader.GetResourceFor( inputModule );
         var retVal = this._loaderCallbacks
            .GetPossibleResourcesForAssemblyReference( inputModulePath, inputModule, aRefInfo, null );
         var libPaths = this._options.LibPaths;
         if ( !libPaths.IsNullOrEmpty() )
         {
            var inputModuleDir = Path.GetDirectoryName( inputModulePath );
            retVal = retVal.Concat( libPaths
               .Where( lp => !String.Equals( Path.GetFullPath( lp ), inputModuleDir ) )
               .SelectMany( lp => this._loaderCallbacks.GetPossibleResourcesForAssemblyReference( Path.Combine( lp, "dummy.dll" ), inputModule, aRefInfo, null ) )
               );
         }
         return retVal;
      }

      private CILAssemblyManipulator.Physical.WritingArguments CreateEmittingArgumentsForTargetModule()
      {

         // Prepare strong _name
         var keyFile = this._options.KeyFile;
         CILAssemblyManipulator.Physical.StrongNameKeyPair sn = null;
         if ( !String.IsNullOrEmpty( keyFile ) )
         {
            try
            {
               sn = new CILAssemblyManipulator.Physical.StrongNameKeyPair( File.ReadAllBytes( keyFile ) );
            }
            catch ( Exception exc )
            {
               throw this.NewCILMergeException( ExitCode.ErrorAccessingSNFile, "Error accessing strong name file " + keyFile + ".", exc );
            }
         }
         else if ( !String.IsNullOrEmpty( this._options.CSPName ) )
         {
            sn = new CILAssemblyManipulator.Physical.StrongNameKeyPair( this._options.CSPName );
         }

         // Prepare emitting arguments
         var pEArgs = this._moduleLoader.GetReadingArgumentsForMetaData( this._primaryModule );
         var pHeaders = pEArgs.ImageInformation;

         var eArgs = new CILAssemblyManipulator.Physical.WritingArguments();
         var eHeaders = pHeaders.CreateWritingOptions();
         eArgs.WritingOptions = eHeaders;
         eHeaders.DebugOptions.DebugData = null;
         eHeaders.PEOptions.FileAlignment = this._options.FileAlign;
         eHeaders.PEOptions.MajorSubsystemVersion = (Int16) this._options.SubsystemMajor;
         eHeaders.PEOptions.MinorSubsystemVersion = (Int16) this._options.SubsystemMinor;
         if ( this._options.HighEntropyVA )
         {
            eHeaders.PEOptions.DLLCharacteristics |= DLLFlags.HighEntropyVA;
         }
         var md = this._options.MetadataVersionString;
         if ( !String.IsNullOrEmpty( md ) )
         {
            eHeaders.CLIOptions.MDRootOptions.VersionString = md;
         }

         eArgs.DelaySign = this._options.DelaySign;
         eArgs.SigningAlgorithm = this._options.SigningAlgorithm;
         eArgs.StrongName = sn;
         eArgs.CryptoCallbacks = this._cryptoCallbacks;

         return eArgs;
      }

      private void CreateTargetAssembly( CILAssemblyManipulator.Physical.WritingArguments eArgs )
      {
         var outPath = this._options.OutPath;
         var targetAssemblyName = this._options.TargetAssemblyName;
         if ( String.IsNullOrEmpty( targetAssemblyName ) )
         {
            targetAssemblyName = Path.GetFileNameWithoutExtension( outPath );
         }
         var targetModuleName = this._options.TargetModuleName;
         if ( String.IsNullOrEmpty( targetModuleName ) )
         {
            targetModuleName = targetAssemblyName + Path.GetExtension( outPath );
         }

         var targetMD = CILMetaDataFactory.CreateMinimalAssembly( targetAssemblyName, targetModuleName ); // Skip dot
         var primaryAInfo = this._primaryModule.AssemblyDefinitions.TableContents[0].AssemblyInformation;
         var aInfo = targetMD.AssemblyDefinitions.TableContents[0].AssemblyInformation;

         aInfo.Culture = primaryAInfo.Culture;
         if ( eArgs.StrongName != null )
         {
            aInfo.PublicKeyOrToken = eArgs.CryptoCallbacks.CreatePublicKeyFromStrongName( eArgs.StrongName );
         }

         if ( this._options.VerMajor > -1 )
         {
            // Version was specified explictly
            aInfo.VersionMajor = this._options.VerMajor;
            aInfo.VersionMinor = this._options.VerMinor;
            aInfo.VersionBuild = this._options.VerBuild;
            aInfo.VersionRevision = this._options.VerRevision;
         }
         else
         {
            var an2 = primaryAInfo;

            aInfo.VersionMajor = an2.VersionMajor;
            aInfo.VersionMinor = an2.VersionMinor;
            aInfo.VersionBuild = an2.VersionBuild;
            aInfo.VersionRevision = an2.VersionRevision;
         }

         this._targetModule = targetMD;


      }

      // Constructs TypeDef, NestedClass, MethodDef, ParamDef, GenericParameter tables to target module
      private IList<IList<Tuple<CILMetaData, Int32>>> ConstructStructuralTables()
      {
         //var nestedTypeInfo = new Dictionary<CILMetaData, IDictionary<Int32, IList<Int32>>>( ReferenceEqualityComparer<CILMetaData>.ReferenceBasedComparer );
         var enclosingTypeInfo = new Dictionary<CILMetaData, IDictionary<Int32, Int32>>( ReferenceEqualityComparer<CILMetaData>.ReferenceBasedComparer );
         var genericParamInfo = new Dictionary<CILMetaData, IDictionary<TableIndex, IList<Int32>>>( ReferenceEqualityComparer<CILMetaData>.ReferenceBasedComparer );

         // Populate enclosing type info
         foreach ( var md in this._inputModules )
         {
            //var ntDic = new Dictionary<Int32, IList<Int32>>();
            var eDic = new Dictionary<Int32, Int32>();
            var gDic = new Dictionary<TableIndex, IList<Int32>>();
            foreach ( var nt in md.NestedClassDefinitions.TableContents )
            {
               //ntDic
               //   .GetOrAdd_NotThreadSafe( nt.EnclosingClass.Index, e => new List<Int32>() )
               //   .Add( nt.NestedClass.Index );
               eDic[nt.NestedClass.Index] = nt.EnclosingClass.Index;
            }
            var gDefs = md.GenericParameterDefinitions.TableContents;
            for ( var i = 0; i < gDefs.Count; ++i )
            {
               gDic
                  .GetOrAdd_NotThreadSafe( gDefs[i].Owner, g => new List<Int32>() )
                  .Add( i );
            }

            //nestedTypeInfo.Add( md, ntDic );
            enclosingTypeInfo.Add( md, eDic );
            genericParamInfo.Add( md, gDic );
         }

         // Create type strings
         var typeStrings = new Dictionary<CILMetaData, IDictionary<Int32, String>>( ReferenceEqualityComparer<CILMetaData>.ReferenceBasedComparer );
         foreach ( var md in this._inputModules )
         {
            var thisTypeStrings = new Dictionary<Int32, String>();
            var thisTypeStringInInput = new Dictionary<String, Int32>();
            var thisEnclosingTypeInfo = enclosingTypeInfo[md];

            for ( var tDefIdx = 1; tDefIdx < md.TypeDefinitions.RowCount; ++tDefIdx ) // Skip <Module> type
            {
               var typeString = CreateTypeString( md.TypeDefinitions, tDefIdx, thisEnclosingTypeInfo );
               thisTypeStrings.Add( tDefIdx, typeString );
               thisTypeStringInInput.Add( typeString, tDefIdx );

            }
            typeStrings.Add( md, thisTypeStrings );
            this._inputModuleTypeNamesInInputModule.Add( md, thisTypeStringInInput );
         }

         // Construct TypeDef table
         var currentlyAddedTypeNames = new Dictionary<String, Int32>();
         ISet<String> allTypeStringsSet = null;
         var targetModule = this._targetModule;
         var targetTypeDefs = targetModule.TypeDefinitions.TableContents;
         var targetTypeInfo = new List<IList<Tuple<CILMetaData, Int32>>>();
         var targetTypeFullNames = this._targetTypeNames;
         // Add type info for <Module> type
         targetTypeInfo.Add( this._inputModules
            .Where( m => m.TypeDefinitions.RowCount > 0 )
            .Select( m => Tuple.Create( m, 0 ) )
            .ToList()
            );
         targetTypeFullNames.Add( targetTypeDefs[0].Name );

         foreach ( var md in this._inputModules )
         {
            var thisTypeStrings = typeStrings[md];
            var thisEnclosingTypeInfo = enclosingTypeInfo[md];
            var thisModuleMapping = new Dictionary<TableIndex, TableIndex>();
            var thisTypeStringInfo = new Dictionary<String, Int32>();

            // Add <Module> type mapping and info
            thisModuleMapping.Add( new TableIndex( Tables.TypeDef, 0 ), new TableIndex( Tables.TypeDef, 0 ) );

            // Process other types
            for ( var tDefIdx = 1; tDefIdx < md.TypeDefinitions.RowCount; ++tDefIdx ) // Skip <Module> type
            {
               var targetTDefIdx = targetTypeDefs.Count;
               var tDef = md.TypeDefinitions.TableContents[tDefIdx];
               var typeStr = thisTypeStrings[tDefIdx];
               var added = currentlyAddedTypeNames.TryAdd_NotThreadSafe( typeStr, targetTDefIdx );
               var typeAttrs = this.GetNewTypeAttributesForType( md, tDefIdx, thisEnclosingTypeInfo.ContainsKey( tDefIdx ), typeStr );
               var newName = tDef.Name;
               IList<Tuple<CILMetaData, Int32>> thisMergedTypes;
               var thisTypeFullName = typeStr;
               if ( added || this.IsDuplicateOK( ref newName, typeStr, ref thisTypeFullName, typeAttrs, typeStrings, ref allTypeStringsSet ) )
               {
                  targetTypeDefs.Add( new TypeDefinition()
                  {
                     Name = newName,
                     Namespace = tDef.Namespace,
                     Attributes = typeAttrs
                  } );
                  thisMergedTypes = new List<Tuple<CILMetaData, Int32>>();
                  targetTypeInfo.Add( thisMergedTypes );
                  targetTypeFullNames.Add( thisTypeFullName );
               }
               else if ( this._options.Union )
               {
                  targetTDefIdx = currentlyAddedTypeNames[typeStr];
                  thisMergedTypes = targetTypeInfo[targetTDefIdx];
               }
               else
               {
                  throw this.NewCILMergeException( ExitCode.DuplicateTypeName, "The type " + typeStr + " appears in more than one assembly." );
               }

               thisModuleMapping.Add( new TableIndex( Tables.TypeDef, tDefIdx ), new TableIndex( Tables.TypeDef, targetTDefIdx ) );
               thisMergedTypes.Add( Tuple.Create( md, tDefIdx ) );
               thisTypeStringInfo.Add( typeStr, targetTDefIdx );
            }

            this._tableIndexMappings.Add( md, thisModuleMapping );
            this._inputModuleTypeNamesInTargetModule.Add( md, thisTypeStringInfo );
         }

         // Construct GenericParameter, NestedClass, FieldDef, MethodDef, ParamDef tables
         var targetTableIndexMappings = this._targetTableIndexMappings;
         for ( var tDefIdx = 0; tDefIdx < targetTypeDefs.Count; ++tDefIdx )
         {
            var thisTypeInfo = targetTypeInfo[tDefIdx];
            var gParameters = thisTypeInfo.Select( t =>
               {
                  IList<Int32> gParams;
                  genericParamInfo[t.Item1].TryGetValue( new TableIndex( Tables.TypeDef, t.Item2 ), out gParams );
                  return gParams;
               } )
               .ToArray();

            if ( !gParameters
               .Select( gParams => gParams == null ? 0 : gParams.Count )
               .EmptyOrAllEqual()
               )
            {
               var first = thisTypeInfo[0];
               throw this.NewCILMergeException( ExitCode.VariableTypeGenericParameterCount, "Type " + typeStrings[first.Item1][first.Item2] + " has different amount of generic arguments in different modules." );
            }
            else if ( gParameters[0] != null )
            {
               // GenericParameter, for type
               // Just use generic parameters from first suitable input module
               var gParamModule = thisTypeInfo[0].Item1;
               foreach ( var gParamIdx in gParameters[0] )
               {
                  var targetGParamIdx = new TableIndex( Tables.GenericParameter, targetModule.GenericParameterDefinitions.RowCount );
                  targetTableIndexMappings.Add( targetGParamIdx, Tuple.Create( gParamModule, gParamIdx ) );
                  this._tableIndexMappings[gParamModule].Add( new TableIndex( Tables.GenericParameter, gParamIdx ), targetGParamIdx );
                  var gParam = gParamModule.GenericParameterDefinitions.TableContents[gParamIdx];
                  targetModule.GenericParameterDefinitions.TableContents.Add( new GenericParameterDefinition()
                  {
                     Attributes = gParam.Attributes,
                     GenericParameterIndex = gParam.GenericParameterIndex,
                     Name = gParam.Name,
                     Owner = new TableIndex( Tables.TypeDef, tDefIdx )
                  } );
               }
            }

            var tDef = targetTypeDefs[tDefIdx];
            var tFDef = targetModule.FieldDefinitions.TableContents;
            var tMDef = targetModule.MethodDefinitions.TableContents;
            tDef.FieldList = new TableIndex( Tables.Field, tFDef.Count );
            tDef.MethodList = new TableIndex( Tables.MethodDef, tMDef.Count );

            foreach ( var typeInfo in thisTypeInfo )
            {
               var inputMD = typeInfo.Item1;
               var inputTDefIdx = typeInfo.Item2;
               var thisGenericParamInfo = genericParamInfo[inputMD];
               var thisTableMappings = this._tableIndexMappings[inputMD];

               // NestedClass
               var thisEnclosingInfo = enclosingTypeInfo[inputMD];
               Int32 enclosingTypeIdx;
               if ( thisEnclosingInfo.TryGetValue( inputTDefIdx, out enclosingTypeIdx ) )
               {
                  targetModule.NestedClassDefinitions.TableContents.Add( new NestedClassDefinition()
                  {
                     NestedClass = new TableIndex( Tables.TypeDef, tDefIdx ),
                     EnclosingClass = thisTableMappings[new TableIndex( Tables.TypeDef, enclosingTypeIdx )]
                  } );
               }

               // FieldDef
               foreach ( var fDefIdx in inputMD.GetTypeFieldIndices( inputTDefIdx ) )
               {
                  var targetFIdx = new TableIndex( Tables.Field, tFDef.Count );
                  targetTableIndexMappings.Add( targetFIdx, Tuple.Create( inputMD, fDefIdx ) );
                  thisTableMappings.Add( new TableIndex( Tables.Field, fDefIdx ), targetFIdx );
                  var fDef = inputMD.FieldDefinitions.TableContents[fDefIdx];
                  tFDef.Add( new FieldDefinition()
                  {
                     Attributes = fDef.Attributes,
                     Name = fDef.Name,
                  } );
               }

               // MethodDef
               var tPDef = targetModule.ParameterDefinitions.TableContents;
               foreach ( var mDefIdx in inputMD.GetTypeMethodIndices( inputTDefIdx ) )
               {
                  var targetMDefIdx = tMDef.Count;
                  targetTableIndexMappings.Add( new TableIndex( Tables.MethodDef, targetMDefIdx ), Tuple.Create( inputMD, mDefIdx ) );
                  thisTableMappings.Add( new TableIndex( Tables.MethodDef, mDefIdx ), new TableIndex( Tables.MethodDef, targetMDefIdx ) );
                  var mDef = inputMD.MethodDefinitions.TableContents[mDefIdx];
                  tMDef.Add( new MethodDefinition()
                  {
                     Attributes = mDef.Attributes,
                     ImplementationAttributes = mDef.ImplementationAttributes,
                     Name = mDef.Name,
                     ParameterList = new TableIndex( Tables.Parameter, tPDef.Count ),
                  } );

                  // GenericParameter, for method
                  IList<Int32> methodGParams;
                  if ( thisGenericParamInfo.TryGetValue( new TableIndex( Tables.MethodDef, mDefIdx ), out methodGParams ) )
                  {
                     var tGDef = targetModule.GenericParameterDefinitions.TableContents;
                     foreach ( var methodGParamIdx in methodGParams )
                     {
                        var targetGParamIdx = new TableIndex( Tables.GenericParameter, tGDef.Count );
                        targetTableIndexMappings.Add( targetGParamIdx, Tuple.Create( inputMD, methodGParamIdx ) );
                        thisTableMappings.Add( new TableIndex( Tables.GenericParameter, methodGParamIdx ), targetGParamIdx );
                        var methodGParam = inputMD.GenericParameterDefinitions.TableContents[methodGParamIdx];
                        tGDef.Add( new GenericParameterDefinition()
                        {
                           Attributes = methodGParam.Attributes,
                           GenericParameterIndex = methodGParam.GenericParameterIndex,
                           Name = methodGParam.Name,
                           Owner = new TableIndex( Tables.MethodDef, targetMDefIdx )
                        } );
                     }
                  }

                  // ParamDef
                  foreach ( var pDefIdx in inputMD.GetMethodParameterIndices( mDefIdx ) )
                  {
                     var targetPIdx = new TableIndex( Tables.Parameter, tPDef.Count );
                     targetTableIndexMappings.Add( targetPIdx, Tuple.Create( inputMD, pDefIdx ) );
                     thisTableMappings.Add( new TableIndex( Tables.Parameter, pDefIdx ), targetPIdx );
                     var pDef = inputMD.ParameterDefinitions.TableContents[pDefIdx];
                     tPDef.Add( new ParameterDefinition()
                     {
                        Attributes = pDef.Attributes,
                        Name = pDef.Name,
                        Sequence = pDef.Sequence
                     } );
                  }
               }
            }
         }

         return targetTypeInfo;
      }

      private void ConstructTablesUsedInSignaturesAndILTokens(
         IList<IList<Tuple<CILMetaData, Int32>>> targetTypeInfo,
         WritingArguments eArgs
         )
      {
         // AssemblyRef (used by MemberRef table)
         this.MergeTables(
            md => md.AssemblyReferences,
            ( md, inputIdx, thisIdx ) => !this._inputModulesAsAssemblyReferences.ContainsKey( md.AssemblyReferences.TableContents[inputIdx.Index] ),
            ( md, aRef, inputIdx, thisIdx ) =>
            {
               var newARef = new AssemblyReference()
               {
                  Attributes = aRef.Attributes,
                  HashValue = aRef.HashValue
               };
               aRef.AssemblyInformation.DeepCopyContentsTo( newARef.AssemblyInformation );
               return newARef;
            } );

         // ModuleRef (used by MemberRef table)
         // Skip the actual module references (but keep the ones from ImplMap table)
         this.MergeTables(
            md => md.ModuleReferences,
            ( md, inputIdx, thisIdx ) => !this._inputModulesAsModuleReferences[md].ContainsKey( md.ModuleReferences.TableContents[inputIdx.Index].ModuleName ),
            ( md, mRef, inputIdx, thisIdx ) => new ModuleReference()
            {
               ModuleName = mRef.ModuleName
            } );

         // TypeRef (used by signatures/IL tokens)
         var typeRefInputModules = new Dictionary<CILMetaData, IDictionary<Int32, Tuple<CILMetaData, String>>>();
         this.MergeTables(
            md => md.TypeReferences,
            ( md, inputIdx, thisIdx ) =>
            {
               // Check whether this will be a TypeRef or TypeDef row in target module
               String typeString; CILMetaData refModule;
               var tDefIdx = this.GetTargetModuleTypeDefIndexForInputModuleTypeRef( md, inputIdx, thisIdx, out typeString, out refModule );
               var retVal = tDefIdx < 0;
               if ( !retVal )
               {
                  // This type ref is actually a type def in target module
                  this._tableIndexMappings[md].Add( inputIdx, new TableIndex( Tables.TypeDef, tDefIdx ) );
                  typeRefInputModules
                     .GetOrAdd_NotThreadSafe( md, mdd => new Dictionary<Int32, Tuple<CILMetaData, String>>() )
                     .Add( inputIdx.Index, Tuple.Create( refModule, typeString ) );
               }
               return retVal;
            },
            ( md, tRef, inputIdx, thisIdx ) => new TypeReference()
            {
               Name = tRef.Name,
               Namespace = tRef.Namespace,
            } );

         // Non-null ResolutionScope indexes:
         // Module (already processed)
         // ModuleRef (already processed)
         // AssemblyRef (already processed)
         // TypeRef (just processed)
         // Update TypeRef.ResolutionScope separately
         this.SetTableIndicesNullable(
            md => md.TypeReferences,
            tRef => tRef.ResolutionScope,
            ( tRef, resScope ) => tRef.ResolutionScope = resScope
            );

         // TypeSpec (used by signatures/IL tokens)
         this.MergeTables(
            md => md.TypeSpecifications,
            null,
            ( md, tSpec, inputIdx, thisIdx ) => new TypeSpecification()
            {

            } );

         // MemberRef (used by IL tokens)
         // DeclaringType indexes:
         // MethodDef (already processed)
         // ModuleRef (already processed, possibly missing rows)
         // TypeDef (already processed)
         // TypeRef (already processed)
         // TypeSpec (already processed)
         // Update MemberReference.DeclaringType in place.
         this.MergeTables(
            md => md.MemberReferences,
            ( md, inputIdx, thisIdx ) =>
            {
               // If member ref declaring type ends up being, replace with corresponding MethodDef/FieldDef reference
               var mRef = md.MemberReferences.TableContents[inputIdx.Index];
               var thisMappings = this._tableIndexMappings[md];
               var declType = mRef.DeclaringType;
               var targetDeclaringType = thisMappings[declType];
               var isActuallyInTargetModule = declType.Table == Tables.TypeRef && targetDeclaringType.Table == Tables.TypeDef;
               if ( isActuallyInTargetModule )
               {
                  var mRefName = mRef.Name;
                  var targetModule = this._targetModule;
                  var targetFields = targetModule.FieldDefinitions;

                  Tables targetMRefTable;
                  Int32 targetMRefIndex;
                  switch ( mRef.Signature.SignatureKind )
                  {
                     case SignatureKind.Field:
                        // Match simply by name
                        // TODO maybe match also field signature as well?
                        targetMRefTable = Tables.Field;
                        targetMRefIndex = this._targetModule
                           .GetTypeFieldIndices( targetDeclaringType.Index )
                           .Where( fi =>
                           {
                              var fDef = this._targetModule.FieldDefinitions.TableContents[fi];
                              return !fDef.Attributes.IsCompilerControlled()
                                 && String.Equals( mRefName, fDef.Name );
                           } )
                           .FirstOrDefaultCustom( -1 );
                        break;
                     case SignatureKind.MethodReference:
                        // Match by name and signature
                        targetMRefTable = Tables.MethodDef;
                        var moduleContainingMethodDefInfo = typeRefInputModules[md][declType.Index];
                        var moduleContainingMethodDef = moduleContainingMethodDefInfo.Item1;
                        var methodDefContainingTypeIndex = this._inputModuleTypeNamesInInputModule[moduleContainingMethodDef][moduleContainingMethodDefInfo.Item2];
                        targetMRefIndex = moduleContainingMethodDef.GetTypeMethodIndices( methodDefContainingTypeIndex )
                           .Where( mi =>
                           {
                              var mDef = moduleContainingMethodDef.MethodDefinitions.TableContents[mi];
                              return !mDef.Attributes.IsCompilerControlled()
                                 && String.Equals( mRefName, mDef.Name )
                                 && this.MatchTargetMethodSignatureToMemberRefMethodSignature( moduleContainingMethodDef, md, mDef.Signature, (MethodReferenceSignature) mRef.Signature );
                           } )
                           .FirstOrDefaultCustom( -1 );
                        if ( targetMRefIndex >= 0 )
                        {
                           targetMRefIndex = this._tableIndexMappings[moduleContainingMethodDef][new TableIndex( Tables.MethodDef, targetMRefIndex )].Index;
                        }
                        break;
                     default:
                        targetMRefTable = (Tables) Byte.MaxValue;
                        targetMRefIndex = -1;
                        break;

                  }

                  if ( targetMRefIndex == -1 )
                  {
                     this.Log( MessageLevel.Warning, "Unresolvable member reference in module " + this._moduleLoader.GetResourceFor( md ) + " at zero-based index " + inputIdx.Index + " to another input module." );
                     isActuallyInTargetModule = false;
                  }
                  else
                  {
                     thisMappings.Add( inputIdx, new TableIndex( targetMRefTable, targetMRefIndex ) );
                  }
               }

               return !isActuallyInTargetModule;
            },
            ( md, mRef, inputIdx, thisIdx ) => new MemberReference()
            {
               DeclaringType = mRef.DeclaringType.Table == Tables.ModuleRef && !this._tableIndexMappings[md].ContainsKey( mRef.DeclaringType ) ?
                  new TableIndex( Tables.TypeDef, 0 ) :
                  this.TranslateTableIndex( md, mRef.DeclaringType ),
               Name = mRef.Name
            } );

         // MethodSpec (used by IL tokens)
         // Method indexes:
         // MethodDef (already processed)
         // MemberRef (already processed)
         // Update MethodSpec.Method in place
         this.MergeTables(
            md => md.MethodSpecifications,
            null,
            ( md, mSpec, inputIdx, thisIdx ) => new MethodSpecification()
            {
               Method = this.TranslateTableIndex( md, mSpec.Method )
            } );

         // StandaloneSignature (used by IL tokens)
         this.MergeTables(
            md => md.StandaloneSignatures,
            null,
            ( md, sig, inputIdx, thisIdx ) => new StandaloneSignature()
            {

            } );

         // Revisit TypeDef table and process its BaseType references
         // TypeDef.BaseType indexes:
         // TypeDef (already processed)
         // TypeRef (already processed)
         // TypeSpec (already processed)
         // Update TypeDef.BaseType separately
         this.SetTableIndicesNullable(
            md => md.TypeDefinitions,
            tDefIdx => targetTypeInfo[tDefIdx][0], // TODO check that base types are 'same' (would involve creating assembly-qualified type string, and even then should take into account the effects of Retargetable assembly ref attribute)
            tDef => tDef.BaseType,
            ( tDef, bType ) => tDef.BaseType = bType
            );
      }

      private Boolean MatchTargetMethodSignatureToMemberRefMethodSignature( CILMetaData defModule, CILMetaData refModule, AbstractMethodSignature methodDef, AbstractMethodSignature methodRef )
      {
         return methodDef.SignatureStarter == methodRef.SignatureStarter
            && methodDef.Parameters.Count == methodRef.Parameters.Count
            && methodDef.Parameters
               .Where( ( p, idx ) => this.MatchTargetParameterSignatureToMemberRefParameterSignature( defModule, refModule, p, methodRef.Parameters[idx] ) )
               .Count() == methodDef.Parameters.Count
            && this.MatchTargetParameterSignatureToMemberRefParameterSignature( defModule, refModule, methodDef.ReturnType, methodRef.ReturnType );
      }

      private Boolean MatchTargetParameterSignatureToMemberRefParameterSignature( CILMetaData defModule, CILMetaData refModule, ParameterSignature paramDef, ParameterSignature paramRef )
      {
         return paramDef.IsByRef == paramRef.IsByRef
            && this.MatchTargetCustomModsToMemberRefCustomMods( defModule, refModule, paramDef.CustomModifiers, paramRef.CustomModifiers )
            && this.MatchTargetTypeSignatureToMemberRefTypeSignature( defModule, refModule, paramDef.Type, paramRef.Type );
      }

      private Boolean MatchTargetCustomModsToMemberRefCustomMods( CILMetaData defModule, CILMetaData refModule, List<CustomModifierSignature> cmDef, List<CustomModifierSignature> cmRef )
      {
         return cmDef.Count == cmRef.Count
            && cmDef
               .Where( ( c, idx ) => c.IsOptional == cmRef[idx].IsOptional && this.MatchTargetSignatureTypeToMemberRefSignatureType( defModule, refModule, c.CustomModifierType, cmRef[idx].CustomModifierType ) )
               .Count() == cmDef.Count;
      }

      private Boolean MatchTargetTypeSignatureToMemberRefTypeSignature( CILMetaData defModule, CILMetaData refModule, TypeSignature typeDef, TypeSignature typeRef )
      {
         var retVal = typeDef.TypeSignatureKind == typeRef.TypeSignatureKind;
         if ( retVal )
         {
            switch ( typeDef.TypeSignatureKind )
            {
               case TypeSignatureKind.ClassOrValue:
                  var classDef = (ClassOrValueTypeSignature) typeDef;
                  var classRef = (ClassOrValueTypeSignature) typeRef;
                  var gArgsDef = classDef.GenericArguments;
                  var gArgsRef = classRef.GenericArguments;
                  retVal = classDef.IsClass == classRef.IsClass
                     && this.MatchTargetSignatureTypeToMemberRefSignatureType( defModule, refModule, classDef.Type, classRef.Type )
                     && gArgsDef.Count == gArgsRef.Count
                     && gArgsDef
                        .Where( ( g, idx ) => this.MatchTargetTypeSignatureToMemberRefTypeSignature( defModule, refModule, g, gArgsRef[idx] ) )
                        .Count() == gArgsDef.Count;
                  break;
               case TypeSignatureKind.ComplexArray:
                  var arrayDef = (ComplexArrayTypeSignature) typeDef;
                  var arrayRef = (ComplexArrayTypeSignature) typeRef;
                  retVal = arrayDef.Rank == arrayRef.Rank
                     && ListEqualityComparer<List<Int32>, Int32>.DefaultListEqualityComparer.Equals( arrayDef.Sizes, arrayRef.Sizes )
                     && ListEqualityComparer<List<Int32>, Int32>.DefaultListEqualityComparer.Equals( arrayDef.LowerBounds, arrayRef.LowerBounds )
                     && this.MatchTargetTypeSignatureToMemberRefTypeSignature( defModule, refModule, arrayDef.ArrayType, arrayRef.ArrayType );
                  break;
               case TypeSignatureKind.FunctionPointer:
                  retVal = this.MatchTargetMethodSignatureToMemberRefMethodSignature( defModule, refModule, ( (FunctionPointerTypeSignature) typeDef ).MethodSignature, ( (FunctionPointerTypeSignature) typeRef ).MethodSignature );
                  break;
               case TypeSignatureKind.GenericParameter:
                  var gDef = (GenericParameterTypeSignature) typeDef;
                  var gRef = (GenericParameterTypeSignature) typeRef;
                  retVal = gDef.IsTypeParameter == gRef.IsTypeParameter
                     && gDef.GenericParameterIndex == gRef.GenericParameterIndex;
                  break;
               case TypeSignatureKind.Pointer:
                  var ptrDef = (PointerTypeSignature) typeDef;
                  var ptrRef = (PointerTypeSignature) typeRef;
                  retVal = this.MatchTargetCustomModsToMemberRefCustomMods( defModule, refModule, ptrDef.CustomModifiers, ptrRef.CustomModifiers )
                     && this.MatchTargetTypeSignatureToMemberRefTypeSignature( defModule, refModule, ptrDef.PointerType, ptrRef.PointerType );
                  break;
               case TypeSignatureKind.Simple:
                  retVal = ( (SimpleTypeSignature) typeDef ).SimpleType == ( (SimpleTypeSignature) typeRef ).SimpleType;
                  break;
               case TypeSignatureKind.SimpleArray:
                  var szArrayDef = (SimpleArrayTypeSignature) typeDef;
                  var szArrayRef = (SimpleArrayTypeSignature) typeRef;
                  retVal = this.MatchTargetCustomModsToMemberRefCustomMods( defModule, refModule, szArrayDef.CustomModifiers, szArrayRef.CustomModifiers )
                     && this.MatchTargetTypeSignatureToMemberRefTypeSignature( defModule, refModule, szArrayDef.ArrayType, szArrayRef.ArrayType );
                  break;
               default:
                  retVal = false;
                  break;
                  //throw this.NewCILMergeException( ExitCode.ErrorMatchingMemberReferenceSignature, "Encountered unrecognized type signature kind: " + typeDef.TypeSignatureKind + "." );
            }
         }

         return retVal;
      }

      private Boolean MatchTargetSignatureTypeToMemberRefSignatureType( CILMetaData defModule, CILMetaData refModule, TableIndex defIdx, TableIndex refIdx )
      {
         switch ( defIdx.Table )
         {
            case Tables.TypeDef:
               return refIdx.Table == Tables.TypeRef && this._tableIndexMappings[defModule][defIdx] == this._tableIndexMappings[refModule][refIdx];
            case Tables.TypeRef:
               return refIdx.Table == Tables.TypeRef && this.MatchDefTypeRefToRefTypeRef( defModule, refModule, defIdx.Index, refIdx.Index );
            case Tables.TypeSpec:
               return refIdx.Table == Tables.TypeSpec && this.MatchTargetTypeSignatureToMemberRefTypeSignature( defModule, refModule, defModule.TypeSpecifications.TableContents[defIdx.Index].Signature, refModule.TypeSpecifications.TableContents[refIdx.Index].Signature );
            default:
               return false;
         }
      }

      private Boolean MatchDefTypeRefToRefTypeRef( CILMetaData defModule, CILMetaData refModule, Int32 defIdx, Int32 refIdx )
      {
         var defTypeRef = defModule.TypeReferences.TableContents[defIdx];
         var refTypeRef = refModule.TypeReferences.TableContents[refIdx];
         var retVal = String.Equals( defTypeRef.Name, refTypeRef.Name )
            && String.Equals( defTypeRef.Namespace, refTypeRef.Namespace );
         if ( retVal )
         {
            var defResScopeNullable = defTypeRef.ResolutionScope;
            var refResScopeNullable = refTypeRef.ResolutionScope;
            if ( defResScopeNullable.HasValue == refResScopeNullable.HasValue )
            {
               if ( defResScopeNullable.HasValue )
               {
                  var defResScope = defResScopeNullable.Value;
                  var refResScope = refResScopeNullable.Value;
                  switch ( defResScope.Table )
                  {
                     case Tables.TypeRef:
                        retVal = refResScope.Table == Tables.TypeRef
                           && this.MatchDefTypeRefToRefTypeRef( defModule, refModule, defResScope.Index, refResScope.Index );
                        break;
                     case Tables.AssemblyRef:
                        retVal = refResScope.Table == Tables.AssemblyRef
                           && this._tableIndexMappings[defModule].ContainsKey( defResScope ) == this._tableIndexMappings[refModule].ContainsKey( refResScope );
                        if ( retVal && this._tableIndexMappings[defModule].ContainsKey( defResScope ) )
                        {
                           var defARef = defModule.AssemblyReferences.TableContents[defResScope.Index];
                           var refARef = refModule.AssemblyReferences.TableContents[refResScope.Index];
                           if ( defARef.Attributes.IsRetargetable() || refARef.Attributes.IsRetargetable() )
                           {
                              // Simple name match
                              retVal = String.Equals( defARef.AssemblyInformation.Name, refARef.AssemblyInformation.Name );
                           }
                           else
                           {
                              retVal = this._assemblyReferenceEqualityComparer.Equals( defARef, refARef );
                           }
                        }
                        break;
                     case Tables.Module:
                     case Tables.ModuleRef:
                        retVal = refResScope.Table == Tables.AssemblyRef
                           && !this._tableIndexMappings[refModule].ContainsKey( refResScope );
                        break;
                     default:
                        retVal = false;
                        break;
                  }

               }
               else
               {
                  // TODO Lazy mapping IDictionary<Tuple<String, String>, ExportedType> for each input module
                  throw new NotImplementedException( "ExportedType in TypeRef while matching method definition and reference signatures." );
               }
            }
         }

         return retVal;
      }

      private Int32 GetTargetModuleTypeDefIndexForInputModuleTypeRef(
         CILMetaData inputModule,
         TableIndex inputIndex,
         TableIndex targetIndex,
         out String thisTypeString,
         out CILMetaData referencedModule
         )
      {
         var tRef = inputModule.TypeReferences.TableContents[inputIndex.Index];
         var resScopeNullable = tRef.ResolutionScope;
         var retVal = -1;
         thisTypeString = null;
         referencedModule = null;
         if ( resScopeNullable.HasValue )
         {
            var resScope = resScopeNullable.Value;
            switch ( resScope.Table )
            {
               case Tables.TypeRef:
                  retVal = this.GetTargetModuleTypeDefIndexForInputModuleTypeRef( inputModule, resScope, targetIndex, out thisTypeString, out referencedModule );
                  if ( referencedModule != null && retVal >= 0 )
                  {
                     retVal = this._inputModuleTypeNamesInTargetModule[referencedModule][thisTypeString + "+" + tRef.Name];
                  }
                  break;
               case Tables.ModuleRef:
                  if ( !this._tableIndexMappings[inputModule].ContainsKey( resScope ) )
                  {
                     referencedModule = this._inputModulesAsModuleReferences[inputModule][inputModule.ModuleReferences.TableContents[resScope.Index].ModuleName];
                  }
                  break;
               case Tables.AssemblyRef:
                  if ( !this._tableIndexMappings[inputModule].ContainsKey( resScope ) )
                  {
                     referencedModule = this._inputModulesAsAssemblyReferences[inputModule.AssemblyReferences.TableContents[resScope.Index]];
                  }
                  break;
               case Tables.Module:
                  referencedModule = inputModule;
                  break;
            }

            if ( referencedModule != null && retVal == -1 )
            {
               thisTypeString = CreateTypeStringFromTopLevelType( tRef.Namespace, tRef.Name );
               retVal = this._inputModuleTypeNamesInTargetModule[referencedModule][thisTypeString];
            }
         }
         else
         {
            throw new NotImplementedException( "ExportedType as ResolutionScope in TypeRef table." );
         }

         return retVal;
      }

      private void ConstructSignaturesAndMethodIL()
      {
         // 1. Create all tables which are still unprocessed, and have signatures.
         this.MergeTables(
            md => md.PropertyDefinitions,
            null,
            ( md, pDef, inputIdx, thisIdx ) => new PropertyDefinition()
            {
               Attributes = pDef.Attributes,
               Name = pDef.Name
            } );

         // CustomAttribute and DeclarativeSecurity signatures do not reference table indices, so they can be skipped

         // 2. Create all signatures
         // Signatures reference only TypeDef, TypeRef, and TypeSpec tables, all of which should've been processed in ConstructNonStructuralTablesUsedInSignaturesAndILTokens method
         var targetModule = this._targetModule;
         // FieldDef
         var fDefs = targetModule.FieldDefinitions.TableContents;
         for ( var i = 0; i < fDefs.Count; ++i )
         {
            var inputInfo = this._targetTableIndexMappings[new TableIndex( Tables.Field, i )];
            var inputModule = inputInfo.Item1;
            var thisMappings = this._tableIndexMappings[inputModule];
            fDefs[i].Signature = inputModule.FieldDefinitions.TableContents[inputInfo.Item2].Signature.CreateDeepCopy( tIdx => thisMappings[tIdx] );
         }

         // MethodDef
         var mDefs = targetModule.MethodDefinitions.TableContents;
         for ( var i = 0; i < mDefs.Count; ++i )
         {
            var inputInfo = this._targetTableIndexMappings[new TableIndex( Tables.MethodDef, i )];
            var inputModule = inputInfo.Item1;
            var thisMappings = this._tableIndexMappings[inputModule];
            var inputMethodDef = inputModule.MethodDefinitions.TableContents[inputInfo.Item2];
            var targetMethodDef = mDefs[i];
            targetMethodDef.Signature = inputMethodDef.Signature.CreateDeepCopy( tIdx => thisMappings[tIdx] );

            // Create IL
            // IL tokens reference only TypeDef, TypeRef, TypeSpec, MethodDef, FieldDef, MemberRef, MethodSpec or StandaloneSignature tables, all of which should've been processed in ConstructNonStructuralTablesUsedInSignaturesAndILTokens method
            var inputIL = inputMethodDef.IL;
            if ( inputIL != null )
            {
               var targetIL = new MethodILDefinition( inputIL.ExceptionBlocks.Count, inputIL.OpCodes.Count );
               targetMethodDef.IL = targetIL;
               targetIL.ExceptionBlocks.AddRange( inputIL.ExceptionBlocks.Select( eb => new MethodExceptionBlock()
               {
                  BlockType = eb.BlockType,
                  ExceptionType = eb.ExceptionType.HasValue ? thisMappings[eb.ExceptionType.Value] : (TableIndex?) null,
                  FilterOffset = eb.FilterOffset,
                  HandlerLength = eb.HandlerLength,
                  HandlerOffset = eb.HandlerOffset,
                  TryLength = eb.TryLength,
                  TryOffset = eb.TryOffset
               } ) );
               targetIL.InitLocals = inputIL.InitLocals;
               targetIL.LocalsSignatureIndex = inputIL.LocalsSignatureIndex.HasValue ? thisMappings[inputIL.LocalsSignatureIndex.Value] : (TableIndex?) null;
               targetIL.MaxStackSize = inputIL.MaxStackSize;
               targetIL.OpCodes.AddRange( inputIL.OpCodes.Select<OpCodeInfo, OpCodeInfo>( oc =>
               {
                  switch ( oc.InfoKind )
                  {
                     case OpCodeOperandKind.OperandInteger:
                        return new OpCodeInfoWithInt32( oc.OpCode, ( (OpCodeInfoWithInt32) oc ).Operand );
                     case OpCodeOperandKind.OperandInteger64:
                        return new OpCodeInfoWithInt64( oc.OpCode, ( (OpCodeInfoWithInt64) oc ).Operand );
                     case OpCodeOperandKind.OperandNone:
                        return oc;
                     case OpCodeOperandKind.OperandR4:
                        return new OpCodeInfoWithSingle( oc.OpCode, ( (OpCodeInfoWithSingle) oc ).Operand );
                     case OpCodeOperandKind.OperandR8:
                        return new OpCodeInfoWithDouble( oc.OpCode, ( (OpCodeInfoWithDouble) oc ).Operand );
                     case OpCodeOperandKind.OperandString:
                        return new OpCodeInfoWithString( oc.OpCode, ( (OpCodeInfoWithString) oc ).Operand );
                     case OpCodeOperandKind.OperandSwitch:
                        var ocSwitch = (OpCodeInfoWithSwitch) oc;
                        var ocSwitchTarget = new OpCodeInfoWithSwitch( oc.OpCode, ocSwitch.Offsets.Count );
                        ocSwitchTarget.Offsets.AddRange( ocSwitch.Offsets );
                        return ocSwitchTarget;
                     case OpCodeOperandKind.OperandToken:
                        return new OpCodeInfoWithToken( oc.OpCode, thisMappings[( (OpCodeInfoWithToken) oc ).Operand] );
                     default:
                        throw new NotSupportedException( "Unknown op code kind: " + oc.InfoKind + "." );
                  }
               } ) );
            }

         }

         // MemberRef
         var mRefs = targetModule.MemberReferences.TableContents;
         for ( var i = 0; i < mRefs.Count; ++i )
         {
            var inputInfo = this._targetTableIndexMappings[new TableIndex( Tables.MemberRef, i )];
            var inputModule = inputInfo.Item1;
            var thisMappings = this._tableIndexMappings[inputModule];
            mRefs[i].Signature = inputModule.MemberReferences.TableContents[inputInfo.Item2].Signature.CreateDeepCopy( tIdx => thisMappings[tIdx] );
         }

         // StandaloneSignature
         var sSigs = targetModule.StandaloneSignatures.TableContents;
         for ( var i = 0; i < sSigs.Count; ++i )
         {
            var inputInfo = this._targetTableIndexMappings[new TableIndex( Tables.StandaloneSignature, i )];
            var inputModule = inputInfo.Item1;
            var thisMappings = this._tableIndexMappings[inputModule];
            sSigs[i].Signature = inputModule.StandaloneSignatures.TableContents[inputInfo.Item2].Signature.CreateDeepCopy( tIdx => thisMappings[tIdx] );
         }

         // PropertyDef
         var pDefs = targetModule.PropertyDefinitions.TableContents;
         for ( var i = 0; i < pDefs.Count; ++i )
         {
            var inputInfo = this._targetTableIndexMappings[new TableIndex( Tables.Property, i )];
            var inputModule = inputInfo.Item1;
            var thisMappings = this._tableIndexMappings[inputModule];
            pDefs[i].Signature = inputModule.PropertyDefinitions.TableContents[inputInfo.Item2].Signature.CreateDeepCopy( tIdx => thisMappings[tIdx] );
         }

         // TypeSpec
         var tSpecs = targetModule.TypeSpecifications.TableContents;
         for ( var i = 0; i < tSpecs.Count; ++i )
         {
            var inputInfo = this._targetTableIndexMappings[new TableIndex( Tables.TypeSpec, i )];
            var inputModule = inputInfo.Item1;
            var thisMappings = this._tableIndexMappings[inputModule];
            tSpecs[i].Signature = inputModule.TypeSpecifications.TableContents[inputInfo.Item2].Signature.CreateDeepCopy( tIdx => thisMappings[tIdx] );
         }

         // MethodSpecification
         var mSpecs = targetModule.MethodSpecifications.TableContents;
         for ( var i = 0; i < mSpecs.Count; ++i )
         {
            var inputInfo = this._targetTableIndexMappings[new TableIndex( Tables.MethodSpec, i )];
            var inputModule = inputInfo.Item1;
            var thisMappings = this._tableIndexMappings[inputModule];
            mSpecs[i].Signature = inputModule.MethodSpecifications.TableContents[inputInfo.Item2].Signature.CreateDeepCopy( tIdx => thisMappings[tIdx] );
         }

         // CustomAttribute and DeclarativeSecurity signatures do not reference table indices, so they are processed in ConstructTheRestOfTheTables method

      }

      private void ConstructTheRestOfTheTables()
      {
         // EventDef
         this.MergeTables(
            md => md.EventDefinitions,
            null,
            ( md, evt, inputIdx, thisIdx ) => new EventDefinition()
            {
               Attributes = evt.Attributes,
               EventType = this.TranslateTableIndex( md, evt.EventType ), // TypeRef, TypeDef, TypeSpec -> already processed
               Name = evt.Name
            } );

         // EventMap
         this.MergeTables(
            md => md.EventMaps,
            null,
            ( md, evtMap, inputIdx, thisIdx ) => new EventMap()
            {
               EventList = this.TranslateTableIndex( md, evtMap.EventList ), // Event -> already processed
               Parent = this.TranslateTableIndex( md, evtMap.Parent ) // TypeDef -> already processed
            } );

         // PropertyMap
         this.MergeTables(
            md => md.PropertyMaps,
            null,
            ( md, propMap, inputIdx, thisIdx ) => new PropertyMap()
            {
               Parent = this.TranslateTableIndex( md, propMap.Parent ), // TypeDef -> already processed
               PropertyList = this.TranslateTableIndex( md, propMap.PropertyList ) // Property -> already processed
            } );

         // InterfaceImpl
         this.MergeTables(
            md => md.InterfaceImplementations,
            null,
            ( md, impl, inputIdx, thisIdx ) => new InterfaceImplementation()
            {
               Class = this.TranslateTableIndex( md, impl.Class ), // TypeDef -> already processed
               Interface = this.TranslateTableIndex( md, impl.Interface ) // TypeDef/TypeRef/TypeSpec -> already processed
            } );

         // ConstantDef
         this.MergeTables(
            md => md.ConstantDefinitions,
            null,
            ( md, constant, inputIdx, thisIdx ) => new ConstantDefinition()
            {
               Parent = this.TranslateTableIndex( md, constant.Parent ), // ParamDef/FieldDef/PropertyDef -> already processed
               Type = constant.Type,
               Value = constant.Value
            } );

         // FieldMarshal
         this.MergeTables(
            md => md.FieldMarshals,
            null,
            ( md, marshal, inputIdx, thisIdx ) => new FieldMarshal()
            {
               NativeType = this.ProcessMarshalingInfo( md, marshal.NativeType ),
               Parent = this.TranslateTableIndex( md, marshal.Parent ) // ParamDef/FieldDef -> already processed
            } );

         // DeclSecurity
         this.MergeTables(
            md => md.SecurityDefinitions,
            null,
            ( md, sec, inputIdx, thisIdx ) =>
            {
               var retVal = new SecurityDefinition( sec.PermissionSets.Count )
               {
                  Action = sec.Action,
                  Parent = this.TranslateTableIndex( md, sec.Parent ) // TypeDef/MethodDef/AssemblyDef -> already processed
               };
               for ( var i = 0; i < sec.PermissionSets.Count; ++i )
               {
                  retVal.PermissionSets.Add( this.ProcessPermissionSet( md, inputIdx.Index, i, sec.PermissionSets[i] ) );
               }
               return retVal;
            } );

         // ClassLayout
         this.MergeTables(
            md => md.ClassLayouts,
            null,
            ( md, layout, inputIdx, thisIx ) => new ClassLayout()
            {
               ClassSize = layout.ClassSize,
               PackingSize = layout.PackingSize,
               Parent = this.TranslateTableIndex( md, layout.Parent ) // TypeDef -> already processed
            } );

         // FieldLayout
         this.MergeTables(
            md => md.FieldLayouts,
            null,
            ( md, layout, inputIdx, thisIdx ) => new FieldLayout()
            {
               Field = this.TranslateTableIndex( md, layout.Field ), // FieldDef -> already processed
               Offset = layout.Offset
            } );

         // MethodSemantics
         this.MergeTables(
            md => md.MethodSemantics,
            null,
            ( md, semantics, inputIdx, thisIdx ) => new MethodSemantics()
            {
               Associaton = this.TranslateTableIndex( md, semantics.Associaton ), // Event/Property -> already processed
               Attributes = semantics.Attributes,
               Method = this.TranslateTableIndex( md, semantics.Method ) // MethodDef -> already processed
            } );

         // MethodImpl
         this.MergeTables(
            md => md.MethodImplementations,
            null,
            ( md, impl, inputIdx, thisIdx ) => new MethodImplementation()
            {
               Class = this.TranslateTableIndex( md, impl.Class ), // TypeDef -> already processed
               MethodBody = this.TranslateTableIndex( md, impl.MethodBody ), // MethodDef/MemberRef -> already processed
               MethodDeclaration = this.TranslateTableIndex( md, impl.MethodDeclaration ) // MetodDef/MemberRef -> already processed
            } );

         // ImplMap
         this.MergeTables(
            md => md.MethodImplementationMaps,
            null,
            ( md, map, inputIdx, thisIdx ) => new MethodImplementationMap()
            {
               Attributes = map.Attributes,
               ImportName = map.ImportName,
               ImportScope = this.TranslateTableIndex( md, map.ImportScope ), // ModuleRef -> already processed
               MemberForwarded = this.TranslateTableIndex( md, map.MemberForwarded ) // FieldDef/MethodDef -> already processed
            } );

         // FieldRVA
         this.MergeTables(
            md => md.FieldRVAs,
            null,
            ( md, fRVA, inputIdx, thisIdx ) => new FieldRVA()
            {
               Data = fRVA.Data.CreateBlockCopy(),
               Field = this.TranslateTableIndex( md, fRVA.Field ) // FieldDef -> already processed
            } );

         // GenericParameterConstraint
         this.MergeTables(
            md => md.GenericParameterConstraintDefinitions,
            ( md, inputIdx, thisIdx ) => this._tableIndexMappings[md].ContainsKey( md.GenericParameterConstraintDefinitions.TableContents[inputIdx.Index].Owner ),
            ( md, constraint, inputIdx, thisIdx ) => new GenericParameterConstraintDefinition()
            {
               Constraint = this.TranslateTableIndex( md, constraint.Constraint ), // TypeDef/TypeRef/TypeSpec -> already processed
               Owner = this.TranslateTableIndex( md, constraint.Owner ) // GenericParameterDefinition -> already processed
            } );

         // ExportedType
         this.MergeTables(
            md => md.ExportedTypes,
            ( md, inputIdx, thisIdx ) => this.ExportedTypeRowStaysInTargetModule( md, md.ExportedTypes.TableContents[inputIdx.Index] ),
            ( md, eType, inputIdx, thisIdx ) => new ExportedType()
            {
               Attributes = eType.Attributes,
               Name = eType.Name,
               Namespace = eType.Namespace,
               TypeDefinitionIndex = eType.TypeDefinitionIndex
            } );
         // ExportedType may reference itself -> update Implementation only now
         this.SetTableIndices1(
            md => md.ExportedTypes,
            eType => eType.Implementation,
            ( eType, impl ) => eType.Implementation = impl
            );

         // FileReference
         this.MergeTables(
            md => md.FileReferences,
            ( md, inputIdx, thisIdx ) =>
            {
               var file = md.FileReferences.TableContents[inputIdx.Index];
               return !file.Attributes.ContainsMetadata() || true; // TODO skip all those netmodules that are part of input modules
            },
            ( md, file, inputIdx, thisIdx ) => new FileReference()
            {
               Attributes = file.Attributes,
               HashValue = file.HashValue,
               Name = file.Name
            } );

         // ManifestResource
         var resDic = new Dictionary<String, IList<Tuple<CILMetaData, ManifestResource>>>();
         foreach ( var inputModule in this._inputModules )
         {
            foreach ( var res in inputModule.ManifestResources.TableContents )
            {
               resDic
                  .GetOrAdd_NotThreadSafe( res.Name, n => new List<Tuple<CILMetaData, ManifestResource>>() )
                  .Add( Tuple.Create( inputModule, res ) );
            }
         }
         var resNameSet = new HashSet<String>();
         this.MergeTables(
            md => md.ManifestResources,
            ( md, inputIdx, thisIdx ) => resNameSet.Add( md.ManifestResources.TableContents[inputIdx.Index].Name ),
            ( md, resource, inputIdx, thisIdx ) => this.ProcessManifestResource( md, resource.Name, resDic )
            );

         // CustomAttributeDef
         this.MergeTables(
            md => md.CustomAttributeDefinitions,
            ( md, inputIdx, thisIdx ) =>
            {
               var parent = md.CustomAttributeDefinitions.TableContents[inputIdx.Index].Parent;
               Boolean retVal;
               switch ( parent.Table )
               {
                  case Tables.Assembly:
                  case Tables.Module:
                     // Skip altogether
                     retVal = false;
                     break;
                  default:
                     // Include only if there is matching row in target module
                     retVal = this._tableIndexMappings[md].ContainsKey( parent );
                     break;
               }
               return retVal;
            },
            ( md, ca, inputIdx, thisIdx ) => new CustomAttributeDefinition()
            {
               Parent = this.TranslateTableIndex( md, ca.Parent ), // TypeDef/MethodDef/FieldDef/ParamDef/TypeRef/InterfaceImpl/MemberRef/ModuleDef/DeclSecurity/PropertyDef/EventDef/StandAloneSig/ModuleRef/TypeSpec/AssemblyDef/AssemblyRef/File/ExportedType/ManifestResource/GenericParameterDefinition/GenericParameterDefinitionConstraint/MethodSpec -> AssemblyDef and ModuleDef are skipped, ModuleRef and AssemblyRef are skipped for those who don't have row in target module
               Signature = this.ProcessCustomAttributeSignature( md, inputIdx.Index, ca.Signature ),
               Type = this.TranslateTableIndex( md, ca.Type ) // Type: MethodDef/MemberRef -> already processed
            } );
      }

      private void ApplyAssemblyAndModuleCustomAttributes()
      {
         var attrSource = this._options.TargetAssemblyAttributeSource;
         this.CopyModuleAndAssemblyAttributesFrom(
            String.IsNullOrEmpty( attrSource ) ?
               ( this._options.CopyAttributes ?
                this._inputModules :
                this._primaryModule.Singleton() ) :
            this._moduleLoader.GetOrLoadMetaData( attrSource ).Singleton()
            );
      }

      private void CopyModuleAndAssemblyAttributesFrom( IEnumerable<CILMetaData> modules )
      {
         var targetCA = this._targetModule.CustomAttributeDefinitions.TableContents;
         foreach ( var m in modules )
         {
            if ( this._inputModules.IndexOf( m ) >= 0 )
            {
               var iCA = m.CustomAttributeDefinitions.TableContents;
               for ( var i = 0; i < iCA.Count; ++i )
               {
                  var ca = iCA[i];
                  if ( ca.Parent.Table == Tables.Assembly || ca.Parent.Table == Tables.Module )
                  {
                     targetCA.Add( new CustomAttributeDefinition()
                     {
                        Parent = ca.Parent,
                        Signature = this.ProcessCustomAttributeSignature( m, i, ca.Signature ),
                        Type = this.TranslateTableIndex( m, ca.Type )
                     } );
                  }
               }
            }
            else
            {
               // Table index translate methods want input module as source.
               // Most likely need to make input modules into IList<Tuple<CILMetaData, Boolean>>, where Boolean would indicate whether the module should be merged
               // Then MergeTables method would just simply not add the row to table if merged = false
               throw new NotImplementedException( "Custom attribute source from other than input assembly is not yet implemented." );
            }
         }
      }

      private void MergeTables<T>(
         Func<CILMetaData, MetaDataTable<T>> tableExtractor,
         Func<CILMetaData, TableIndex, TableIndex, Boolean> filter,
         Func<CILMetaData, T, TableIndex, TableIndex, T> copyFunc
         )
         where T : class
      {
         var targetMDTable = tableExtractor( this._targetModule );
         var tableKind = targetMDTable.TableKind;
         var targetTable = targetMDTable.TableContents;
         System.Diagnostics.Debug.Assert( targetTable.Count == 0, "Merging non-empty table in target module!" );
         foreach ( var md in this._inputModules )
         {
            var inputTable = tableExtractor( md ).TableContents;
            var thisMappings = this._tableIndexMappings[md];
            for ( var i = 0; i < inputTable.Count; ++i )
            {
               var inputIndex = new TableIndex( tableKind, i );
               var targetIndex = new TableIndex( tableKind, targetTable.Count );
               if ( filter == null || filter( md, inputIndex, targetIndex ) )
               {
                  this._targetTableIndexMappings.Add( targetIndex, Tuple.Create( md, i ) );
                  thisMappings.Add( inputIndex, targetIndex );

                  targetTable.Add( copyFunc( md, inputTable[i], inputIndex, targetIndex ) );
               }
            }
         }
      }

      private void SetTableIndicesNullable<T>(
         Func<CILMetaData, MetaDataTable<T>> tableExtractor,
         Func<T, TableIndex?> tableIndexGetter,
         Action<T, TableIndex> tableIndexSetter
         )
         where T : class
      {
         var tableKind = tableExtractor( this._targetModule ).TableKind;
         this.SetTableIndicesNullable(
            tableExtractor,
            i => this._targetTableIndexMappings[new TableIndex( tableKind, i )],
            tableIndexGetter,
            tableIndexSetter
            );
      }

      private void SetTableIndicesNullable<T>(
         Func<CILMetaData, MetaDataTable<T>> tableExtractor,
         Func<Int32, Tuple<CILMetaData, Int32>> inputInfoGetter,
         Func<T, TableIndex?> tableIndexGetter,
         Action<T, TableIndex> tableIndexSetter
         )
         where T : class
      {
         var targetTable = tableExtractor( this._targetModule ).TableContents;
         for ( var i = 0; i < targetTable.Count; ++i )
         {
            var inputInfo = inputInfoGetter( i );
            var inputModule = inputInfo.Item1;
            var inputTableIndexNullable = tableIndexGetter( tableExtractor( inputModule ).TableContents[inputInfo.Item2] );
            if ( inputTableIndexNullable.HasValue )
            {
               var inputTableIndex = inputTableIndexNullable.Value;
               var targetTableIndex = this._tableIndexMappings[inputModule][inputTableIndex];
               tableIndexSetter( targetTable[i], targetTableIndex );
            }
         }
      }

      private void SetTableIndices1<T>(
         Func<CILMetaData, MetaDataTable<T>> tableExtractor,
         Func<T, TableIndex> tableIndexGetter,
         Action<T, TableIndex> tableIndexSetter
         )
         where T : class
      {
         var targetMDTable = tableExtractor( this._targetModule );
         var tableKind = targetMDTable.TableKind;
         var targetTable = targetMDTable.TableContents;
         for ( var i = 0; i < targetTable.Count; ++i )
         {
            var inputInfo = this._targetTableIndexMappings[new TableIndex( tableKind, i )];
            var inputModule = inputInfo.Item1;
            var inputTableIndex = tableIndexGetter( tableExtractor( inputModule ).TableContents[inputInfo.Item2] );
            var targetTableIndex = this._tableIndexMappings[inputModule][inputTableIndex];
            tableIndexSetter( targetTable[i], targetTableIndex );
         }
      }

      private TableIndex TranslateTableIndex(
         CILMetaData inputModule,
         TableIndex inputTableIndex
         )
      {
         return this._tableIndexMappings[inputModule][inputTableIndex];
      }

      private Boolean ExportedTypeRowStaysInTargetModule( CILMetaData inputModule, ExportedType exportedType )
      {
         var impl = exportedType.Implementation;
         switch ( impl.Table )
         {
            case Tables.ExportedType:
               // Nested type - return whatever the enclosing type is
               return this.ExportedTypeRowStaysInTargetModule( inputModule, inputModule.ExportedTypes.TableContents[impl.Index] );
            case Tables.File:
               // The target module will be single-module assembly, so there shouldn't be any of these
               return false;
            case Tables.AssemblyRef:
               // This row stays only if the assembly ref is present in target module
               return this._tableIndexMappings[inputModule].ContainsKey( impl );
            default:
               return false;
         }
      }

      private static String CreateTypeString( MetaDataTable<TypeDefinition> mdTypeDefs, Int32 tDefIndex, IDictionary<Int32, Int32> enclosingTypeInfo )
      {
         var typeDefs = mdTypeDefs.TableContents;
         var sb = new StringBuilder( typeDefs[tDefIndex].Name );

         // Iterate: thisType -> enclosingType -> ... -> outMostEnclosingType
         // Use loop detection to avoid nasty stack overflows with faulty modules
         var last = tDefIndex;
         using ( var enumerator = tDefIndex.AsSingleBranchEnumerableWithLoopDetection(
            cur =>
            {
               Int32 enclosingTypeIdx;
               return enclosingTypeInfo.TryGetValue( cur, out enclosingTypeIdx ) ?
                  enclosingTypeIdx :
                  -1;
            },
            endCondition: cur => cur < 0,
            includeFirst: false
            ).GetEnumerator() )
         {
            while ( enumerator.MoveNext() )
            {
               var enclosingTypeIdx = enumerator.Current;
               sb.Insert( 0, "+" )
                  .Insert( 0, typeDefs[enclosingTypeIdx].Name );
               last = enclosingTypeIdx;
            }
         }

         var ns = typeDefs[last].Namespace;
         if ( !String.IsNullOrEmpty( ns ) )
         {
            sb.Insert( 0, "." )
               .Insert( 0, ns );
         }

         return sb.ToString();
      }

      private static String CreateTypeStringFromTopLevelType( String ns, String name )
      {
         return String.IsNullOrEmpty( ns ) ? name : ( ns + "." + name );
      }

      private TypeAttributes GetNewTypeAttributesForType(
         CILMetaData md,
         Int32 tDefIndex,
         Boolean hasEnclosingType,
         String typeString )
      {
         var attrs = md.TypeDefinitions.TableContents[tDefIndex].Attributes;
         // TODO cache all modules assembly def strings so we wouldn't call AssemblyDefinition.ToString() too excessively
         if ( this._options.Internalize
            && !this._primaryModule.Equals( md )
            && !this._excludeRegexes.Value.Any(
               reg => reg.IsMatch( typeString )
               || ( md.AssemblyDefinitions.RowCount > 0 && reg.IsMatch( "[" + md.AssemblyDefinitions.TableContents[0] + "]" + typeString ) )
               )
            )
         {
            // Have to make this type internal
            if ( !hasEnclosingType )
            {
               attrs &= ~TypeAttributes.VisibilityMask;
            }
            else if ( attrs.IsNestedPublic() )
            {
               attrs |= TypeAttributes.VisibilityMask;
               attrs &= ( ( ~TypeAttributes.VisibilityMask ) | TypeAttributes.NestedAssembly );
            }
            else if ( attrs.IsNestedFamily() || attrs.IsNestedFamilyORAssembly() )
            {
               attrs |= TypeAttributes.VisibilityMask;
               attrs &= ( ~TypeAttributes.VisibilityMask ) | TypeAttributes.NestedFamANDAssem;
            }
         }
         return attrs;
      }

      private Boolean IsDuplicateOK(
         ref String newName,
         String fullTypeString,
         ref String newFullTypeString,
         TypeAttributes newTypeAttrs,
         IDictionary<CILMetaData, IDictionary<Int32, String>> allTypeStrings,
         ref ISet<String> allTypeStringsSet
         )
      {
         var retVal = !this._options.Union
            && ( !newTypeAttrs.IsVisibleToOutsideOfDefinedAssembly()
               || this._options.AllowDuplicateTypes == null
               || this._options.AllowDuplicateTypes.Contains( fullTypeString )
               );
         if ( retVal )
         {
            // Have to rename
            if ( allTypeStringsSet == null )
            {
               allTypeStringsSet = new HashSet<String>( allTypeStrings.Values.SelectMany( dic => dic.Values ) );
            }

            var i = 2;
            var namePrefix = fullTypeString;
            do
            {
               fullTypeString = namePrefix + "_" + i;
               ++i;
            } while ( !allTypeStringsSet.Add( fullTypeString ) );

            newFullTypeString = fullTypeString;
            newName = newName + "_" + i;
         }
         return retVal;
      }

      private AbstractMarshalingInfo ProcessMarshalingInfo( CILMetaData inputModule, AbstractMarshalingInfo inputMarshalingInfo )
      {
         var retVal = inputMarshalingInfo.CreateDeepCopy();
         if ( retVal != null )
         {
            String typeStr;
            switch ( inputMarshalingInfo.MarshalingInfoKind )
            {
               case MarshalingInfoKind.SafeArray:
                  typeStr = ( (SafeArrayMarshalingInfo) inputMarshalingInfo ).UserDefinedType;
                  if ( !String.IsNullOrEmpty( typeStr ) )
                  {
                     typeStr = this.ProcessTypeString( inputModule, typeStr );
                     ( (SafeArrayMarshalingInfo) retVal ).UserDefinedType = typeStr;
                  }
                  break;
               case MarshalingInfoKind.Custom:
                  typeStr = ( (CustomMarshalingInfo) inputMarshalingInfo ).CustomMarshalerTypeName;
                  if ( !String.IsNullOrEmpty( typeStr ) )
                  {
                     typeStr = this.ProcessTypeString( inputModule, typeStr );
                     ( (CustomMarshalingInfo) retVal ).CustomMarshalerTypeName = typeStr;
                  }
                  break;
            }
         }

         return retVal;
      }

      private AbstractSecurityInformation ProcessPermissionSet( CILMetaData md, Int32 declSecurityIdx, Int32 permissionSetIdx, AbstractSecurityInformation inputSecurityInfo )
      {

         AbstractSecurityInformation retVal;
         switch ( inputSecurityInfo.SecurityInformationKind )
         {
            case SecurityInformationKind.Raw:
               this.Log( MessageLevel.Warning, "Unresolved security information BLOB in {0}, at table index {1}, permission set {2}.", this._moduleLoader.GetResourceFor( md ), declSecurityIdx, permissionSetIdx );
               var raw = (RawSecurityInformation) inputSecurityInfo;
               retVal = new RawSecurityInformation()
               {
                  ArgumentCount = raw.ArgumentCount,
                  Bytes = raw.Bytes.CreateBlockCopy()
               };
               break;
            case SecurityInformationKind.Resolved:
               var resolved = (SecurityInformation) inputSecurityInfo;
               var args = resolved.NamedArguments;
               resolved = new SecurityInformation( resolved.NamedArguments.Count );
               retVal = resolved;
               resolved.NamedArguments.AddRange( args.Select( arg => this.ProcessCANamedArg( md, arg ) ) );
               break;
            default:
               throw new NotSupportedException( "Unsupported security information kind: " + inputSecurityInfo.SecurityInformationKind + "." );
         }
         retVal.SecurityAttributeType = this.ProcessTypeString( md, inputSecurityInfo.SecurityAttributeType );
         return retVal;
      }

      private String ProcessTypeString( CILMetaData inputModule, String typeString )
      {
         String typeName, assemblyName;
         CILMetaData moduleHoldingType;
         if ( typeString.ParseAssemblyQualifiedTypeString( out typeName, out assemblyName ) )
         {
            AssemblyInformation aInfo; Boolean isFullPublicKey;
            if ( AssemblyInformation.TryParse( assemblyName, out aInfo, out isFullPublicKey ) )
            {
               var aRef = new AssemblyReference();
               if ( isFullPublicKey )
               {
                  aRef.Attributes = AssemblyFlags.PublicKey;
               }
               aInfo.DeepCopyContentsTo( aRef.AssemblyInformation );
               this._inputModulesAsAssemblyReferences.TryGetValue( aRef, out moduleHoldingType );
            }
            else
            {
               this.Log( MessageLevel.Warning, "Type string contained malformed assembly name: \"{0}\", skipping.", assemblyName );
               moduleHoldingType = null;
            }
         }
         else
         {
            moduleHoldingType = this._inputModuleTypeNamesInInputModule[inputModule].ContainsKey( typeName ) ? inputModule : null;
         }

         if ( moduleHoldingType != null )
         {
            // The module holding the type is one of the input modules -> check renames
            typeString = this._targetTypeNames[this._inputModuleTypeNamesInTargetModule[moduleHoldingType][typeName]];
         }
         return typeString;
      }

      private AbstractCustomAttributeSignature ProcessCustomAttributeSignature( CILMetaData md, Int32 caIdx, AbstractCustomAttributeSignature sig )
      {
         AbstractCustomAttributeSignature retVal;
         switch ( sig.CustomAttributeSignatureKind )
         {
            case CustomAttributeSignatureKind.Raw:
               this.Log( MessageLevel.Warning, "Unresolved custom attribute BLOB in {0}, at table index {1}.", this._moduleLoader.GetResourceFor( md ), caIdx );
               retVal = new RawCustomAttributeSignature()
               {
                  Bytes = ( (RawCustomAttributeSignature) sig ).Bytes.CreateBlockCopy()
               };
               break;
            case CustomAttributeSignatureKind.Resolved:
               var resolved = (CustomAttributeSignature) sig;
               var resolvedRetVal = new CustomAttributeSignature( resolved.TypedArguments.Count, resolved.NamedArguments.Count );
               resolvedRetVal.TypedArguments.AddRange( resolved.TypedArguments.Select( arg => this.ProcessCATypedArg( md, arg ) ) );
               resolvedRetVal.NamedArguments.AddRange( resolved.NamedArguments.Select( arg => this.ProcessCANamedArg( md, arg ) ) );
               retVal = resolvedRetVal;
               break;
            default:
               throw new NotSupportedException( "Unsupported custom attribute signature kind: " + sig.CustomAttributeSignatureKind + "." );
         }
         return retVal;
      }

      private CustomAttributeNamedArgument ProcessCANamedArg( CILMetaData inputModule, CustomAttributeNamedArgument arg )
      {
         return new CustomAttributeNamedArgument()
         {
            IsField = arg.IsField,
            Name = arg.Name,
            FieldOrPropertyType = this.ProcessCATypedArgType( inputModule, arg.FieldOrPropertyType ),
            Value = this.ProcessCATypedArg( inputModule, arg.Value )
         };
      }

      private CustomAttributeTypedArgument ProcessCATypedArg( CILMetaData inputModule, CustomAttributeTypedArgument arg )
      {
         return new CustomAttributeTypedArgument()
         {
            Value = this.ProcessCATypedArgValue( inputModule, arg.Value )
         };
      }

      private Object ProcessCATypedArgValue( CILMetaData inputModule, Object value )
      {
         if ( value != null )
         {
            var complex = value as CustomAttributeTypedArgumentValueComplex;
            if ( complex != null )
            {
               switch ( complex.CustomAttributeTypedArgumentValueKind )
               {
                  case CustomAttributeTypedArgumentValueKind.Type:
                     value = new CustomAttributeValue_TypeReference( this.ProcessTypeString( inputModule, ( (CustomAttributeValue_TypeReference) value ).TypeString ) );
                     break;
                  case CustomAttributeTypedArgumentValueKind.Enum:
                     var enumValue = (CustomAttributeValue_EnumReference) value;
                     value = new CustomAttributeValue_EnumReference( this.ProcessTypeString( inputModule, enumValue.EnumType ), enumValue.EnumValue );
                     break;
                  case CustomAttributeTypedArgumentValueKind.Array:
                     var array = (CustomAttributeValue_Array) value;
                     var oldArray = array.Array;
                     var newArray = oldArray == null ? null : Array.CreateInstance( oldArray.GetType().GetElementType(), oldArray.Length );
                     value = new CustomAttributeValue_Array( newArray, this.ProcessCATypedArgType( inputModule, array.ArrayElementType ) );
                     if ( newArray != null )
                     {
                        for ( var i = 0; i < newArray.Length; ++i )
                        {
                           newArray.SetValue( this.ProcessCATypedArgValue( inputModule, oldArray.GetValue( i ) ), i );
                        }
                     }
                     break;
                  default:
                     throw new NotSupportedException( "Unsupported custom attribute typed argument complex value kind: " + complex.CustomAttributeTypedArgumentValueKind + "." );
               }
            }
         }

         return value;
      }

      private CustomAttributeArgumentType ProcessCATypedArgType( CILMetaData inputModule, CustomAttributeArgumentType type )
      {
         switch ( type.ArgumentTypeKind )
         {
            case CustomAttributeArgumentTypeKind.Array:
               return new CustomAttributeArgumentTypeArray()
               {
                  ArrayType = this.ProcessCATypedArgType( inputModule, ( (CustomAttributeArgumentTypeArray) type ).ArrayType )
               };
            case CustomAttributeArgumentTypeKind.Simple:
               return type;
            case CustomAttributeArgumentTypeKind.TypeString:
               return new CustomAttributeArgumentTypeEnum()
               {
                  TypeString = this.ProcessTypeString( inputModule, ( (CustomAttributeArgumentTypeEnum) type ).TypeString )
               };
            default:
               throw new NotSupportedException( "Unsupported custom attribute typed argument type: " + type.ArgumentTypeKind + "." );
         }
      }

      private ManifestResource ProcessManifestResource(
         CILMetaData md,
         String resourceName,
         IDictionary<String, IList<Tuple<CILMetaData, ManifestResource>>> resourcesByName
         )
      {
         var list = resourcesByName[resourceName];
         if ( list.Count > 1 )
         {
            if ( !this._options.AllowDuplicateResources )
            {
               this.Log( MessageLevel.Warning, "Ignoring duplicate resource {0} in modules {1}.", resourceName, String.Join( ", ", list.Skip( 1 ).Select( t => this._moduleLoader.GetResourceFor( t.Item1 ) ) ) );
               var first = list[0];
               list = new List<Tuple<CILMetaData, ManifestResource>>()
               {
                  first
               };
            }
            else if ( list.All( t => !t.Item2.IsEmbeddedResource() ) )
            {
               // All are external resource -> just use one
               this.Log( MessageLevel.Warning, "Multiple external resource {0} in modules {1}, using resource from first module.", resourceName, String.Join( ", ", list.Select( t => this._moduleLoader.GetResourceFor( t.Item1 ) ) ) );
               var first = list[0];
               list = new List<Tuple<CILMetaData, ManifestResource>>()
               {
                  first
               };
            }
            else if ( list.Any( t => !t.Item2.IsEmbeddedResource() ) )
            {
               var embedded = list.Where( t => t.Item2.IsEmbeddedResource() );
               this.Log( MessageLevel.Warning, "Resource {0} is not embedded in all input modules, merging only embedded ones from modules {1}.", resourceName, String.Join( ", ", embedded.Select( t => this._moduleLoader.GetResourceFor( t.Item1 ) ) ) );
               list = embedded.ToList();
            }
         }

         var firstRes = list[0].Item2;
         var retVal = new ManifestResource()
         {
            Attributes = firstRes.Attributes,
            Implementation = firstRes.Implementation,
            Name = resourceName,
            Offset = firstRes.Offset
         };

         if ( firstRes.IsEmbeddedResource() )
         {
            // Then all resources are embedded
            using ( var strm = new MemoryStream() )
            {
               var rw = new System.Resources.ResourceWriter( strm );
               foreach ( var tuple in list )
               {
                  var inputResource = tuple.Item2;
                  var data = inputResource.DataInCurrentFile;
                  Boolean wasResourceManager;
                  foreach ( var resx in MResourcesIO.GetResourceInfo( data, out wasResourceManager ) )
                  {
                     var resName = resx.Name;
                     var resType = resx.Type;
                     if ( !resx.IsUserDefinedType && String.Equals( "ResourceTypeCode.String", resType ) )
                     {
                        // In case there is textual information about types serialized, have to fix that.
                        var idx = resx.DataOffset;
                        var strlen = data.ReadInt32Encoded7Bit( ref idx );
                        rw.AddResource( resName, this.ProcessTypeString( md, data.ReadStringWithEncoding( ref idx, strlen, Encoding.UTF8 ) ) );
                     }
                     else
                     {
                        var newTypeStr = this.ProcessTypeString( md, resType );
                        if ( String.Equals( newTypeStr, resType ) )
                        {
                           // Predefined ResourceTypeCode or pure reference type, add right away
                           var array = new Byte[resx.DataSize];
                           var dataStart = resx.DataOffset;
                           data.BlockCopyFrom( ref dataStart, array );
                           rw.AddResourceData( resName, resType, array );
                        }
                        else
                        {
                           // Have to fix records one by one
                           var idx = resx.DataOffset;
                           var records = MResourcesIO.ReadNRBFRecords( data, ref idx, idx + resx.DataSize );
                           foreach ( var rec in records )
                           {
                              this.ProcessNRBFRecord( md, rec );
                           }
                           var strm2 = new MemoryStream();
                           MResourcesIO.WriteNRBFRecords( records, strm2 );
                           rw.AddResourceData( resName, newTypeStr, strm2.ToArray() );
                        }
                     }
                  }
               }

               rw.Generate();
               retVal.DataInCurrentFile = strm.ToArray();
            }
         }
         else
         {
            // None are embedded, meaning that there is only one resource, just use its implementation table index
            retVal.Implementation = firstRes.Implementation;
         }

         return retVal;
      }

      private void ProcessNRBFRecord( CILMetaData inputModule, AbstractRecord record )
      {
         if ( record != null )
         {
            switch ( record.Kind )
            {
               case RecordKind.String:
                  ( (StringRecord) record ).StringValue = this.ProcessTypeString( inputModule, ( (StringRecord) record ).StringValue );
                  break;
               case RecordKind.Class:
                  var claas = (ClassRecord) record;
                  this.ProcessNRBFTypeInfo( claas );
                  foreach ( var member in claas.Members )
                  {
                     this.ProcessNRBFTypeInfo( member );
                     this.ProcessNRBFRecord( inputModule, member.Value as AbstractRecord );
                  }
                  break;
               case RecordKind.Array:
                  foreach ( var val in ( (ArrayRecord) record ).ValuesAsVector )
                  {
                     this.ProcessNRBFRecord( inputModule, val as AbstractRecord );
                  }
                  break;
               case RecordKind.PrimitiveWrapper:
                  var wrapper = (PrimitiveWrapperRecord) record;
                  if ( wrapper.Value is String )
                  {
                     wrapper.Value = this.ProcessTypeString( inputModule, wrapper.Value as String );
                  }
                  break;
            }
         }
      }

      private void ProcessNRBFTypeInfo( ElementWithTypeInfo element )
      {
         var tmp = element.AssemblyName;
         if ( tmp != null )
         {
            CILMetaData referencedInputAssembly;
            element.AssemblyName = this.ProcessAssemblyName( tmp, out referencedInputAssembly );
            element.TypeName = this.ProcessTypeName( referencedInputAssembly, element.TypeName );
         }
      }

      private String ProcessTypeName( CILMetaData referencedInputAssembly, String typeName )
      {
         if ( referencedInputAssembly != null )
         {
            Int32 tDefIdx;
            if ( this._inputModuleTypeNamesInTargetModule[referencedInputAssembly].TryGetValue( typeName, out tDefIdx ) )
            {
               typeName = this._targetTypeNames[tDefIdx];
            }
            else
            {
               this.Log( MessageLevel.Warning, "Resource of type {0} from input module {1} did not have corresponding type within module itself.", typeName, this._moduleLoader.GetResourceFor( referencedInputAssembly ) );
            }
         }
         return typeName;
      }

      private String ProcessAssemblyName( String assName, out CILMetaData referencedInputAssembly )
      {
         AssemblyInformation assInfo; Boolean isFullPublicKey;
         referencedInputAssembly = null;
         if ( AssemblyInformation.TryParse( assName, out assInfo, out isFullPublicKey ) )
         {

            var aRef = new AssemblyReference();
            assInfo.DeepCopyContentsTo( aRef.AssemblyInformation );
            if ( isFullPublicKey )
            {
               aRef.Attributes = AssemblyFlags.PublicKey;
            }
            if ( this._inputModulesAsAssemblyReferences.TryGetValue( aRef, out referencedInputAssembly ) )
            {
               // TODO maybe assembly name should be null/empty, since in same assembly??
               assName = this._targetModule.AssemblyDefinitions.TableContents[0].ToString();
            }
         }

         return assName;
      }

      private void Log( MessageLevel mLevel, String formatString, params Object[] args )
      {
         this._merger.Log( mLevel, formatString, args );
      }

      private CILMergeException NewCILMergeException( ExitCode code, String message, Exception inner = null )
      {
         return this._merger.NewCILMergeException( code, message, inner );
      }

      private void DoPotentiallyInParallel<T>( IEnumerable<T> enumerable, Action<Boolean, T> action )
      {
         this._merger.DoPotentiallyInParallel( enumerable, action );
      }

      protected override void Dispose( Boolean disposing )
      {
         if ( disposing )
         {
            this._moduleLoader.DisposeSafely();
         }
      }
   }

   public class CILMergeException : Exception
   {
      private readonly ExitCode _exitCode;

      internal CILMergeException( ExitCode exitCode, String msg, Exception inner )
         : base( msg, inner )
      {
         this._exitCode = exitCode;
      }

      public ExitCode ExitCode
      {
         get
         {
            return this._exitCode;
         }
      }
   }

   internal static class E_Internal
   {


      internal static Boolean TryAdd_NotThreadSafe<TKey, TValue>( this IDictionary<TKey, TValue> dic, TKey key, TValue value )
      {
         var retVal = !dic.ContainsKey( key );
         if ( retVal )
         {
            dic.Add( key, value );
         }

         return retVal;
      }


   }
}
