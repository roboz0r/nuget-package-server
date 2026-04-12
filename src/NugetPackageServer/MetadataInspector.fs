namespace NugetPackageServer

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Runtime.InteropServices

type TypeSummary =
    {
        FullName: string
        PackageName: string
        Namespace: string
        TypeKind: string
        AssemblyPath: string
    }

type TypeDefinition =
    {
        FullName: string
        TypeKind: string
        BaseType: string option
        Interfaces: string list
        GenericParameters: string list
        Constructors: string list
        Properties: string list
        Methods: string list
        Events: string list
        Fields: string list
    }

type MemberSummary =
    {
        MemberName: string
        MemberKind: string
        DeclaringType: string
        PackageName: string
    }

type ContextInputs =
    {
        TargetFramework: string
        PackageDllPaths: string list
        ProjectPath: string option
    }

module MetadataInspector =

    let private getRuntimeAssemblyPaths () =
        let runtimeDir = RuntimeEnvironment.GetRuntimeDirectory()
        Directory.GetFiles(runtimeDir, "*.dll") |> Array.toList

    let createContext (inputs: ContextInputs) : MetadataLoadContext * string list =
        let androidResult =
            AndroidRefPacks.getAndroidRefAssemblyPaths inputs.TargetFramework

        let diagnostics = ResizeArray<string>()

        let extraPaths, stubPath =
            match androidResult with
            | NotAndroid -> [], None
            | Resolved(refFolder, paths, stub) ->
                diagnostics.Add($"Android TFM detected: using ref pack at {refFolder}")
                paths, stub
            | MissingPack msg ->
                diagnostics.Add($"Android TFM detected but ref pack could not be resolved: {msg}")
                [], None

        // When the project has been built, prefer the real Resource.Designer over the stub:
        // it has the correct strong name / PKT, so PathAssemblyResolver accepts it directly
        // and it carries real resource IDs instead of an empty module.
        let realDesignerPath =
            match inputs.ProjectPath, androidResult with
            | Some projectPath, Resolved _ ->
                match AndroidRefPacks.findRealResourceDesigner projectPath inputs.TargetFramework with
                | Some path ->
                    diagnostics.Add($"Using real Resource.Designer assembly from {path}")
                    Some path
                | None -> None
            | _ -> None

        let designerPath =
            match realDesignerPath with
            | Some _ -> realDesignerPath
            | None -> stubPath

        let paths =
            inputs.PackageDllPaths
            @ extraPaths
            @ (realDesignerPath |> Option.toList)
            @ getRuntimeAssemblyPaths ()
            |> List.distinct

        let resolver: MetadataAssemblyResolver =
            match designerPath with
            | Some designer -> AndroidAwareAssemblyResolver(paths, designer)
            | None -> PathAssemblyResolver(paths)

        new MetadataLoadContext(resolver), List.ofSeq diagnostics

    let getPublicTypes (context: MetadataLoadContext) (dllPath: string) (packageName: string) =
        try
            let assembly = context.LoadFromAssemblyPath(dllPath)

            assembly.GetExportedTypes()
            |> Array.choose (fun t ->
                match t.FullName with
                | null -> None
                | fullName ->
                    Some
                        {
                            FullName = fullName
                            PackageName = packageName
                            Namespace = t.Namespace |> Option.ofObj |> Option.defaultValue ""
                            TypeKind = TypeFormatting.formatTypeKind t
                            AssemblyPath = dllPath
                        }
            )
            |> Array.toList
            |> Ok
        with ex ->
            Error $"Failed to load types from {dllPath}: {ex.Message}"

    let getPublicMembers (context: MetadataLoadContext) (dllPath: string) (packageName: string) =
        try
            let assembly = context.LoadFromAssemblyPath(dllPath)

            let publicDeclared =
                BindingFlags.Public
                ||| BindingFlags.Instance
                ||| BindingFlags.Static
                ||| BindingFlags.DeclaredOnly

            assembly.GetExportedTypes()
            |> Array.collect (fun t ->
                match t.FullName with
                | null -> Array.empty
                | typeName ->
                    let members = ResizeArray<MemberSummary>()

                    for c in t.GetConstructors(BindingFlags.Public ||| BindingFlags.Instance) do
                        members.Add(
                            {
                                MemberName = ".ctor"
                                MemberKind = "constructor"
                                DeclaringType = typeName
                                PackageName = packageName
                            }
                        )

                    for p in t.GetProperties(publicDeclared) do
                        members.Add(
                            {
                                MemberName = p.Name
                                MemberKind = "property"
                                DeclaringType = typeName
                                PackageName = packageName
                            }
                        )

                    for m in t.GetMethods(publicDeclared) do
                        if not m.IsSpecialName then
                            members.Add(
                                {
                                    MemberName = m.Name
                                    MemberKind = "method"
                                    DeclaringType = typeName
                                    PackageName = packageName
                                }
                            )

                    for e in t.GetEvents(publicDeclared) do
                        members.Add(
                            {
                                MemberName = e.Name
                                MemberKind = "event"
                                DeclaringType = typeName
                                PackageName = packageName
                            }
                        )

                    for f in t.GetFields(publicDeclared) do
                        if not f.IsSpecialName then
                            let kind = if t.IsEnum then "enum value" else "field"

                            members.Add(
                                {
                                    MemberName = f.Name
                                    MemberKind = kind
                                    DeclaringType = typeName
                                    PackageName = packageName
                                }
                            )

                    members.ToArray()
            )
            |> Array.toList
            |> Ok
        with ex ->
            Error $"Failed to load members from {dllPath}: {ex.Message}"

    let getTypeDefinition (context: MetadataLoadContext) (assemblyPath: string) (fullTypeName: string) =
        try
            let assembly = context.LoadFromAssemblyPath(assemblyPath)
            let t = assembly.GetType(fullTypeName)

            if isNull t then
                Ok None
            else

                let publicInstance = BindingFlags.Public ||| BindingFlags.Instance

                let publicStatic = BindingFlags.Public ||| BindingFlags.Static

                let allPublic = publicInstance ||| publicStatic ||| BindingFlags.DeclaredOnly

                let constructors =
                    t.GetConstructors(publicInstance)
                    |> Array.map TypeFormatting.formatConstructor
                    |> Array.toList

                let properties =
                    t.GetProperties(allPublic)
                    |> Array.map TypeFormatting.formatProperty
                    |> Array.toList

                let methods =
                    t.GetMethods(allPublic)
                    |> Array.filter (fun m -> not m.IsSpecialName)
                    |> Array.map TypeFormatting.formatMethod
                    |> Array.toList

                let events =
                    t.GetEvents(allPublic) |> Array.map TypeFormatting.formatEvent |> Array.toList

                let fields =
                    if t.IsEnum then
                        t.GetFields(BindingFlags.Public ||| BindingFlags.Static)
                        |> Array.map (fun f -> f.Name)
                        |> Array.toList
                    else
                        t.GetFields(allPublic)
                        |> Array.filter (fun f -> not f.IsSpecialName)
                        |> Array.map TypeFormatting.formatField
                        |> Array.toList

                let baseType =
                    if
                        isNull t.BaseType
                        || t.BaseType.FullName = "System.Object"
                        || t.BaseType.FullName = "System.ValueType"
                        || t.BaseType.FullName = "System.Enum"
                    then
                        None
                    else
                        Some(TypeFormatting.formatTypeName t.BaseType)

                let interfaces =
                    t.GetInterfaces() |> Array.map TypeFormatting.formatTypeName |> Array.toList

                let genericParams =
                    if t.IsGenericType then
                        t.GetGenericArguments() |> Array.map (fun a -> a.Name) |> Array.toList
                    else
                        []

                Ok(
                    Some
                        {
                            FullName = fullTypeName
                            TypeKind = TypeFormatting.formatTypeKind t
                            BaseType = baseType
                            Interfaces = interfaces
                            GenericParameters = genericParams
                            Constructors = constructors
                            Properties = properties
                            Methods = methods
                            Events = events
                            Fields = fields
                        }
                )
        with ex ->
            Error $"Failed to load type '{fullTypeName}' from {assemblyPath}: {ex.Message}"
