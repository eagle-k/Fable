module Fable.Cli.Main

open System
open System.Collections.Generic
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics

open Fable
open Fable.AST
open Fable.Transforms
open Fable.Transforms.State
open ProjectCracker

module private Util =
    let loadType (r: PluginRef): Type =
        /// Prevent ReflectionTypeLoadException
        /// From http://stackoverflow.com/a/7889272
        let getTypes (asm: System.Reflection.Assembly) =
            let mutable types: Option<Type[]> = None
            try
                types <- Some(asm.GetTypes())
            with
            | :? System.Reflection.ReflectionTypeLoadException as e ->
                types <- Some e.Types
            match types with
            | None -> Seq.empty
            | Some types ->
                types |> Seq.filter ((<>) null)

        // The assembly may be already loaded, so use `LoadFrom` which takes
        // the copy in memory unlike `LoadFile`, see: http://stackoverflow.com/a/1477899
        System.Reflection.Assembly.LoadFrom(r.DllPath)
        |> getTypes
        // Normalize type name
        |> Seq.tryFind (fun t -> t.FullName.Replace("+", ".") = r.TypeFullName)
        |> function
            | Some t ->
                $"Loaded %s{r.TypeFullName} from %s{File.getRelativePathFromCwd r.DllPath}"
                |> Log.always; t
            | None -> failwithf "Cannot find %s in %s" r.TypeFullName r.DllPath

    let getSourceFiles (opts: FSharpProjectOptions) =
        opts.OtherOptions |> Array.filter (fun path -> path.StartsWith("-") |> not)

    let splitVersion (version: string) =
        match Version.TryParse(version) with
        | true, v -> v.Major, v.Minor, v.Revision
        | _ -> 0, 0, 0

    // let checkFableCoreVersion (checkedProject: FSharpCheckProjectResults) =
    //     for ref in checkedProject.ProjectContext.GetReferencedAssemblies() do
    //         if ref.SimpleName = "Fable.Core" then
    //             let version = System.Text.RegularExpressions.Regex.Match(ref.QualifiedName, @"Version=(\d+\.\d+\.\d+)")
    //             let expectedMajor, expectedMinor, _ = splitVersion Literals.CORE_VERSION
    //             let actualMajor, actualMinor, _ = splitVersion version.Groups.[1].Value
    //             if not(actualMajor = expectedMajor && actualMinor = expectedMinor) then
    //                 failwithf "Fable.Core v%i.%i detected, expecting v%i.%i" actualMajor actualMinor expectedMajor expectedMinor
    //             // else printfn "Fable.Core version matches"

    let measureTime (f: unit -> 'a) =
        let sw = Diagnostics.Stopwatch.StartNew()
        let res = f()
        sw.Stop()
        res, sw.ElapsedMilliseconds

    let measureTimeAsync (f: unit -> Async<'a>) = async {
        let sw = Diagnostics.Stopwatch.StartNew()
        let! res = f()
        sw.Stop()
        return res, sw.ElapsedMilliseconds
    }

    let formatException file ex =
        let rec innerStack (ex: Exception) =
            if isNull ex.InnerException then ex.StackTrace else innerStack ex.InnerException
        let stack = innerStack ex
        $"[ERROR] %s{file}\n%s{ex.Message}\n%s{stack}"

    let formatLog (log: Log) =
        match log.FileName with
        | None -> log.Message
        | Some file ->
            let severity =
                match log.Severity with
                | Severity.Warning -> "warning"
                | Severity.Error -> "error"
                | Severity.Info -> "info"
            match log.Range with
            | Some r -> $"%s{file}(%i{r.start.line},%i{r.start.column}): (%i{r.``end``.line},%i{r.``end``.column}) %s{severity} %s{log.Tag}: %s{log.Message}"
            | None -> $"%s{file}(1,1): %s{severity} %s{log.Tag}: %s{log.Message}"

    let getFSharpErrorLogs (proj: Project) =
        proj.Errors
        |> Array.map (fun er ->
            let severity =
                match er.Severity with
                | FSharpDiagnosticSeverity.Hidden
                | FSharpDiagnosticSeverity.Info -> Severity.Info
                | FSharpDiagnosticSeverity.Warning -> Severity.Warning
                | FSharpDiagnosticSeverity.Error -> Severity.Error

            let range =
                { start={ line=er.StartLine; column=er.StartColumn+1}
                  ``end``={ line=er.EndLine; column=er.EndColumn+1}
                  identifierName = None }

            let msg = $"%s{er.Message} (code %i{er.ErrorNumber})"

            Log.Make(severity, msg, fileName=er.FileName, range=range, tag="FSHARP")
        )

    let hasWatchDependency (path: string) (dirtyFiles: Set<string>) watchDependencies =
        match Map.tryFind path watchDependencies with
        | None -> false
        | Some watchDependencies ->
            watchDependencies |> Array.exists (fun p -> Set.contains p dirtyFiles)

    let changeFsExtension isInFableHiddenDir filePath fileExt =
        let fileExt =
            // Prevent conflicts in package sources as they may include
            // JS files with same name as the F# .fs file
            if fileExt = ".js" && isInFableHiddenDir then
                CompilerOptionsHelper.DefaultExtension
            else fileExt
        Path.replaceExtension fileExt filePath

    let getOutJsPath (cliArgs: CliArgs) dedupTargetDir file =
        let fileExt = cliArgs.CompilerOptions.FileExtension
        let isInFableHiddenDir = Naming.isInFableHiddenDir file
        match cliArgs.OutDir with
        | Some outDir ->
            let projDir = IO.Path.GetDirectoryName cliArgs.ProjectFile
            let absPath = Imports.getTargetAbsolutePath dedupTargetDir file projDir outDir
            changeFsExtension isInFableHiddenDir absPath fileExt
        | None ->
            changeFsExtension isInFableHiddenDir file fileExt

    type FileWriter(sourcePath: string, targetPath: string, cliArgs: CliArgs, dedupTargetDir) =
        // In imports *.ts extensions have to be converted to *.js extensions instead
        let fileExt =
            let fileExt = cliArgs.CompilerOptions.FileExtension
            if fileExt.EndsWith(".ts") then Path.replaceExtension ".js" fileExt else fileExt
        let targetDir = Path.GetDirectoryName(targetPath)
        let stream = new IO.StreamWriter(targetPath)
        let mapGenerator = lazy (SourceMapSharp.SourceMapGenerator(?sourceRoot = cliArgs.SourceMapsRoot))
        interface BabelPrinter.Writer with
            member _.Write(str) =
                stream.WriteAsync(str) |> Async.AwaitTask
            member _.EscapeJsStringLiteral(str) =
                Web.HttpUtility.JavaScriptStringEncode(str)
            member _.MakeImportPath(path) =
                let projDir = IO.Path.GetDirectoryName(cliArgs.ProjectFile)
                let path = Imports.getImportPath dedupTargetDir sourcePath targetPath projDir cliArgs.OutDir path
                if path.EndsWith(".fs") then
                    let isInFableHiddenDir = Path.Combine(targetDir, path) |> Naming.isInFableHiddenDir
                    changeFsExtension isInFableHiddenDir path fileExt
                else path
            member _.Dispose() = stream.Dispose()
            member _.AddSourceMapping((srcLine, srcCol, genLine, genCol, name)) =
                if cliArgs.SourceMaps then
                    let generated: SourceMapSharp.Util.MappingIndex = { line = genLine; column = genCol }
                    let original: SourceMapSharp.Util.MappingIndex = { line = srcLine; column = srcCol }
                    let targetPath = Path.normalizeFullPath targetPath
                    let sourcePath = Path.getRelativeFileOrDirPath false targetPath false sourcePath
                    mapGenerator.Force().AddMapping(generated, original, source=sourcePath, ?name=name)
        member _.SourceMap =
            mapGenerator.Force().toJSON()

    let compileFile isRecompile (cliArgs: CliArgs) dedupTargetDir (com: CompilerImpl) = async {
        try
            let babel =
                FSharp2Fable.Compiler.transformFile com
                |> FableTransforms.transformFile com
                |> Fable2Babel.Compiler.transformFile com

            let outPath = getOutJsPath cliArgs dedupTargetDir com.CurrentFile

            // ensure directory exists
            let dir = IO.Path.GetDirectoryName outPath
            if not (IO.Directory.Exists dir) then IO.Directory.CreateDirectory dir |> ignore

            // write output to file
            let! sourceMap = async {
                use writer = new FileWriter(com.CurrentFile, outPath, cliArgs, dedupTargetDir)
                do! BabelPrinter.run writer babel
                return if cliArgs.SourceMaps then Some writer.SourceMap else None
            }

            // write source map to file
            match sourceMap with
            | Some sourceMap ->
                let mapPath = outPath + ".map"
                do! IO.File.AppendAllLinesAsync(outPath, [$"//# sourceMappingURL={IO.Path.GetFileName(mapPath)}"]) |> Async.AwaitTask
                use fs = IO.File.Open(mapPath, IO.FileMode.Create)
                do! sourceMap.SerializeAsync(fs) |> Async.AwaitTask
            | None -> ()

            "Compiled " + File.getRelativePathFromCwd com.CurrentFile
            |> Log.verboseOrIf isRecompile

            return Ok {| File = com.CurrentFile
                         Logs = com.Logs
                         WatchDependencies = com.WatchDependencies |}
        with e ->
            return Error {| File = com.CurrentFile
                            Exception = e |}
    }

module FileWatcherUtil =
    let getCommonBaseDir (files: string list) =
        let withTrailingSep d = $"%s{d}%c{IO.Path.DirectorySeparatorChar}"
        files
        |> List.map IO.Path.GetDirectoryName
        |> List.distinct
        |> List.sortBy (fun f -> f.Length)
        |> function
            | [] -> failwith "Empty list passed to watcher"
            | [dir] -> dir
            | dir::restDirs ->
                let rec getCommonDir (dir: string) =
                    // it's important to include a trailing separator when comparing, otherwise things
                    // like ["a/b"; "a/b.c"] won't get handled right
                    // https://github.com/fable-compiler/Fable/issues/2332
                    let dir' = withTrailingSep dir
                    if restDirs |> List.forall (fun d -> (withTrailingSep d).StartsWith dir') then dir
                    else
                        match IO.Path.GetDirectoryName(dir) with
                        | null -> failwith "No common base dir"
                        | dir -> getCommonDir dir
                getCommonDir dir

open Util
open FileWatcher
open FileWatcherUtil

let caseInsensitiveSet(items: string seq): ISet<string> =
    let s = HashSet(items)
    for i in items do s.Add(i) |> ignore
    s :> _

type FsWatcher() =
    let globFilters = [ "*.fs"; "*.fsx"; "*.fsproj" ]
    let createWatcher () =
        let usePolling =
            // This is the same variable used by dotnet watch
            let envVar = Environment.GetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER")
            not (isNull envVar) &&
                (envVar.Equals("1", StringComparison.OrdinalIgnoreCase)
                || envVar.Equals("true", StringComparison.OrdinalIgnoreCase))

        let watcher: IFileSystemWatcher =
            if usePolling then
                Log.always("Using polling watcher.")
                // Ignored for performance reasons:
                let ignoredDirectoryNameRegexes = [ "(?i)node_modules"; "(?i)bin"; "(?i)obj"; "\..+" ]
                upcast new ResetablePollingFileWatcher(globFilters, ignoredDirectoryNameRegexes)
            else
                upcast new DotnetFileWatcher(globFilters)
        watcher

    let watcher = createWatcher ()
    let observable = Observable.SingleObservable(fun () ->
        watcher.EnableRaisingEvents <- false)

    do
        watcher.OnFileChange.Add(fun path -> observable.Trigger(path))
        watcher.OnError.Add(fun ev ->
            Log.always("Watcher found an error, some events may have been lost.")
            Log.verbose(lazy ev.GetException().Message)
        )

    member _.Observe(filesToWatch: string list) =
        let commonBaseDir = getCommonBaseDir filesToWatch
        Log.always("Watching " + File.getRelativePathFromCwd commonBaseDir)

        // It may happen we get the same path with different case in case-insensitive file systems
        // https://github.com/fable-compiler/Fable/issues/2277#issuecomment-737748220
        let filePaths = caseInsensitiveSet filesToWatch
        watcher.BasePath <- commonBaseDir
        watcher.EnableRaisingEvents <- true

        observable
        |> Observable.choose (fun fullPath ->
            let fullPath = Path.normalizePath fullPath
            if filePaths.Contains(fullPath)
            then Some fullPath
            else None)
        |> Observable.throttle 200.
        |> Observable.map caseInsensitiveSet

// TODO: Check the path is actually normalized?
type File(normalizedFullPath: string) =
    let mutable sourceHash = None
    member _.NormalizedFullPath = normalizedFullPath
    member _.ReadSource() =
        match sourceHash with
        | Some h -> h, lazy File.readAllTextNonBlocking normalizedFullPath
        | None ->
            let source = File.readAllTextNonBlocking normalizedFullPath
            let h = hash source
            sourceHash <- Some h
            h, lazy source

type ProjectCracked(projFile: string,
                    sourceFiles: File array,
                    fableCompilerOptions: CompilerOptions,
                    crackerResponse: CrackerResponse) =

    member _.ProjectFile = projFile
    member _.FableOptions = fableCompilerOptions
    member _.ProjectOptions = crackerResponse.ProjectOptions
    member _.Packages = crackerResponse.Packages
    member _.SourceFiles = sourceFiles

    member _.MakeCompiler(currentFile, project, outDir) =
        let fableLibDir = Path.getRelativePath currentFile crackerResponse.FableLibDir
        CompilerImpl(currentFile, project, fableCompilerOptions, fableLibDir, ?outDir=outDir)

    member _.MapSourceFiles(f) =
        ProjectCracked(projFile, Array.map f sourceFiles, fableCompilerOptions, crackerResponse)

    static member Init(cliArgs: CliArgs) =
        let result, ms = measureTime <| fun () ->
            CrackerOptions(fableOpts = cliArgs.CompilerOptions,
                           fableLib = cliArgs.FableLibraryPath,
                           outDir = cliArgs.OutDir,
                           configuration = cliArgs.Configuration,
                           exclude = cliArgs.Exclude,
                           replace = cliArgs.Replace,
                           noCache = cliArgs.NoCache,
                           noRestore = cliArgs.NoRestore,
                           projFile = cliArgs.ProjectFile)
            |> getFullProjectOpts

        // We display "parsed" becaused "projCracked" may not be undersood by users
        Log.always $"Project parsed in %i{ms}ms"
        Log.verbose(lazy
            let proj = File.getRelativePathFromCwd cliArgs.ProjectFile
            let opts = result.ProjectOptions.OtherOptions |> String.concat "\n   "
            $"F# PROJECT: %s{proj}\n   %s{opts}")

        let sourceFiles = getSourceFiles result.ProjectOptions |> Array.map File
        ProjectCracked(cliArgs.ProjectFile, sourceFiles, cliArgs.CompilerOptions, result)

type ProjectChecked(project: Project, checker: FSharpChecker) =

    static let checkProject (config: ProjectCracked) (checker: FSharpChecker) = async {
        Log.always("Compiling " + File.getRelativePathFromCwd config.ProjectOptions.ProjectFileName + "...")
        let! result, ms = measureTimeAsync <| fun () ->
            checker.ParseAndCheckProject(config.ProjectOptions)
        Log.always $"F# compilation finished in %i{ms}ms"
        return result
    }

    member _.Project = project
    member _.Checker = checker

    static member Init(config: ProjectCracked, ?checker) = async {
        let checker =
            match checker with
            | Some checker -> checker
            | None -> FSharpChecker.Create(keepAssemblyContents=true)

        let! checkResults = checkProject config checker
        let proj = Project(config.ProjectFile,
                           checkResults,
                           getPlugin=loadType,
                           optimizeFSharpAst=config.FableOptions.OptimizeFSharpAst,
                           rootModule=config.FableOptions.RootModule)
        return ProjectChecked(proj, checker)
    }

    member this.Update(config: ProjectCracked) = async {
        let! checkResults = checkProject config this.Checker
        let proj = this.Project.Update(checkResults)
        return ProjectChecked(proj, checker)
    }

type State =
    { CliArgs: CliArgs
      ProjectCrackedAndChecked: (ProjectCracked * ProjectChecked) option
      WatchDependencies: Map<string, string[]>
      PendingFilesToCompile: string[]
      ErroredFiles: Set<string>
      DeduplicateDic: Collections.Concurrent.ConcurrentDictionary<string, string>
      Watcher: FsWatcher option
      HasCompiledOnce: bool }
    member this.GetOrAddDeduplicateTargetDir (importDir: string) addTargetDir =
        // importDir must be trimmed and normalized by now, but lower it just in case
        // as some OS use case insensitive paths
        let importDir = importDir.ToLower()
        this.DeduplicateDic.GetOrAdd(importDir, fun _ ->
            set this.DeduplicateDic.Values
            |> addTargetDir)

    static member Create(cliArgs, isWatch: bool) =
        { CliArgs = cliArgs
          ProjectCrackedAndChecked = None
          WatchDependencies = Map.empty
          Watcher = if isWatch then Some(FsWatcher()) else None
          DeduplicateDic = Collections.Concurrent.ConcurrentDictionary()
          PendingFilesToCompile = [||]
          ErroredFiles = Set.empty
          HasCompiledOnce = false }

let rec startCompilation (changes: ISet<string>) (state: State) = async {
    let state =
        match state.CliArgs.RunProcess with
        | Some runProc when runProc.IsFast ->
            let workingDir = state.CliArgs.RootDir
            let exeFile =
                File.tryNodeModulesBin workingDir runProc.ExeFile
                |> Option.defaultValue runProc.ExeFile
            Process.start workingDir exeFile runProc.Args
            { state with CliArgs = { state.CliArgs with RunProcess = None } }
        | _ -> state

    // TODO: Use Result here to fail more gracefully if FCS crashes
    let! projCracked, projChecked, filesToCompile = async {
        match state.ProjectCrackedAndChecked with
        | Some(projCracked, projChecked) ->
            let fsprojChanged, oldFiles, projCracked =
                if changes.Contains(state.CliArgs.ProjectFile)
                    // For performance reasons, don't crack .fsx scripts for every change
                    && not(state.CliArgs.ProjectFile.EndsWith(".fsx")) then
                    let oldFiles =
                        projCracked.SourceFiles
                        |> Array.map (fun f -> f.NormalizedFullPath)
                        |> Set
                    true, oldFiles, ProjectCracked.Init(state.CliArgs)
                else false, Set.empty, projCracked

            let dirtyFiles =
                // If Fable compilation didn't happen yet (because of errors) just compile all files
                if not state.HasCompiledOnce then
                    projCracked.SourceFiles
                    |> Array.map (fun f -> f.NormalizedFullPath)
                else
                    projCracked.SourceFiles
                    |> Array.choose (fun file ->
                        let path = file.NormalizedFullPath
                        if changes.Contains(path)
                            || (fsprojChanged && not(Set.contains path oldFiles))
                            then Some path
                        else None)

            if Array.isEmpty dirtyFiles then
                return projCracked, projChecked, [||]
            else
                let dirtyFiles = set dirtyFiles
                let projCracked = projCracked.MapSourceFiles(fun file ->
                    if Set.contains file.NormalizedFullPath dirtyFiles then
                        File(file.NormalizedFullPath) // Clear the cached source hash
                    else file)
                let! projChecked =
                    if fsprojChanged then ProjectChecked.Init(projCracked, projChecked.Checker)
                    else projChecked.Update(projCracked)
                let filesToCompile =
                    projCracked.SourceFiles
                    |> Array.choose (fun file ->
                        let path = file.NormalizedFullPath
                        if Set.contains path dirtyFiles
                            || hasWatchDependency path dirtyFiles state.WatchDependencies then Some path
                        else None)
                return projCracked, projChecked, filesToCompile
        | None ->
            let projCracked = ProjectCracked.Init(state.CliArgs)
            let! projChecked = ProjectChecked.Init(projCracked)
            let filesToCompile =
                projCracked.SourceFiles
                |> Array.map (fun f -> f.NormalizedFullPath)
                |> fun files ->
                    if Option.isNone state.Watcher || state.CliArgs.NoCache then files
                    else
                        // Skip files that have a more recent JS version
                        let filesToCompile =
                            files |> Array.skipWhile (fun file ->
                                try
                                    let jsFile = getOutJsPath state.CliArgs state.GetOrAddDeduplicateTargetDir file
                                    IO.File.Exists(jsFile) && IO.File.GetLastWriteTime(jsFile) > IO.File.GetLastWriteTime(file)
                                with _ -> false)
                        if filesToCompile.Length < files.Length then
                            Log.always("Skipping Fable compilation of up-to-date JS files")
                        filesToCompile
            return projCracked, projChecked, filesToCompile
        }

    let filesToCompile =
        filesToCompile
        |> Array.filter (fun file -> file.EndsWith(".fs") || file.EndsWith(".fsx"))
        |> Array.append (Set.toArray state.ErroredFiles)
        |> Array.append state.PendingFilesToCompile
        |> Array.distinct

    let logs = getFSharpErrorLogs projChecked.Project
    let hasFSharpError = logs |> Array.exists (fun l -> l.Severity = Severity.Error)

    let! logs, state = async {
        // Skip Fable compilation if there are F# errors
        if hasFSharpError then
            return
                if not state.HasCompiledOnce then logs, state
                else logs, { state with PendingFilesToCompile = filesToCompile }
        else
            let! results, ms = measureTimeAsync <| fun () ->
                filesToCompile
                |> Array.map (fun file ->
                    projCracked.MakeCompiler(file, projChecked.Project, state.CliArgs.OutDir)
                    |> compileFile state.HasCompiledOnce state.CliArgs state.GetOrAddDeduplicateTargetDir)
                |> Async.Parallel

            Log.always $"Fable compilation finished in %i{ms}ms"

            let logs, watchDependencies =
                ((logs, state.WatchDependencies), results)
                ||> Array.fold (fun (logs, deps) -> function
                    | Ok res ->
                        let logs = Array.append logs res.Logs
                        let deps = Map.add res.File res.WatchDependencies deps
                        logs, deps
                    | Error e ->
                        let log = Log.MakeError(e.Exception.Message, fileName=e.File, tag="EXCEPTION")
                        Log.verbose(lazy e.Exception.StackTrace)
                        Array.append logs [|log|], deps)

            return logs, { state with HasCompiledOnce = true
                                      PendingFilesToCompile = [||]
                                      WatchDependencies = watchDependencies }
    }

    // Sometimes errors are duplicated
    let logs = Array.distinct logs

    logs
    |> Array.filter (fun x -> x.Severity = Severity.Info)
    |> Array.iter (formatLog >> Log.always)

    logs
    |> Array.filter (fun log ->
        match log.Severity, log.FileName with
        // Ignore warnings from packages in `fable_modules` folder
        | Severity.Warning, Some filename when Naming.isInFableHiddenDir(filename) -> false
        | Severity.Warning, _ -> true
        | _ -> false)
    |> Array.iter (formatLog >> Log.warning)

    let newErrors =
        (Set.empty, logs) ||> Array.fold (fun errors log ->
            if log.Severity = Severity.Error then
                Log.error(formatLog log)
                match log.FileName with
                | Some file -> Set.add file errors
                | None -> errors
            else errors)

    let errorMsg =
        if Set.isEmpty newErrors then None
        else Some "Compilation failed"

    let errorMsg, state =
        match errorMsg, state.CliArgs.RunProcess with
        // Only run process if there are no errors
        | Some e, _ -> Some e, state
        | None, None -> None, state
        | None, Some runProc ->
            let workingDir = state.CliArgs.RootDir

            let exeFile, args =
                match runProc.ExeFile with
                | Naming.placeholder ->
                    let lastFile = Array.last projCracked.SourceFiles
                    let lastFilePath = getOutJsPath state.CliArgs state.GetOrAddDeduplicateTargetDir lastFile.NormalizedFullPath
                    // Fable's getRelativePath version ensures there's always a period in front of the path: ./
                    let lastFilePath = Path.getRelativeFileOrDirPath true workingDir false lastFilePath
                    "node", lastFilePath::runProc.Args
                | exeFile ->
                    File.tryNodeModulesBin workingDir exeFile
                    |> Option.defaultValue exeFile, runProc.Args

            if Option.isSome state.Watcher then
                Process.start workingDir exeFile args
                let runProc = if runProc.IsWatch then Some runProc else None
                None, { state with CliArgs = { state.CliArgs with RunProcess = runProc } }
            else
                let exitCode = Process.runSync workingDir exeFile args
                (if exitCode = 0 then None else Some "Run process failed"), state

    match state.Watcher with
    | Some watcher ->
        let oldErrors =
            state.ErroredFiles
            |> Set.filter (fun file -> not(Array.contains file filesToCompile))

        let! changes =
            watcher.Observe [
                projCracked.ProjectOptions.ProjectFileName
                yield! projCracked.SourceFiles
                    |> Array.choose (fun f ->
                        let path = f.NormalizedFullPath
                        if Naming.isInFableHiddenDir(path) then None
                        else Some path)
            ]
            |> Async.AwaitObservable

        return!
            { state with ProjectCrackedAndChecked = Some(projCracked, projChecked)
                         ErroredFiles = Set.union oldErrors newErrors }
            |> startCompilation changes

    | None ->
        return match errorMsg with Some e -> Error e | None -> Ok()
}

let startFirstCompilation state =
    startCompilation (HashSet() :> ISet<_>) state
