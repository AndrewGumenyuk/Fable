module Fable.Cli.Entry

open System
open Main
open Fable

let argValue key (args: string list) =
    args
    |> List.windowed 2
    |> List.tryPick (function
        | [key2; value] when key = key2 && not(value.StartsWith("-")) -> Some value
        | _ -> None)

let tryFlag flag (args: string list) =
    match argValue flag args with
    | Some flag ->
        match Boolean.TryParse(flag) with
        | true, flag -> Some flag
        | false, _ -> None
    // Flags can be activated without an explicit value
    | None when List.contains flag args -> Some true
    | None -> None

let flagEnabled flag (args: string list) =
    tryFlag flag args |> Option.defaultValue false

let argValues key (args: string list) =
    args
    |> List.windowed 2
    |> List.fold (fun acc window ->
        match window with
        | [key2; value] when key = key2 -> value::acc
        | _ -> acc) []
    |> List.rev

let printHelp() =
    Log.always """Usage: fable [watch] [.fsproj file or dir path] [arguments]

Commands:
  -h|--help         Show help
  --version         Print version
  watch             Run Fable in watch mode
  clean             Remove .fable folders and files with specified extension (default .fs.js)

Arguments:
  --cwd             Working directory
  --outDir          Redirect compilation output to a directory
  --define          Defines a symbol for use in conditional compilation
  --run             The command after the argument will be executed after compilation
  --runWatch        Like run, but will execute after each watch compilation
  --runScript       Runs the generated script for last file with node (requires "esm" npm package)
  --typedArrays     Compile numeric arrays as JS typed arrays (default true)
  --forcePkgs       Force a new copy of package sources into `.fable` folder
  --extension       Extension for generated JS files (default .fs.js)
  --verbose         Print more info during compilation
  --exclude         Skip Fable compilation for files containing the pattern
  --optimize        Compile with optimized F# AST (experimental)
  --typescript      Compile to TypeScript (experimental)
"""

type Runner =
  static member Run(args: string list, rootDir: string, runProc: RunProcess option, ?fsprojPath: string, ?watch, ?testInfo) =
    let watch = defaultArg watch false

    let fsprojPath =
        match fsprojPath with
        | None -> rootDir
        | Some p when IO.Path.IsPathRooted(p) -> p
        | Some p -> IO.Path.Combine(rootDir, p)

    if IO.Directory.Exists(fsprojPath) then
        IO.Directory.EnumerateFileSystemEntries(fsprojPath)
        |> Seq.filter (fun file -> file.EndsWith(".fsproj"))
        |> Seq.toList
        |> function
            | [] -> Error("Cannot find .fsproj in dir: " + fsprojPath)
            | [fsproj] -> Ok fsproj
            | _ -> Error("Found multiple .fsproj in dir: " + fsprojPath)
    elif not(IO.File.Exists(fsprojPath)) then
        Error("File does not exist: " + fsprojPath)
    else
        Ok fsprojPath

    // TODO: Remove this check when typed arrays are compatible with typescript
    |> Result.bind (fun projFile ->
        let typescript = flagEnabled "--typescript" args
        let typedArrays = tryFlag "--typedArrays" args |> Option.defaultValue true
        if typescript && typedArrays then
            Error("Typescript output is currently not compatible with typed arrays, pass: --typedArrays false")
        else
            Ok(projFile, typescript, typedArrays)
    )

    |> Result.bind (fun (projFile, typescript, typedArrays) ->
        let verbosity =
            if flagEnabled "--verbose" args then
                Log.makeVerbose()
                Verbosity.Verbose
            else Verbosity.Normal

        let defines =
            argValues "--define" args
            |> List.append [
                "FABLE_COMPILER"
                if watch then "DEBUG"
            ]
            |> List.distinct
            |> List.toArray

        let compilerOptions =
            CompilerOptionsHelper.Make(typescript = typescript,
                                       typedArrays = typedArrays,
                                       ?fileExtension = argValue "--extension" args,
                                       debugMode = Array.contains "DEBUG" defines,
                                       optimizeFSharpAst = flagEnabled "--optimize" args,
                                       verbosity = verbosity)

        let cliArgs =
            { ProjectFile = Path.normalizeFullPath projFile
              FableLibraryPath = argValue "--fableLib" args
              RootDir = rootDir
              OutDir = argValue "--outDir" args
              WatchMode = watch
              ForcePackages = flagEnabled "--forcePkgs" args
              Exclude = argValue "--exclude" args
              Define = defines
              RunProcess = runProc
              CompilerOptions = compilerOptions }

        { CliArgs = cliArgs
          ProjectCrackedAndParsed = None
          WatchDependencies = Map.empty
          ErroredFiles = Set.empty
          TestInfo = testInfo }
        |> startCompilation Set.empty
        |> Async.RunSynchronously)


let clean args dir =
    let ignoreDirs = set ["bin"; "obj"; "node_modules"]
    let ext =
        argValue "--extension" args
        |> Option.defaultValue CompilerOptionsHelper.DefaultFileExtension

    let mutable fileCount = 0
    let rec recClean dir =
        IO.Directory.GetFiles(dir, "*" + ext)
        |> Array.iter (fun file ->
            IO.File.Delete(file)
            fileCount <- fileCount + 1
            Log.verbose(lazy ("Deleted " + file)))

        IO.Directory.GetDirectories(dir)
        |> Array.filter (fun subdir ->
            ignoreDirs.Contains(IO.Path.GetFileName(subdir)) |> not)
        |> Array.iter (fun subdir ->
            if IO.Path.GetFileName(subdir) = Naming.fableHiddenDir then
                IO.Directory.Delete(subdir, true)
                Log.always("Deleted " + File.getRelativePathFromCwd subdir)
            else recClean subdir)

    recClean dir
    Log.always("Clean completed! Files deleted: " + string fileCount)

type ResultBuilder() =
    member _.Bind(v,f) = Result.bind f v
    member _.Return v = Ok v
    member _.ReturnFrom v = v

let result = ResultBuilder()

[<EntryPoint>]
let main argv =
    result {
        let! argv, runProc =
            argv
            |> List.ofArray
            |> List.splitWhile (fun a -> not(a.StartsWith("--run")))
            |> function
                | argv, flag::runArgs ->
                    match flag, runArgs with
                    | "--run", [] -> Error "Missing command after --run"
                    | "--runWatch", [] -> Error "Missing command after --runWatch"
                    | "--run", exeFile::args -> Ok(false, exeFile, args)
                    | "--runWatch", exeFile::args -> Ok(true, exeFile, args)
                    | "--runScript", args -> Ok(true, Naming.placeholder, args)
                    | _ -> Error ""
                    |> Result.map (fun (watch, exeFile, args) ->
                        argv, Some(RunProcess(exeFile, args, watch)))
                | argv, [] -> Ok(argv, None)

        let rootDir =
            match argValue "--cwd" argv with
            | Some rootDir -> IO.Path.GetFullPath(rootDir)
            | None -> IO.Directory.GetCurrentDirectory()

        do
            Log.always("Fable: F# to JS compiler " + Literals.VERSION)
            Log.always("Thanks to the contributor! @" + Contributors.getRandom())
            if flagEnabled "--verbose" argv then
                Log.makeVerbose()

        match argv with
        | ("help"|"--help"|"-h")::_ -> return printHelp()
        | ("--version")::_ -> return Log.always Literals.VERSION
        | argv ->
            let commands, args =
                argv |> List.splitWhile (fun x ->
                    x.StartsWith("-") |> not)

            match commands with
            | ["clean"; dir] -> return clean args dir
            | ["clean"] -> return clean args rootDir
            | ["test"; path] -> return! Runner.Run(args, rootDir, runProc, fsprojPath=path, testInfo=TestInfo())
            | ["watch"; path] -> return! Runner.Run(args, rootDir, runProc, fsprojPath=path, watch=true)
            | ["watch"] -> return! Runner.Run(args, rootDir, runProc, watch=true)
            | [path] -> return! Runner.Run(args, rootDir, runProc, fsprojPath=path)
            | [] -> return! Runner.Run(args, rootDir, runProc)
            | _ -> return Log.always "Unexpected arguments. Use `fable --help` to see available options."
    }
    |> function
        | Ok _ -> 0
        | Error msg -> Log.always msg; 1
