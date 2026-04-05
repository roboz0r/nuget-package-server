namespace NugetPackageServer

open System.ComponentModel
open System.Runtime.InteropServices
open System.Text
open ModelContextProtocol.Server

[<McpServerToolType>]
type NugetTools(manager: ProjectContextManager) =

    let withProject (projectPath: string) (f: ProjectState -> string) =
        match manager.TryResolveProject(projectPath) with
        | Error msg -> msg
        | Ok state -> f state

    [<McpServerTool(Name = "load_project")>]
    [<Description("Load a .NET project's NuGet package information. Parses project.assets.json to discover all packages. Run 'dotnet restore' first if the project hasn't been restored.")>]
    member _.LoadProject([<Description("Path to the .csproj or .fsproj file")>] projectPath: string) =
        match manager.LoadProject(projectPath) with
        | Error msg -> $"Failed to load project: {msg}"
        | Ok state ->
            let direct = state.Project.Packages |> List.filter (fun p -> p.IsDirect)

            let transitive = state.Project.Packages |> List.filter (fun p -> not p.IsDirect)

            let sb = StringBuilder()

            sb.AppendLine($"Project loaded: {state.ProjectPath}") |> ignore

            sb.AppendLine($"Target Framework: {state.Project.TargetFramework}") |> ignore

            sb.AppendLine($"Types indexed: {state.TypeIndex.Length}") |> ignore

            sb.AppendLine(
                $"Packages: {state.Project.Packages.Length} ({direct.Length} direct, {transitive.Length} transitive)"
            )
            |> ignore

            sb.ToString()

    [<McpServerTool(Name = "unload_project")>]
    [<Description("Unload a previously loaded project to free memory")>]
    member _.UnloadProject([<Description("Path to the .csproj or .fsproj file to unload")>] projectPath: string) =
        match manager.UnloadProject(projectPath) with
        | Ok() -> $"Project unloaded: {projectPath}"
        | Error msg -> msg

    [<McpServerTool(Name = "list_project_packages")>]
    [<Description("List all NuGet packages (direct and transitive) referenced by the loaded project")>]
    member _.ListPackages
        ([<Optional;
           DefaultParameterValue(null: string);
           Description("Path to the project file. Optional if only one project is loaded.")>] projectPath: string)
        =
        withProject
            projectPath
            (fun state ->
                let packages = state.Project.Packages
                let sb = StringBuilder()
                sb.AppendLine($"Project: {state.ProjectPath}") |> ignore
                sb.AppendLine($"Target Framework: {state.Project.TargetFramework}") |> ignore
                sb.AppendLine($"Total packages: {packages.Length}") |> ignore
                sb.AppendLine() |> ignore

                let direct = packages |> List.filter (fun p -> p.IsDirect)
                let transitive = packages |> List.filter (fun p -> not p.IsDirect)

                if not direct.IsEmpty then
                    sb.AppendLine("## Direct Dependencies") |> ignore

                    for pkg in direct do
                        sb.AppendLine($"  {pkg.Name} {pkg.Version}") |> ignore

                if not transitive.IsEmpty then
                    sb.AppendLine() |> ignore
                    sb.AppendLine("## Transitive Dependencies") |> ignore

                    for pkg in transitive do
                        sb.AppendLine($"  {pkg.Name} {pkg.Version}") |> ignore

                sb.ToString()
            )

    [<McpServerTool(Name = "search_types")>]
    [<Description("Search for types across all loaded package assemblies. Returns up to 50 matches.")>]
    member _.SearchTypes
        (
            [<Description("Search term (case-insensitive substring match against full type name)")>] query: string,
            [<Optional; DefaultParameterValue(null: string); Description("Filter to a specific NuGet package name")>] packageName:
                string,
            [<Optional;
              DefaultParameterValue(null: string);
              Description("Filter to types in this namespace (prefix match)")>] ``namespace``: string,
            [<Optional;
              DefaultParameterValue(null: string);
              Description("Filter by type kind: class, interface, struct, enum, delegate")>] typeKind: string,
            [<Optional;
              DefaultParameterValue(null: string);
              Description("Path to the project file. Optional if only one project is loaded.")>] projectPath: string
        ) =
        withProject
            projectPath
            (fun state ->
                let results =
                    state.TypeIndex
                    |> Array.filter (fun t ->
                        (isNull query
                         || query.Length = 0
                         || t.FullName.Contains(query, System.StringComparison.OrdinalIgnoreCase))
                        && (isNull packageName
                            || packageName.Length = 0
                            || t.PackageName.Equals(packageName, System.StringComparison.OrdinalIgnoreCase))
                        && (isNull ``namespace``
                            || ``namespace``.Length = 0
                            || t.Namespace.StartsWith(``namespace``, System.StringComparison.OrdinalIgnoreCase))
                        && (isNull typeKind
                            || typeKind.Length = 0
                            || t.TypeKind.Equals(typeKind, System.StringComparison.OrdinalIgnoreCase))
                    )
                    |> Array.truncate 50

                let sb = StringBuilder()

                if results.Length = 0 then
                    sb.AppendLine("No types found matching the search criteria.") |> ignore
                else
                    sb.AppendLine($"Found {results.Length} type(s):") |> ignore
                    sb.AppendLine() |> ignore

                    for t in results do
                        sb.AppendLine($"  [{t.TypeKind}] {t.FullName}  ({t.PackageName})") |> ignore

                    if results.Length = 50 then
                        sb.AppendLine() |> ignore

                        sb.AppendLine("Results capped at 50. Refine your search with additional filters.")
                        |> ignore

                sb.ToString()
            )

    [<McpServerTool(Name = "get_type_definition")>]
    [<Description("Get the full API surface of a type including constructors, methods, properties, events, and inline XML doc summaries")>]
    member _.GetTypeDefinition
        (
            [<Description("Fully qualified type name, e.g. System.Text.Json.JsonSerializer")>] fullTypeName: string,
            [<Optional;
              DefaultParameterValue(null: string);
              Description("Path to the project file. Optional if only one project is loaded.")>] projectPath: string
        ) =
        withProject
            projectPath
            (fun state ->
                let entry = state.TypeIndex |> Array.tryFind (fun t -> t.FullName = fullTypeName)

                match entry with
                | None -> $"Type '{fullTypeName}' not found. Use search_types to find the correct name."
                | Some entry ->

                    match MetadataInspector.getTypeDefinition state.Context entry.AssemblyPath fullTypeName with
                    | None -> $"Type '{fullTypeName}' could not be loaded."
                    | Some typeDef ->

                        let sb = StringBuilder()

                        // Inline doc summary for the type
                        match ProjectState.findXmlDoc state $"T:{fullTypeName}" with
                        | Some doc ->
                            match doc.Summary with
                            | Some summary -> sb.AppendLine($"/// {summary}") |> ignore
                            | None -> ()
                        | None -> ()

                        // Type header
                        let header =
                            match typeDef.GenericParameters with
                            | [] -> $"{typeDef.TypeKind} {typeDef.FullName}"
                            | parms ->
                                let args = parms |> String.concat ", "
                                $"{typeDef.TypeKind} {typeDef.FullName}<{args}>"

                        let baseAndInterfaces =
                            [ yield! typeDef.BaseType |> Option.toList; yield! typeDef.Interfaces ]

                        match baseAndInterfaces with
                        | [] -> sb.AppendLine(header) |> ignore
                        | items ->
                            let inherits = items |> String.concat ", "
                            sb.AppendLine($"{header} : {inherits}") |> ignore

                        sb.AppendLine() |> ignore

                        let appendSection title items =
                            if not (List.isEmpty items) then
                                sb.AppendLine($"  // {title}") |> ignore

                                for item in items do
                                    sb.AppendLine($"  {item}") |> ignore

                                sb.AppendLine() |> ignore

                        appendSection "Constructors" typeDef.Constructors
                        appendSection "Properties" typeDef.Properties
                        appendSection "Methods" typeDef.Methods
                        appendSection "Events" typeDef.Events
                        appendSection "Fields" typeDef.Fields

                        sb.ToString()
            )

    [<McpServerTool(Name = "get_xml_documentation")>]
    [<Description("Get XML documentation comments for a type or member. Use member ID format: T:Namespace.Type, M:Namespace.Type.Method, P:Namespace.Type.Property")>]
    member _.GetXmlDocumentation
        (
            [<Description("Member ID in XML doc format (e.g. T:Namespace.Type, M:Namespace.Type.Method)")>] memberId:
                string,
            [<Optional;
              DefaultParameterValue(null: string);
              Description("Path to the project file. Optional if only one project is loaded.")>] projectPath: string
        ) =
        withProject
            projectPath
            (fun state ->
                match ProjectState.findXmlDoc state memberId with
                | None -> $"No documentation found for '{memberId}'."
                | Some doc ->

                    let sb = StringBuilder()
                    sb.AppendLine($"Documentation for {memberId}:") |> ignore
                    sb.AppendLine() |> ignore

                    match doc.Summary with
                    | Some s -> sb.AppendLine($"Summary: {s}") |> ignore
                    | None -> ()

                    if not doc.Params.IsEmpty then
                        sb.AppendLine() |> ignore
                        sb.AppendLine("Parameters:") |> ignore

                        for name, desc in doc.Params do
                            sb.AppendLine($"  {name}: {desc}") |> ignore

                    match doc.Returns with
                    | Some r ->
                        sb.AppendLine() |> ignore
                        sb.AppendLine($"Returns: {r}") |> ignore
                    | None -> ()

                    match doc.Remarks with
                    | Some r ->
                        sb.AppendLine() |> ignore
                        sb.AppendLine($"Remarks: {r}") |> ignore
                    | None -> ()

                    sb.ToString()
            )

    [<McpServerTool(Name = "refresh_project_context")>]
    [<Description("Reload a project's context after dependencies have changed. Run 'dotnet restore' first if you've modified the project file.")>]
    member _.RefreshProjectContext
        ([<Optional;
           DefaultParameterValue(null: string);
           Description("Path to the project file. Optional if only one project is loaded.")>] projectPath: string)
        =
        withProject
            projectPath
            (fun state ->
                match manager.LoadProject(state.ProjectPath) with
                | Ok newState ->
                    $"Project context refreshed successfully. Loaded {newState.Project.Packages.Length} packages, {newState.TypeIndex.Length} types."
                | Error msg -> $"Failed to refresh project context: {msg}"
            )
