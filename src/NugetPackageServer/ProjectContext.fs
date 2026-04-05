namespace NugetPackageServer

open System
open System.Collections.Generic
open System.IO
open System.Reflection

type ProjectState =
    {
        ProjectPath: string
        Project: ResolvedProject
        Context: MetadataLoadContext
        TypeIndex: TypeSummary array
        XmlDocCache: Dictionary<string, Dictionary<string, XmlDocEntry>>
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

                let allDllPaths = project.Packages |> List.collect (fun p -> p.DllPaths)

                let context = MetadataInspector.createContext allDllPaths

                let typeIndex =
                    project.Packages
                    |> List.collect (fun pkg ->
                        pkg.DllPaths
                        |> List.collect (fun dll -> MetadataInspector.getPublicTypes context dll pkg.Name)
                    )
                    |> List.toArray

                let xmlDocCache = Dictionary<string, Dictionary<string, XmlDocEntry>>()

                for pkg in project.Packages do
                    for xmlPath in pkg.XmlDocPaths do
                        let docs = XmlDocReader.parseXmlDocFile xmlPath

                        if docs.Count > 0 then
                            xmlDocCache.[xmlPath] <- docs

                Ok
                    {
                        ProjectPath = projectPath
                        Project = project
                        Context = context
                        TypeIndex = typeIndex
                        XmlDocCache = xmlDocCache
                    }

    let findXmlDoc (s: ProjectState) (memberId: string) =
        s.XmlDocCache.Values
        |> Seq.tryPick (fun docs ->
            match docs.TryGetValue(memberId) with
            | true, entry -> Some entry
            | _ -> None
        )

type ProjectContextManager() =
    let projects = Dictionary<string, ProjectState>(StringComparer.OrdinalIgnoreCase)

    let normalize (path: string) = Path.GetFullPath(path)

    member _.LoadProject(projectPath: string) =
        let fullPath = normalize projectPath

        if not (File.Exists fullPath) then
            Error $"Project file not found: {fullPath}"
        else

            // Dispose existing context for this path if reloading
            match projects.TryGetValue(fullPath) with
            | true, existing -> (existing :> IDisposable).Dispose()
            | _ -> ()

            match ProjectState.build fullPath with
            | Error msg ->
                projects.Remove(fullPath) |> ignore
                Error msg
            | Ok state ->
                projects.[fullPath] <- state
                Ok state

    member _.UnloadProject(projectPath: string) =
        let fullPath = normalize projectPath

        match projects.TryGetValue(fullPath) with
        | true, state ->
            (state :> IDisposable).Dispose()
            projects.Remove(fullPath) |> ignore
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
                (state :> IDisposable).Dispose()

            projects.Clear()
