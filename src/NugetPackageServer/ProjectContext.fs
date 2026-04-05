namespace NugetPackageServer

open System
open System.Collections.Concurrent
open System.Collections.Generic
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

                let context = MetadataInspector.createContext allDllPaths

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
