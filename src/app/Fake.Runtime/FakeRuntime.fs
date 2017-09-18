﻿module Fake.Runtime.FakeRuntime

open System
open System.IO
open Fake.Runtime
open Paket

//#if DOTNETCORE

type RawFakeSection =
  { Header : string
    Section : string }

let readFakeSection (scriptText:string) =
  let startString = "(* -- Fake Dependencies "
  let endString = "-- Fake Dependencies -- *)"
  let start = scriptText.IndexOf(startString) + startString.Length
  let endIndex = scriptText.IndexOf(endString) - 1
  if (start >= endIndex) then
    None
  else
    let fakeSectionWithVersion = scriptText.Substring(start, endIndex - start)
    let newLine = fakeSectionWithVersion.IndexOf("\n")
    let header = fakeSectionWithVersion.Substring(0, newLine).Trim()
    let fakeSection = fakeSectionWithVersion.Substring(newLine).Trim()
    Some { Header = header; Section = fakeSection}

type FakeSection =
 | PaketDependencies of Paket.Dependencies * group : String option

let readAllLines (r : TextReader) =
  seq {
    let mutable line = r.ReadLine()
    while not (isNull line) do
      yield line
      line <- r.ReadLine()
  }
let private dependenciesFileName = "paket.dependencies"
let parseHeader scriptCacheDir (f : RawFakeSection) =
  match f.Header with
  | "paket-inline" ->
    let dependenciesFile = Path.Combine(scriptCacheDir, dependenciesFileName)
    let fixedSection =
      f.Section.Split([| "\r\n"; "\r"; "\n" |], System.StringSplitOptions.None)
      |> Seq.map (fun line ->
        let replacePaketCommand (command:string) (line:string) =
          let trimmed = line.Trim()
          if trimmed.StartsWith command then
            let restString = trimmed.Substring(command.Length).Trim()
            let isValidPath = try Path.GetFullPath restString |> ignore; true with _ -> false
            let isAbsoluteUrl = match Uri.TryCreate(restString, UriKind.Absolute) with | true, _ -> true | _ -> false
            if isAbsoluteUrl || not isValidPath || Path.IsPathRooted restString then line
            else line.Replace(restString, Path.Combine("..", "..", restString))
          else line
        line
        |> replacePaketCommand "source"
        |> replacePaketCommand "cache"
      )
    File.WriteAllLines(dependenciesFile, fixedSection)
    PaketDependencies (Paket.Dependencies(dependenciesFile), None)
  | "paket.dependencies" ->
    let groupStart = "group "
    let fileStart = "file "
    let readLine (l:string) : (string * string) option =
      if l.StartsWith groupStart then ("group", (l.Substring groupStart.Length).Trim()) |> Some
      elif l.StartsWith fileStart then ("file", (l.Substring fileStart.Length).Trim()) |> Some
      elif String.IsNullOrWhiteSpace l then None
      else failwithf "Cannot recognise line in dependency section: '%s'" l
    let options =
      (use r = new StringReader(f.Section)
       readAllLines r |> Seq.toList)
      |> Seq.choose readLine
      |> dict
    let group =
      match options.TryGetValue "group" with
      | true, gr -> Some gr
      | _ -> None
    let file =
      match options.TryGetValue "file" with
      | true, depFile -> depFile
      | _ -> dependenciesFileName
    PaketDependencies (Paket.Dependencies(Path.GetFullPath file), group)
  | _ -> failwithf "unknown dependencies header '%s'" f.Header
type AssemblyData =
  { IsReferenceAssembly : bool
    Info : Runners.AssemblyInfo }

let paketCachingProvider printDetails cacheDir (paketDependencies:Paket.Dependencies) group =
  let groupStr = match group with Some g -> g | None -> "Main"
  let groupName = Paket.Domain.GroupName (groupStr)
 #if DOTNETCORE
  let framework = Paket.FrameworkIdentifier.DotNetStandard (Paket.DotNetStandardVersion.V1_6)
#else
  let framework = Paket.FrameworkIdentifier.DotNetFramework (Paket.FrameworkVersion.V4_5)
#endif
  let lockFilePath = Paket.DependenciesFile.FindLockfile paketDependencies.DependenciesFile
  let parent s = Path.GetDirectoryName s
  let comb name s = Path.Combine(s, name)
  //let paketDependenciesHashFile = cacheDir |> comb "paket.depedencies.sha1"
  //let saveDependenciesHash () =
  //  File.WriteAllText (paketDependenciesHashFile, HashGeneration.getStringHash (File.ReadAllText paketDependencies.DependenciesFile))
  let restoreOrUpdate () =
    if printDetails then Trace.log "Restoring with paket..."

    // Update
    if not <| File.Exists lockFilePath.FullName then
      if printDetails then Trace.log "Lockfile was not found. We will update the dependencies and write our own..."
      paketDependencies.UpdateGroup(groupStr, false, false, false, false, false, Paket.SemVerUpdateMode.NoRestriction, false)
      |> ignore

    // Restore
    paketDependencies.Restore((*false, group, [], false, true*))
    |> ignore

    let lockFile = paketDependencies.GetLockFile()
    let (cache:DependencyCache) = DependencyCache(paketDependencies.GetDependenciesFile(), lockFile)
    if printDetails then Trace.log "Setup DependencyCache..."
    cache.SetupGroup groupName |> ignore

    let orderedGroup = cache.OrderedGroups groupName // lockFile.GetGroup groupName
    // Write loadDependencies file (basically only for editor support)
    let intellisenseFile = Path.Combine (cacheDir, "intellisense.fsx")
    if printDetails then Trace.log <| sprintf "Writing '%s'" intellisenseFile
    // TODO: Make sure to create #if !FAKE block, because we don't actually need it.
    let intellisenseContents =
      [| "// This file is needed for IDE support"
         "printfn \"loading dependencies ...\"" |]
    File.WriteAllLines (intellisenseFile, intellisenseContents)

    let rid =
#if DOTNETCORE
        let ridString = Microsoft.DotNet.PlatformAbstractions.RuntimeEnvironment.GetRuntimeIdentifier()
#else
        let ridString = "win"
#endif
        Paket.Rid.Of(ridString)

    // get runtime graph
    if printDetails then Trace.log <| sprintf "Calculating the runtime graph..."
    let graph =
        orderedGroup
        |> Seq.choose (fun p -> RuntimeGraph.getRuntimeGraphFromNugetCache cacheDir groupName p.Resolved)
        |> RuntimeGraph.mergeSeq

    // Retrieve assemblies
    if printDetails then Trace.log <| sprintf "Retrieving the assemblies (rid: '%O')..." rid
    orderedGroup
    |> Seq.filter (fun p ->
      if p.Name.ToString() = "Microsoft.FSharp.Core.netcore" then
        eprintfn "Ignoring 'Microsoft.FSharp.Core.netcore' please tell the package authors to fix their package and reference 'FSharp.Core' instead."
        false
      else true)
    |> Seq.collect (fun p ->
      match cache.InstallModel groupName p.Name with
      | None -> failwith "InstallModel not cached?"
      | Some installModelRaw ->
      let installModel =
        installModelRaw
          .ApplyFrameworkRestrictions(Paket.Requirements.getExplicitRestriction p.Settings.FrameworkRestrictions)
      let targetProfile = Paket.TargetProfile.SinglePlatform framework

      let refAssemblies =
        installModel.GetCompileReferences targetProfile
        |> Seq.map (fun fi -> true, FileInfo fi.Path)
        |> Seq.toList
      let runtimeAssemblies =
        installModel.GetRuntimeAssemblies graph rid targetProfile
        |> Seq.map (fun fi -> false, FileInfo fi.Library.Path)
        |> Seq.toList
      runtimeAssemblies @ refAssemblies)
    |> Seq.filter (fun (_, r) -> r.Extension = ".dll" || r.Extension = ".exe" )
    |> Seq.choose (fun (isReferenceAssembly, fi) ->
      let fullName = fi.FullName
      try let assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly fullName
          { IsReferenceAssembly = isReferenceAssembly
            Info =
              { Runners.AssemblyInfo.FullName = assembly.Name.FullName
                Runners.AssemblyInfo.Version = assembly.Name.Version.ToString()
                Runners.AssemblyInfo.Location = fullName } } |> Some
      with e -> (if printDetails then Trace.log <| sprintf "Could not load '%s': %O" fullName e); None)
    |> Seq.toList
    //|> List.partition (fun c -> c.IsReferenceAssembly)
  // Restore or update immediatly, because or everything might be OK -> cached path.
  let knownAssemblies = restoreOrUpdate()
  if printDetails then
    Trace.tracefn "Known assemblies: \n\t%s" (System.String.Join("\n\t", knownAssemblies |> Seq.map (fun a -> sprintf " - %s: %s (%s)" (if a.IsReferenceAssembly then "ref" else "lib") a.Info.Location a.Info.Version)))
  { new CoreCache.ICachingProvider with
      member x.CleanCache context =
        if printDetails then Trace.log "Invalidating cache..."
        let assemblyPath, warningsFile = context.CachedAssemblyFilePath + ".dll", context.CachedAssemblyFilePath + ".warnings"
        try File.Delete warningsFile; File.Delete assemblyPath
        with e -> Trace.traceError (sprintf "Failed to delete cached files: %O" e)
      member __.TryLoadCache (context) =
          let references =
              knownAssemblies
              |> List.filter (fun a -> a.IsReferenceAssembly)
              |> List.map (fun (a:AssemblyData) -> a.Info.Location)
          let runtimeAssemblies =
              knownAssemblies
              |> List.filter (fun a -> not a.IsReferenceAssembly)
              |> List.map (fun a -> a.Info)
          let fsiOpts = context.Config.CompileOptions.AdditionalArguments |> Yaaf.FSharp.Scripting.FsiOptions.ofArgs
          let newAdditionalArgs =
              { fsiOpts with
                  NoFramework = true
                  Debug = Some Yaaf.FSharp.Scripting.DebugMode.Portable }
              |> (fun options -> options.AsArgs)
              |> Seq.toList
          { context with
              Config =
                { context.Config with
                    CompileOptions =
                      { context.Config.CompileOptions with
                          AdditionalArguments = newAdditionalArgs
                          RuntimeDependencies = runtimeAssemblies @ context.Config.CompileOptions.RuntimeDependencies
                          CompileReferences = references @ context.Config.CompileOptions.CompileReferences
                      }
                }
          },
          let assemblyPath, warningsFile = context.CachedAssemblyFilePath + ".dll", context.CachedAssemblyFilePath + ".warnings"
          if File.Exists (assemblyPath) && File.Exists (warningsFile) then
              Some { CompiledAssembly = assemblyPath; Warnings = File.ReadAllText(warningsFile) }
          else None
      member x.SaveCache (context, cache) =
          if printDetails then Trace.log "saving cache..."
          File.WriteAllText (context.CachedAssemblyFilePath + ".warnings", cache.Warnings) }

let restoreDependencies printDetails cacheDir section =
  match section with
  | PaketDependencies (paketDependencies, group) ->
    paketCachingProvider printDetails cacheDir paketDependencies group

let tryFindGroupFromDepsFile scriptDir =
    let depsFile = Path.Combine(scriptDir, "paket.dependencies")
    if File.Exists (depsFile) then
        match
            File.ReadAllLines(depsFile)
            |> Seq.map (fun l -> l.Trim())
            |> Seq.fold (fun (takeNext, result) l ->
                // find '// [ FAKE GROUP ]' and take the next one.
                match takeNext, result with
                | _, Some s -> takeNext, Some s
                | true, None ->
                    if not (l.ToLowerInvariant().StartsWith "group") then
                        Trace.traceFAKE "Expected a group after '// [ FAKE GROUP]' comment, but got %s" l
                        false, None
                    else
                        let splits = l.Split([|" "|], StringSplitOptions.RemoveEmptyEntries)
                        if splits.Length < 2 then
                            Trace.traceFAKE "Expected a group name after '// [ FAKE GROUP]' comment, but got %s" l
                            false, None
                        else
                            false, Some (splits.[1])
                | _ -> if l.Contains "// [ FAKE GROUP ]" then true, None else false, None) (false, None)
            |> snd with
        | Some group ->
            PaketDependencies (Paket.Dependencies(Path.GetFullPath depsFile), Some group) |> Some
        | _ -> None
    else None

let prepareFakeScript printDetails script =
    // read dependencies from the top
    let scriptDir = Path.GetDirectoryName (script)
    let cacheDir = Path.Combine(scriptDir, ".fake", Path.GetFileName(script))
    Directory.CreateDirectory (cacheDir) |> ignore
    let scriptText = File.ReadAllText(script)
    let section = readFakeSection scriptText
    let section =
        match section with
        | Some s -> parseHeader cacheDir s |> Some
        | None ->
            tryFindGroupFromDepsFile scriptDir

    match section, Environment.environVar "FAKE_UNDOCUMENTED_NETCORE_HACK" = "true" with
    | _, true ->
        Trace.traceFAKE "NetCore hack (FAKE_UNDOCUMENTED_NETCORE_HACK) is activated: %s" script
        CoreCache.Cache.defaultProvider
    | Some section, _ ->
        restoreDependencies printDetails cacheDir section
    | _ ->
        failwithf "You cannot use the netcore version of FAKE as drop-in replacement, please add a dependencies section (and read the migration guide)."

let prepareAndRunScriptRedirect printDetails fsiOptions scriptPath envVars onErrMsg onOutMsg useCache =
  let provider = prepareFakeScript printDetails scriptPath
  use out = Yaaf.FSharp.Scripting.ScriptHost.CreateForwardWriter onOutMsg
  use err = Yaaf.FSharp.Scripting.ScriptHost.CreateForwardWriter onErrMsg
  let config =
    { Runners.FakeConfig.PrintDetails = printDetails
      Runners.FakeConfig.ScriptFilePath = scriptPath
      Runners.FakeConfig.CompileOptions =
        { CompileReferences = []
          RuntimeDependencies = []
          AdditionalArguments = fsiOptions }
      Runners.FakeConfig.UseCache = useCache
      Runners.FakeConfig.Out = out
      Runners.FakeConfig.Err = err
      Runners.FakeConfig.Environment = envVars }
  CoreCache.runScriptWithCacheProvider config provider

let prepareAndRunScript printDetails fsiOptions scriptPath envVars useCache =
  prepareAndRunScriptRedirect printDetails fsiOptions scriptPath envVars (printf "%s") (printf "%s") useCache

//#endif
