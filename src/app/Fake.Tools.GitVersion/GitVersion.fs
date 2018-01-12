[<RequireQualifiedAccess>]
module Fake.Tools.GitVersion

open System
open System.Linq
open System.IO

open Fake.Core
open Fake.Core.Globbing
open Fake.Core.StringBuilder

open Newtonsoft.Json

open Aether
open Aether.Operators

(*
    Options types to configure call to gitversion
*)

type Options =
    { /// The path to the gitversion executable
      toolPath                      :   string

      /// The directory containing .git. If not defined current directory is used. (Must be first argument)
      path                          :   string option
      
      /// Determines the output to the console. Can be either 'json' or 'buildserver', will default to 'json'.
      output                        :   OutputTypeOption
      
      ///  Overrides GitVersion config values inline (semicolon-separated key value pairs e.g. /overrideconfig tag-prefix=Foo)
      overrideConfig                :   ConfigOverrideOptions list
      
      /// Search specified AssemblyInfo file(s) in the git repo and update them
      updateAssemblyInfo            :   UpdateAssemblyInfoOption option
      
      /// If the assembly info file specified with /updateassemblyinfo or /updateassemblyinfofilename is not found,
      /// it be created with these attributes: AssemblyFileVersion, AssemblyVersion and AssemblyInformationalVersion
      /// ---
      /// Supports writing version info for: C#, F#, VB
      ensureAssemblyInfo            :   bool
      
      /// Remote repository args
      gitOptions                    :   GitOptions option
      
      /// Execute build args
      execBuildOptions              :   ExecuteBuildArgsOptions option }

and ConfigOverrideOptions = 
    /// Overrides tag-prefix config
    TagPrefix of string

and UpdateAssemblyInfoOption = 
    /// Will recursively search for all 'AssemblyInfo.cs' files in the git repo and update them
    | AutoDetect
    /// Specify name of AssemblyInfo file.
    | File of string
    /// Specify name of AssemblyInfo files.
    | Files of string list

and OutputTypeOption = Json | BuildServer

and GitOptions =
    { /// Url to remote git repository.
      url                   :   Uri option
      
      /// Name of the branch to use on the remote repository, must be used in combination with /url.
      branchName            :   string option
      
      /// Username in case authentication is required.
      userName              :   string option
      
      /// Password in case authentication is required.
      password              :   string option
      
      /// The commit id to check. If not specified, the latest available commit on the specified branch will be used.
      commitId              :   string option
      
      /// By default dynamic repositories will be cloned to %tmp%. Use this switch to override.
      dynamicRepoLocation   :   string option
      
      /// Disables 'git fetch' during version calculation. Might cause GitVersion to not calculate your version as expected.
      noFetch               :   bool }

and ExecuteBuildArgsOptions =
    { /// Executes target executable making GitVersion variables available as environmental variables
      exec      :   string option
      
      /// Arguments for the executable specified by /exec
      execArgs  :   string option
      
      /// Build a msbuild file, GitVersion variables will be passed as msbuild properties
      proj      :   string option
      
      /// Additional arguments to pass to msbuild
      projArgs  :   string option }

// ----------------------------------------------------------------------------
// Lenses

let toolPath_ : Lens<Options, string> = 
    (fun x -> x.toolPath),
    (fun v x -> { x with toolPath = v })

let path_ : Prism<Options, string> =
    (fun x -> x.path),
    (fun v x -> { x with path = Some v })

let output_ : Lens<Options, OutputTypeOption> =
    (fun x -> x.output),
    (fun v x -> { x with output = v })

let overrideConfig_ : Lens<Options, ConfigOverrideOptions list> =
    (fun x -> x.overrideConfig),
    (fun v x -> { x with overrideConfig = v })

let updateAssemblyInfo_ : Prism<Options, UpdateAssemblyInfoOption> =
    (fun x -> x.updateAssemblyInfo),
    (fun v x -> { x with updateAssemblyInfo = Some v })

let ensureAssemblyInfo_ : Lens<Options, bool> =
    (fun x -> x.ensureAssemblyInfo),
    (fun v x -> { x with ensureAssemblyInfo = v })

let gitOptions_ : Prism<Options, GitOptions> =
    (fun x -> x.gitOptions),
    (fun v x -> { x with gitOptions = Some v })

let url_ : Prism<GitOptions, Uri> =
    (fun x -> x.url),
    (fun v x -> { x with url = Some v })

let urlstring_ : Isomorphism<Uri, string> =
    string,
    (fun s -> Uri(s))

let branchName_ : Prism<GitOptions, string> =
    (fun x -> x.branchName),
    (fun v x -> { x with branchName = Some v })

let userName_ : Prism<GitOptions, string> =
    (fun x -> x.userName),
    (fun v x -> { x with userName = Some v })

let password_ : Prism<GitOptions, string> =
    (fun x -> x.password),
    (fun v x -> { x with password = Some v })

let commitId_ : Prism<GitOptions, string> =
    (fun x -> x.commitId),
    (fun v x -> { x with commitId = Some v })

let dynamicRepoLocation_ : Prism<GitOptions, string> =
    (fun x -> x.dynamicRepoLocation),
    (fun v x -> { x with dynamicRepoLocation = Some v })

let noFetch_ : Lens<GitOptions, bool> =
    (fun x -> x.noFetch),
    (fun v x -> { x with noFetch = v })


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
    let currentDirectory = Directory.GetCurrentDirectory()
    let toolPath = Environment.environVarOrDefault "ChocolateyInstall" currentDirectory
                   |> Tools.findToolInSubPath "GitVersion.exe"

    { toolPath = toolPath
      path = None
      output = Json
      overrideConfig = List.empty
      updateAssemblyInfo = None
      ensureAssemblyInfo = false
      gitOptions = None
      execBuildOptions = None }


(*
    GitVersion command
*)

let private executeGitVersion toolPath arguments =
    let timeSpan =  TimeSpan.FromMinutes 1.

    Trace.trace <| sprintf "Arguments: %s" arguments 
    let result = Process.ExecProcessAndReturnMessages
                    (fun info -> 
                        info.FileName <- toolPath
                        info.Arguments <- arguments)
                    timeSpan
    
    let messages = String.concat "" result.Messages

    if result.ExitCode <> 0 then
        failwithf "GitVersion failed with code %i and message %s" result.ExitCode messages
    else
        messages

let private buildArguments (options : Options) =
    let appendOption key prism =
        appendIfSome (options ^. prism) (sprintf "/%s %s" key)

    StringBuilder ()

    |> append       (options ^. output_
                    |> function
                    | Json -> "/output json"
                    | BuildServer -> "/output buildServer")

    |> append       (options ^. overrideConfig_
                    |> List.map (function TagPrefix s -> sprintf "tag-prefix=%s" s)
                    |> String.concat ";"
                    |> sprintf "/overrideconfig %s")

    |> appendIfSome (options ^. updateAssemblyInfo_)
                    (function
                    | AutoDetect -> "/updateassemblyinfo"
                    | File f     -> sprintf "/updateAssemblyInfo %s" f
                    | Files fs   -> String.concat ";" fs |> sprintf "/updateAssemblyInfo %s")

    |> appendIfSome (options ^. (gitOptions_ >?> noFetch_)) (function true -> "/nofetch" | false -> "")

    |> appendOption "targetpath"                            path_
    |> appendIfTrue (options ^. ensureAssemblyInfo_)        "/ensureassemblyinfo"
    |> appendOption "url"                                   (gitOptions_ >?> url_ >?> urlstring_)
    |> appendOption "b"                                     (gitOptions_ >?> branchName_)
    |> appendOption "u"                                     (gitOptions_ >?> userName_)
    |> appendOption "p"                                     (gitOptions_ >?> password_)
    |> appendOption "c"                                     (gitOptions_ >?> commitId_)
    |> appendOption "dynamicRepoLocation"                   (gitOptions_ >?> dynamicRepoLocation_)

let exec options = 
    buildArguments options
    |> toText
    |> executeGitVersion options.toolPath
    |> JsonConvert.DeserializeObject<GitVersionOutput>

let showVariable options varName =
    buildArguments options
    |> append (sprintf "/showvariable %s" varName)
    |> toText
    |> executeGitVersion options.toolPath
    |> fun r -> r.Last()
