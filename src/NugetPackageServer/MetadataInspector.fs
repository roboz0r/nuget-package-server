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

module MetadataInspector =

    let private getRuntimeAssemblyPaths () =
        let runtimeDir = RuntimeEnvironment.GetRuntimeDirectory()
        Directory.GetFiles(runtimeDir, "*.dll") |> Array.toList

    let createContext (allDllPaths: string list) =
        let paths = allDllPaths @ getRuntimeAssemblyPaths () |> List.distinct

        let resolver = PathAssemblyResolver(paths)
        new MetadataLoadContext(resolver)

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
