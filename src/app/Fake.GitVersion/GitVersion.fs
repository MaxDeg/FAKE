module Fake.Tools.GitVersion

open System

open Fake.Core
open Fake.Core.Globbing

(*
    Options types to configure call to gitversion
*)
type GitVersionOptions =
    { toolPath                      :   string
      path                          :   string option
      overrideConfig                :   GitVersionConfigOverride list
      updateAssemblyInfo            :   string list option
      updateAssemblyInfoFileName    :   string option
      ensureAssemblyInfo            :   bool
      gitOptions                    :   GitVersionGitOptions option }

and GitVersionConfigOverride =
    { tagPrefix :   string }

and GitVersionGitOptions =
    { url                   :   Uri option
      branchName            :   string option
      userName              :   string option
      password              :   string option
      commitId              :   string option
      dynamicRepoLocation   :   string option
      noFetch               :   bool }

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
    { toolPath = Environment.environVarOrDefault "ChocolateyInstall" currentDirectory |> Tools.findToolInSubPath "GitVersion.exe"
      path = None
      overrideConfig = List.empty
      updateAssemblyInfo = None
      updateAssemblyInfoFileName = None
      ensureAssemblyInfo = false
      gitOptions = None }

(*
    GitVersion command
*)
let gitVersion (options : GitVersionOptions -> GitVersionOptions) = 
    // Process.ExecProcessAndReturnMessages
    ()
