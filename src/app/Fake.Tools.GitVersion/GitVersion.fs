module Fake.Tools.GitVersion

open System
open System.Linq

open Fake.Core
open Fake.Core.Globbing
open Newtonsoft.Json
open Aether


(*
    Options types to configure call to gitversion
*)

type GitVersionOptions =
    { toolPath                      :   string
      path                          :   string option
      output                        :   GitVersionOutputType
      overrideConfig                :   GitVersionConfigOverride list
      updateAssemblyInfo            :   string list option
      ensureAssemblyInfo            :   bool
      gitOptions                    :   GitVersionGitOptions option }

and GitVersionConfigOverride = TagPrefix of string

and GitVersionOutputType = Json | BuildServer

and GitVersionGitOptions =
    { url                   :   Uri option
      branchName            :   string option
      userName              :   string option
      password              :   string option
      commitId              :   string option
      dynamicRepoLocation   :   string option
      noFetch               :   bool }

// ----------------------------------------------------------------------------
// Lenses

let toolPath = (fun x -> x.toolPath), (fun v x -> { x with toolPath = v })

let path = (fun x -> x.path), (fun v x -> { x with path = v })

let overrideConfig = (fun x -> x.overrideConfig), (fun v x -> { x with overrideConfig = v })

(*
    Output of GitVersion command
*)

type GitVersionOutput =
    { major                             :   int
      minor                             :   int
      path                              :   int
      preReleaseTag                     :   string
      preReleaseTagWithDash             :   string
      preReleaseLabel                   :   string
      preReleaseNumber                  :   int
      buildMetaData                     :   int
      buildMetaDataPadded               :   string
      fullBuildMetaData                 :   string
      majorMinorPatch                   :   string
      semVer                            :   string
      legacySemVer                      :   string
      legacySemVerPadded                :   string
      assemblySemVer                    :   string
      assemblySemFileVer                :   string
      fullSemVer                        :   string
      informationalVersion              :   string
      branchName                        :   string
      sha                               :   string
      nugetVersionV2                    :   string
      nugetVersion                      :   string
      nugetPreReleaseTagV2              :   string
      nugetPreReleaseTag                :   string
      commitsSinceVersionSource         :   int
      commitsSinceVersionSourcePadded   :   string
      commitDate                        :   string }


(*
    Default options
*)

let gitVersionDefaultOptions =
    let toolPath = Environment.environVarOrDefault "ChocolateyInstall" currentDirectory
                   |> Tools.findToolInSubPath "GitVersion.exe"

    { toolPath = toolPath
      path = None
      output = Json
      overrideConfig = List.empty
      updateAssemblyInfo = None
      ensureAssemblyInfo = false
      gitOptions = None }


(*
    GitVersion command
*)

let private buildArguments (options : GitVersionOptions) =
    let flip f a b = f b a

    seq {
        yield Option.map (sprintf "/targetpath %s") options.path

        yield match options.output with Json -> Some "/output json" | BuildServer -> Some "/output buildServer"

        yield List.map (function TagPrefix s -> sprintf "tag-prefix=%s" s) options.overrideConfig
              |> fun l -> if List.isEmpty l
                          then None
                          else String.concat " " l |> sprintf "/overrideconfig %s" |> Some

        yield fun l -> if List.isEmpty l
                       then None
                       else String.concat " " l |> sprintf "/updateassemblyinfo %s" |> Some
              |> flip Option.bind options.updateAssemblyInfo

        yield if options.ensureAssemblyInfo then Some "/ensureassemblyinfo" else None

        yield fun o -> o.url |> Option.map (string >> sprintf "/url %s")
              |> flip Option.bind options.gitOptions

        yield fun (o: GitVersionGitOptions) -> o.branchName |> Option.map (sprintf "/b %s")
              |> flip Option.bind options.gitOptions

        yield fun o -> o.userName |> Option.map (string >> sprintf "/u %s")
              |> flip Option.bind options.gitOptions

        yield fun o -> o.password |> Option.map (string >> sprintf "/p %s")
              |> flip Option.bind options.gitOptions

        yield fun o -> o.commitId |> Option.map (string >> sprintf "/c %s")
              |> flip Option.bind options.gitOptions

        yield fun o -> o.dynamicRepoLocation |> Option.map (string >> sprintf "/dynamicRepoLocation %s")
              |> flip Option.bind options.gitOptions

        yield fun o -> if o.noFetch then Some "/nofetch" else None
              |> flip Option.bind options.gitOptions
    }
    |> Seq.choose id

let gitVersion options = 
    let timeSpan =  TimeSpan.FromMinutes 1.
    let deSerializeResult = JsonConvert.DeserializeObject<GitVersionOutput>
    let arguments = buildArguments options |> String.concat " "

    Trace.trace <| sprintf "Arguments: %s" arguments 
    let result = Process.ExecProcessAndReturnMessages 
                    (fun info -> 
                        info.FileName <- options.toolPath
                        info.Arguments <- arguments)
                    timeSpan
    
    if result.ExitCode <> 0 then
        String.concat "" result.Messages
        |> failwithf "GitVersion failed with code %i and message %s" result.ExitCode
    else 
        String.concat "" result.Messages |> deSerializeResult

let gitVersionShowVariable options varName = 
    let timeSpan =  TimeSpan.FromMinutes 1.
    let deSerializeResult = JsonConvert.DeserializeObject<GitVersionOutput>
    let arguments = 
        seq {
            yield! buildArguments options
            yield  sprintf "/showvariable %s" varName
        }
        |> String.concat " "

    Trace.trace <| sprintf "Arguments: %s" arguments
    let result = Process.ExecProcessAndReturnMessages 
                    (fun info -> 
                        info.FileName <- options.toolPath
                        info.Arguments <- arguments)
                    timeSpan
    
    if result.ExitCode <> 0 then
        String.concat "" result.Messages
        |> failwithf "GitVersion failed with code %i and message %s" result.ExitCode
    else 
        result.Messages.Single()
