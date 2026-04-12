module NugetPackageServer.Tests.Tests

open System
open System.IO
open Expecto
open NugetPackageServer

let private findRepoRoot () =
    let rec find (d: string) =
        if File.Exists(Path.Combine(d, "NugetPackageServer.slnx")) then
            d
        else
            let parent = Directory.GetParent(d)

            if isNull parent then
                failwith "Could not find repo root"

            find parent.FullName

    find AppContext.BaseDirectory

let private repoRoot = findRepoRoot ()

let private serverProjectDir = Path.Combine(repoRoot, "src", "NugetPackageServer")

let private serverProjectFile =
    Path.Combine(serverProjectDir, "NugetPackageServer.fsproj")

[<Tests>]
let preconditionTests =
    testList "Preconditions" [
        test "repo root was found" { Expect.isTrue (Directory.Exists repoRoot) $"Repo root should exist: {repoRoot}" }

        test "server project directory exists" {
            Expect.isTrue (Directory.Exists serverProjectDir) $"Server project dir should exist: {serverProjectDir}"
        }

        test "server project file exists" {
            Expect.isTrue (File.Exists serverProjectFile) $"Server .fsproj should exist: {serverProjectFile}"
        }
    ]

[<Tests>]
let assetsParserTests =
    testList "AssetsParser" [
        test "findAssetsFile succeeds for server project" {
            let result = AssetsParser.findAssetsFile serverProjectFile

            match result with
            | Ok path -> Expect.isTrue (File.Exists path) "assets file should exist"
            | Error msg -> failtest $"Expected Ok but got Error: {msg}"
        }

        test "parseAssetsFile returns packages" {
            let assetsPath =
                match AssetsParser.findAssetsFile serverProjectFile with
                | Ok p -> p
                | Error msg -> failtest msg

            let result = AssetsParser.parseAssetsFile assetsPath

            match result with
            | Ok project ->
                Expect.isNonEmpty project.Packages "should have packages"
                Expect.isNonEmpty project.TargetFramework "should have a target framework"

                let modelContextProtocol =
                    project.Packages
                    |> List.tryFind (fun p -> p.Name.Equals("ModelContextProtocol", StringComparison.OrdinalIgnoreCase))

                Expect.isSome modelContextProtocol "ModelContextProtocol should be in packages"
                Expect.isTrue modelContextProtocol.Value.IsDirect "ModelContextProtocol should be a direct dependency"
            | Error msg -> failtest $"Expected Ok but got Error: {msg}"
        }

        test "parseAssetsFile resolves DLL paths" {
            let assetsPath =
                match AssetsParser.findAssetsFile serverProjectFile with
                | Ok p -> p
                | Error msg -> failtest msg

            match AssetsParser.parseAssetsFile assetsPath with
            | Ok project ->
                for pkg in project.Packages do
                    for dll in pkg.DllPaths do
                        Expect.isTrue (File.Exists dll) $"DLL should exist: {dll}"
            | Error msg -> failtest msg
        }
    ]

[<Tests>]
let metadataInspectorTests =
    testList "MetadataInspector" [
        test "createContext and getPublicTypes works" {
            let assetsPath =
                match AssetsParser.findAssetsFile serverProjectFile with
                | Ok p -> p
                | Error msg -> failtest msg

            match AssetsParser.parseAssetsFile assetsPath with
            | Ok project ->
                let allDlls = project.Packages |> List.collect (fun p -> p.DllPaths)

                let context, _ =
                    MetadataInspector.createContext
                        {
                            TargetFramework = project.TargetFramework
                            PackageDllPaths = allDlls
                        }

                use context = context
                let pkg = project.Packages |> List.find (fun p -> not p.DllPaths.IsEmpty)

                let types =
                    match MetadataInspector.getPublicTypes context pkg.DllPaths.Head pkg.Name with
                    | Ok t -> t
                    | Error msg -> failtest msg

                Expect.isNonEmpty types $"Should find public types in {pkg.Name}"
            | Error msg -> failtest msg
        }

        test "getTypeDefinition returns type info" {
            let assetsPath =
                match AssetsParser.findAssetsFile serverProjectFile with
                | Ok p -> p
                | Error msg -> failtest msg

            match AssetsParser.parseAssetsFile assetsPath with
            | Ok project ->
                let allDlls = project.Packages |> List.collect (fun p -> p.DllPaths)

                let context, _ =
                    MetadataInspector.createContext
                        {
                            TargetFramework = project.TargetFramework
                            PackageDllPaths = allDlls
                        }

                use context = context
                let pkg = project.Packages |> List.find (fun p -> not p.DllPaths.IsEmpty)

                let types =
                    match MetadataInspector.getPublicTypes context pkg.DllPaths.Head pkg.Name with
                    | Ok t -> t
                    | Error msg -> failtest msg

                let firstType = types.Head

                match MetadataInspector.getTypeDefinition context firstType.AssemblyPath firstType.FullName with
                | Ok(Some _) -> ()
                | Ok None -> failtest $"Should get type definition for {firstType.FullName}"
                | Error msg -> failtest msg
            | Error msg -> failtest msg
        }
    ]

[<Tests>]
let projectContextManagerTests =
    testList "ProjectContextManager" [
        test "LoadProject and GetLoadedProjects works" {
            use mgr = new ProjectContextManager()

            match mgr.LoadProject(serverProjectFile) with
            | Error msg -> failtest msg
            | Ok state ->
                Expect.isNonEmpty state.Project.Packages "should have packages"
                Expect.equal mgr.ProjectCount 1 "should have 1 loaded project"
        }

        test "TryResolveProject works with single project" {
            use mgr = new ProjectContextManager()
            mgr.LoadProject(serverProjectFile) |> ignore

            match mgr.TryResolveProject(null) with
            | Error msg -> failtest msg
            | Ok state -> Expect.isNonEmpty state.Project.Packages "should have packages"
        }

        test "TryResolveProject error when no project loaded" {
            use mgr = new ProjectContextManager()

            match mgr.TryResolveProject(null) with
            | Ok _ -> failtest "Expected error when no project loaded"
            | Error msg -> Expect.stringContains msg "No project loaded" "should hint to call load_project"
        }

        test "SearchTypes via TryResolveProject" {
            use mgr = new ProjectContextManager()
            mgr.LoadProject(serverProjectFile) |> ignore

            match mgr.TryResolveProject(null) with
            | Error msg -> failtest msg
            | Ok state ->
                let results =
                    state.TypeIndex
                    |> Array.filter (fun t -> t.TypeKind = "interface")
                    |> Array.truncate 10

                Expect.isNonEmpty results "should find interfaces"

                for t in results do
                    Expect.equal t.TypeKind "interface" $"Expected interface but got {t.TypeKind}"
        }

        test "GetTypeDefinition via state" {
            use mgr = new ProjectContextManager()
            mgr.LoadProject(serverProjectFile) |> ignore

            match mgr.TryResolveProject(null) with
            | Error msg -> failtest msg
            | Ok state ->
                Expect.isNonEmpty state.TypeIndex "should have types"
                let entry = state.TypeIndex.[0]

                match MetadataInspector.getTypeDefinition state.Context entry.AssemblyPath entry.FullName with
                | Ok(Some _) -> ()
                | Ok None -> failtest $"Should get definition for {entry.FullName}"
                | Error msg -> failtest msg
        }

        test "Reload replaces context" {
            use mgr = new ProjectContextManager()
            mgr.LoadProject(serverProjectFile) |> ignore
            let before = (mgr.GetLoadedProjects() |> List.head).Project.Packages.Length
            mgr.LoadProject(serverProjectFile) |> ignore
            let after = (mgr.GetLoadedProjects() |> List.head).Project.Packages.Length
            Expect.equal after before "should have same packages after reload"
            Expect.equal mgr.ProjectCount 1 "should still have 1 project"
        }

        test "UnloadProject removes context" {
            use mgr = new ProjectContextManager()
            mgr.LoadProject(serverProjectFile) |> ignore
            Expect.equal mgr.ProjectCount 1 "should have 1 project"

            match mgr.UnloadProject(serverProjectFile) with
            | Error msg -> failtest msg
            | Ok() -> ()

            Expect.equal mgr.ProjectCount 0 "should have 0 projects after unload"
        }
    ]

[<Tests>]
let androidRefPacksTests =
    testList "AndroidRefPacks" [
        test "tryParseAndroidTfm matches net10.0-android36.0" {
            match AndroidRefPacks.tryParseAndroidTfm "net10.0-android36.0" with
            | Some parsed ->
                Expect.equal parsed.NetVersion "10.0" "NetVersion"
                Expect.equal parsed.ApiLevel 36 "ApiLevel"
            | None -> failtest "Expected Some"
        }

        test "tryParseAndroidTfm matches net6.0-android31" {
            match AndroidRefPacks.tryParseAndroidTfm "net6.0-android31" with
            | Some parsed ->
                Expect.equal parsed.NetVersion "6.0" "NetVersion"
                Expect.equal parsed.ApiLevel 31 "ApiLevel"
            | None -> failtest "Expected Some"
        }

        test "tryParseAndroidTfm returns None for net10.0" {
            Expect.isNone (AndroidRefPacks.tryParseAndroidTfm "net10.0") "plain TFM should not parse"
        }

        test "tryParseAndroidTfm returns None for net10.0-ios18.0" {
            Expect.isNone (AndroidRefPacks.tryParseAndroidTfm "net10.0-ios18.0") "iOS TFM should not parse"
        }

        test "findAndroidRefPack picks the highest version that has a matching ref folder" {
            let fixture =
                Path.Combine(Path.GetTempPath(), $"nps-android-fixture-{Guid.NewGuid():N}")

            let packDir = Path.Combine(fixture, "packs", "Microsoft.Android.Ref.36")

            try
                // Higher version that doesn't have net10.0 — should be skipped.
                Directory.CreateDirectory(Path.Combine(packDir, "99.0.1", "ref", "net11.0"))
                |> ignore

                // Lower version that does have net10.0 — should be selected.
                let targetRef = Path.Combine(packDir, "36.1.43", "ref", "net10.0")
                Directory.CreateDirectory(targetRef) |> ignore
                File.WriteAllBytes(Path.Combine(targetRef, "Mono.Android.dll"), [||])

                // Middle version with net10.0 but higher than 36.1.43 — should win.
                let winnerRef = Path.Combine(packDir, "36.2.0", "ref", "net10.0")
                Directory.CreateDirectory(winnerRef) |> ignore
                File.WriteAllBytes(Path.Combine(winnerRef, "Mono.Android.dll"), [||])

                match AndroidRefPacks.findAndroidRefPack fixture 36 "10.0" with
                | Ok path ->
                    Expect.equal (Path.GetFullPath path) (Path.GetFullPath winnerRef) "highest valid version wins"
                | Error msg -> failtest msg
            finally
                if Directory.Exists fixture then
                    Directory.Delete(fixture, true)
        }

        test "findAndroidRefPack errors when pack dir is missing" {
            let fixture =
                Path.Combine(Path.GetTempPath(), $"nps-android-missing-{Guid.NewGuid():N}")

            try
                Directory.CreateDirectory(fixture) |> ignore

                match AndroidRefPacks.findAndroidRefPack fixture 36 "10.0" with
                | Ok _ -> failtest "expected Error"
                | Error msg -> Expect.stringContains msg "Microsoft.Android.Ref.36" "error names the pack"
            finally
                if Directory.Exists fixture then
                    Directory.Delete(fixture, true)
        }

        test "ensureResourceDesignerStub emits an assembly loadable by MetadataLoadContext" {
            match AndroidRefPacks.ensureResourceDesignerStub () with
            | Error msg -> failtest msg
            | Ok path ->
                Expect.isTrue (File.Exists path) $"stub file should exist at {path}"

                let runtimeDlls =
                    Directory.GetFiles(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")
                    |> Array.toList

                let resolver = Reflection.PathAssemblyResolver(path :: runtimeDlls)
                use ctx = new Reflection.MetadataLoadContext(resolver)
                let asm = ctx.LoadFromAssemblyPath(path)
                Expect.equal (asm.GetName().Name) "_Microsoft.Android.Resource.Designer" "assembly name matches"
        }

        test "getAndroidRefAssemblyPaths returns NotAndroid for plain TFM" {
            match AndroidRefPacks.getAndroidRefAssemblyPaths "net10.0" with
            | NotAndroid -> ()
            | other -> failtest $"expected NotAndroid, got {other}"
        }
    ]

[<Tests>]
let typeFormattingTests =
    testList "TypeFormatting" [
        test "formatTypeName handles common types" {
            Expect.equal (TypeFormatting.formatTypeName typeof<string>) "string" "string"
            Expect.equal (TypeFormatting.formatTypeName typeof<int>) "int" "int"
            Expect.equal (TypeFormatting.formatTypeName typeof<bool>) "bool" "bool"
            Expect.equal (TypeFormatting.formatTypeName typeof<obj>) "object" "object"
        }

        test "formatTypeName handles arrays" {
            Expect.equal (TypeFormatting.formatTypeName typeof<int[]>) "int[]" "int array"
        }

        test "formatTypeName handles generics" {
            let t = typeof<System.Collections.Generic.List<string>>
            let name = TypeFormatting.formatTypeName t
            Expect.stringContains name "List" "should contain List"
            Expect.stringContains name "string" "should contain string"
        }

        test "formatTypeKind classifies correctly" {
            Expect.equal (TypeFormatting.formatTypeKind typeof<IDisposable>) "interface" "interface"
            Expect.equal (TypeFormatting.formatTypeKind typeof<DayOfWeek>) "enum" "enum"
            Expect.equal (TypeFormatting.formatTypeKind typeof<DateTime>) "struct" "struct"
        }
    ]
