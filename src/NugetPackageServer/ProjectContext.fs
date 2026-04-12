namespace NugetPackageServer

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Reflection

[<AutoOpen>]
module Disposable =
    let inline dispose (x: #IDisposable) = x.Dispose()

type ProjectState =
    {
        ProjectPath: string
        Project: ResolvedProject
        Context: MetadataLoadContext
        TypeIndex: TypeSummary array
        MemberIndex: MemberSummary array
        XmlDocCache: Dictionary<string, Dictionary<string, XmlDocEntry>>
        Warnings: string list
    }

    interface IDisposable with
        member this.Dispose() = this.Context.Dispose()

module ProjectState =

    let build (projectPath: string) =
        match AssetsParser.findAssetsFile projectPath with
        | Error msg -> Error msg
        | Ok assetsPath ->

            match AssetsParser.parseAssetsFile assetsPath with
            | Error msg -> Error msg
            | Ok project ->

                let warnings = ResizeArray<string>()
                let allDllPaths = project.Packages |> List.collect (fun p -> p.DllPaths)

                let context, ctxDiagnostics =
                    MetadataInspector.createContext
                        {
                            TargetFramework = project.TargetFramework
                            PackageDllPaths = allDllPaths
                        }

                for d in ctxDiagnostics do
                    warnings.Add(d)

                let typeIndex =
                    project.Packages
                    |> List.collect (fun pkg ->
                        pkg.DllPaths
                        |> List.collect (fun dll ->
                            match MetadataInspector.getPublicTypes context dll pkg.Name with
                            | Ok types -> types
                            | Error msg ->
                                warnings.Add(msg)
                                []
                        )
                    )
                    |> List.toArray

                let memberIndex =
                    project.Packages
                    |> List.collect (fun pkg ->
                        pkg.DllPaths
                        |> List.collect (fun dll ->
                            match MetadataInspector.getPublicMembers context dll pkg.Name with
                            | Ok members -> members
                            | Error msg ->
                                warnings.Add(msg)
                                []
                        )
                    )
                    |> List.toArray

                let xmlDocCache = Dictionary<string, Dictionary<string, XmlDocEntry>>()

                for pkg in project.Packages do
                    for xmlPath in pkg.XmlDocPaths do
                        match XmlDocReader.parseXmlDocFile xmlPath with
                        | Ok docs when docs.Count > 0 -> xmlDocCache.[xmlPath] <- docs
                        | Ok _ -> ()
                        | Error msg -> warnings.Add(msg)

                Ok
                    {
                        ProjectPath = projectPath
                        Project = project
                        Context = context
                        TypeIndex = typeIndex
                        MemberIndex = memberIndex
                        XmlDocCache = xmlDocCache
                        Warnings = Seq.toList warnings
                    }

    let findXmlDoc (s: ProjectState) (memberId: string) =
        s.XmlDocCache.Values
        |> Seq.tryPick (fun docs ->
            match docs.TryGetValue(memberId) with
            | true, entry -> Some entry
            | _ -> None
        )

module PackageLoader =

    let private isCpmEnabled (repoRoot: string) =
        let propsPath = Path.Combine(repoRoot, "Directory.Packages.props")

        if File.Exists(propsPath) then
            let content = File.ReadAllText(propsPath)
            content.Contains("ManagePackageVersionsCentrally") && content.Contains("true")
        else
            false

    let private generateProjectFile (packageName: string) (version: string) (tfm: string) (useCpm: bool) =
        let versionAttr =
            if useCpm then
                $"VersionOverride=\"{version}\""
            else
                $"Version=\"{version}\""

        $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>{tfm}</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="{packageName}" {versionAttr} />
  </ItemGroup>
</Project>
"""

    let private runDotnetRestore (projectDir: string) =
        let psi = ProcessStartInfo()
        psi.FileName <- "dotnet"
        psi.Arguments <- "restore"
        psi.WorkingDirectory <- projectDir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        use proc = Process.Start(psi)
        let stdout = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        proc.WaitForExit()

        if proc.ExitCode <> 0 then
            let output = if stderr.Length > 0 then stderr else stdout
            Error $"dotnet restore failed (exit code {proc.ExitCode}): {output}"
        else
            Ok()

    let createAndRestore (repoRoot: string) (packageName: string) (version: string) (tfm: string) =
        let safeName = $"{packageName}_{version}_{tfm}"
        let tmpDir = Path.Combine(repoRoot, "tmp", safeName)
        let projectFile = Path.Combine(tmpDir, $"{safeName}.csproj")

        try
            Directory.CreateDirectory(tmpDir) |> ignore
            let useCpm = isCpmEnabled repoRoot
            let content = generateProjectFile packageName version tfm useCpm
            File.WriteAllText(projectFile, content)

            match runDotnetRestore tmpDir with
            | Error msg -> Error msg
            | Ok() -> Ok projectFile
        with ex ->
            Error $"Failed to create temp project: {ex.Message}"

type ProjectContextManager() =
    let projects =
        ConcurrentDictionary<string, ProjectState>(StringComparer.OrdinalIgnoreCase)

    let normalize (path: string) = Path.GetFullPath(path)

    member _.LoadProject(projectPath: string) =
        let fullPath = normalize projectPath

        if not (File.Exists fullPath) then
            Error $"Project file not found: {fullPath}"
        else

            match ProjectState.build fullPath with
            | Error msg ->
                match projects.TryRemove(fullPath) with
                | true, old -> dispose old
                | _ -> ()

                Error msg
            | Ok newState ->
                let mutable toDispose: ProjectState option = None

                projects.AddOrUpdate(
                    fullPath,
                    newState,
                    fun _ old ->
                        toDispose <- Some old
                        newState
                )
                |> ignore

                toDispose |> Option.iter dispose
                Ok newState

    member _.UnloadProject(projectPath: string) =
        let fullPath = normalize projectPath

        match projects.TryRemove(fullPath) with
        | true, state ->
            dispose state
            Ok()
        | _ -> Error $"No project loaded for: {fullPath}"

    member _.GetLoadedProjects() = projects.Values |> Seq.toList

    member _.TryGetDefaultTfm() =
        if projects.Count = 1 then
            Some (projects.Values |> Seq.head).Project.TargetFramework
        else
            None

    member _.ProjectCount = projects.Count

    member _.TryResolveProject(projectPath: string) =
        if projects.Count = 0 then
            Error "No project loaded. Call load_project first with the path to your .csproj/.fsproj file."
        elif isNull projectPath || projectPath.Length = 0 then
            if projects.Count = 1 then
                Ok(projects.Values |> Seq.head)
            else
                let paths = projects.Keys |> Seq.map (fun p -> $"  - {p}") |> String.concat "\n"

                Error $"Multiple projects are loaded. Specify projectPath to choose one:\n{paths}"
        else
            let fullPath = normalize projectPath

            match projects.TryGetValue(fullPath) with
            | true, state -> Ok state
            | _ ->
                let paths = projects.Keys |> Seq.map (fun p -> $"  - {p}") |> String.concat "\n"

                Error $"Project not loaded: {fullPath}\nLoaded projects:\n{paths}"

    interface IDisposable with
        member _.Dispose() =
            for state in projects.Values do
                dispose state

            projects.Clear()
