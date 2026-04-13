namespace NugetPackageServer

open System
open System.IO
open System.Reflection
open System.Reflection.Emit
open System.Runtime.InteropServices
open System.Text.RegularExpressions
open NuGet.Versioning

type AndroidTfm = { NetVersion: string; ApiLevel: int }

/// Where the `_Microsoft.Android.Resource.Designer` assembly came from.
/// `RealDesigner` is the strong-named DLL the Android SDK build targets produced in
/// `obj/` and is preferred when available; `StubDesigner` is our emitted empty assembly.
type DesignerSource =
    | RealDesigner of path: string
    | StubDesigner of path: string

    member this.Path =
        match this with
        | RealDesigner p
        | StubDesigner p -> p

type AndroidRefResult =
    | NotAndroid
    | Resolved of refFolder: string * paths: string list * designer: DesignerSource option
    | MissingPack of message: string

/// Wraps PathAssemblyResolver with a bypass for `_Microsoft.Android.Resource.Designer`.
/// The real designer assembly is generated per-project and strong-named
/// (PublicKeyToken=1e9360d6629057ee). Package DLLs reference it by that identity, so
/// PathAssemblyResolver filters out our unsigned stub on PKT mismatch and returns null.
/// This resolver returns the stub unconditionally whenever the simple name matches.
type AndroidAwareAssemblyResolver(paths: string seq, stubPath: string) =
    inherit MetadataAssemblyResolver()

    static let stubSimpleName = "_Microsoft.Android.Resource.Designer"
    let inner = PathAssemblyResolver(paths)

    member _.StubPath = stubPath

    override _.Resolve(context, assemblyName) =
        if String.Equals(assemblyName.Name, stubSimpleName, StringComparison.OrdinalIgnoreCase) then
            context.LoadFromAssemblyPath(stubPath)
        else
            inner.Resolve(context, assemblyName)

module AndroidRefPacks =

    let private androidTfmRegex =
        Regex(@"^net(\d+\.\d+)-android(\d+)(?:\.\d+)?$", RegexOptions.Compiled)

    let tryParseAndroidTfm (tfm: string) : AndroidTfm option =
        if String.IsNullOrEmpty tfm then
            None
        else
            let m = androidTfmRegex.Match(tfm)

            if m.Success then
                Some
                    {
                        NetVersion = m.Groups.[1].Value
                        ApiLevel = Int32.Parse(m.Groups.[2].Value)
                    }
            else
                None

    let getDotnetRoot () : string option =
        let envRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT")

        if not (String.IsNullOrWhiteSpace envRoot) && Directory.Exists envRoot then
            Some envRoot
        else
            let rec walkUp (dir: string) =
                if String.IsNullOrEmpty dir then
                    None
                elif
                    Directory.Exists(Path.Combine(dir, "packs"))
                    && Directory.Exists(Path.Combine(dir, "shared"))
                then
                    Some dir
                else
                    match Directory.GetParent(dir) with
                    | null -> None
                    | parent -> walkUp parent.FullName

            walkUp (RuntimeEnvironment.GetRuntimeDirectory())

    // TODO: canonical lookup is sdk-manifests/<band>/microsoft.net.sdk.android/WorkloadManifest.json
    // → pack version → packs dir. Directory enumeration covers 99% of cases for 10% of the code;
    // workload-manifest lookup is a future hardening when side-by-side SDKs disagree.
    let findAndroidRefPack (dotnetRoot: string) (apiLevel: int) (netVersion: string) : Result<string, string> =
        let packRoot =
            Path.Combine(dotnetRoot, "packs", $"Microsoft.Android.Ref.{apiLevel}")

        if not (Directory.Exists packRoot) then
            Error
                $"Android ref pack not found: {packRoot}. Install the .NET Android workload (dotnet workload install android)."
        else
            let candidates =
                Directory.GetDirectories(packRoot)
                |> Array.choose (fun dir ->
                    let name = Path.GetFileName(dir)
                    let mutable parsed = Unchecked.defaultof<NuGetVersion>

                    if NuGetVersion.TryParse(name, &parsed) then
                        Some(parsed, dir)
                    else
                        None
                )
                |> Array.sortByDescending fst

            let refSubpath = Path.Combine("ref", $"net{netVersion}")

            let found =
                candidates
                |> Array.tryPick (fun (_, dir) ->
                    let refDir = Path.Combine(dir, refSubpath)

                    if File.Exists(Path.Combine(refDir, "Mono.Android.dll")) then
                        Some refDir
                    else
                        None
                )

            match found with
            | Some dir -> Ok dir
            | None ->
                Error
                    $"No version under {packRoot} has ref/net{netVersion}/Mono.Android.dll. Install a Microsoft.Android.Ref.{apiLevel} pack matching net{netVersion}."

    let private stubLock = obj ()
    let mutable private cachedStubPath: string option = None

    let ensureResourceDesignerStub () : Result<string, string> =
        let existing =
            match cachedStubPath with
            | Some p when File.Exists p -> Some p
            | _ -> None

        match existing with
        | Some p -> Ok p
        | None ->
            lock
                stubLock
                (fun () ->
                    match cachedStubPath with
                    | Some p when File.Exists p -> Ok p
                    | _ ->
                        try
                            let dir = Path.Combine(Path.GetTempPath(), "NugetPackageServer")
                            Directory.CreateDirectory(dir) |> ignore

                            let filePath = Path.Combine(dir, "_Microsoft.Android.Resource.Designer.dll")

                            // If a previous process (MCP server, test host) already emitted the
                            // stub, reuse it. The stub is deterministic, and attempting to
                            // overwrite a file still held by another process would fail.
                            if File.Exists filePath then
                                cachedStubPath <- Some filePath
                                Ok filePath
                            else
                                let name = AssemblyName("_Microsoft.Android.Resource.Designer")
                                let coreAssembly = typeof<obj>.Assembly
                                let builder = PersistedAssemblyBuilder(name, coreAssembly)

                                let modBuilder = builder.DefineDynamicModule("_Microsoft.Android.Resource.Designer")

                                let tb =
                                    modBuilder.DefineType(
                                        "<StubModule>",
                                        TypeAttributes.NotPublic ||| TypeAttributes.Class
                                    )

                                tb.CreateType() |> ignore

                                builder.Save(filePath)
                                cachedStubPath <- Some filePath
                                Ok filePath
                        with ex ->
                            Error $"Failed to emit _Microsoft.Android.Resource.Designer stub: {ex.Message}"
                )

    /// Locate the real `_Microsoft.Android.Resource.Designer.dll` generated by the
    /// Android SDK build targets under `<projectDir>/obj/**/`. It's strong-named with
    /// the correct PKT, so it resolves cleanly for every package reference and carries
    /// the actual resource IDs instead of an empty module.
    ///
    /// The Android SDK strips the API-level suffix in obj paths — a project with TFM
    /// `net10.0-android36.0` writes to `obj/<Config>/net10.0-android/`. We accept both
    /// the full spelling and the stripped form, then pick the newest match by mtime
    /// (so Debug vs Release vs multi-config all work without guessing the active one).
    let findRealResourceDesigner (projectPath: string) (tfm: string) : string option =
        try
            let projectDir = Path.GetDirectoryName(projectPath)

            if String.IsNullOrEmpty projectDir then
                None
            else
                let objDir = Path.Combine(projectDir, "obj")

                if not (Directory.Exists objDir) then
                    None
                else
                    let candidateParents =
                        match tryParseAndroidTfm tfm with
                        | Some parsed -> [ tfm; $"net{parsed.NetVersion}-android" ]
                        | None -> [ tfm ]

                    Directory.EnumerateFiles(
                        objDir,
                        "_Microsoft.Android.Resource.Designer.dll",
                        SearchOption.AllDirectories
                    )
                    |> Seq.filter (fun path ->
                        let parent = Path.GetDirectoryName(path)
                        let parentName = Path.GetFileName(parent)

                        candidateParents
                        |> List.exists (fun c -> String.Equals(c, parentName, StringComparison.OrdinalIgnoreCase))
                    )
                    |> Seq.sortByDescending (fun path -> File.GetLastWriteTimeUtc(path))
                    |> Seq.tryHead
        with _ ->
            None

    let getAndroidRefAssemblyPaths (projectPath: string option) (tfm: string) : AndroidRefResult =
        match tryParseAndroidTfm tfm with
        | None -> NotAndroid
        | Some parsed ->
            match getDotnetRoot () with
            | None ->
                MissingPack "Could not locate DOTNET_ROOT or a dotnet install directory containing a 'packs' folder."
            | Some dotnetRoot ->
                match findAndroidRefPack dotnetRoot parsed.ApiLevel parsed.NetVersion with
                | Error msg -> MissingPack msg
                | Ok refFolder ->
                    let refDlls = Directory.GetFiles(refFolder, "*.dll") |> Array.toList

                    // Prefer the real Resource.Designer assembly (built under obj/) when
                    // available — it's strong-named with the correct PKT and has real
                    // resource IDs. Only fall back to emitting a stub when the project
                    // isn't built for this TFM, and never include both in the path list
                    // (PathAssemblyResolver's tie-break between two 1.0.0.0 candidates is
                    // undefined).
                    let realPath = projectPath |> Option.bind (fun p -> findRealResourceDesigner p tfm)

                    let designer =
                        match realPath with
                        | Some p -> Some(RealDesigner p)
                        | None ->
                            match ensureResourceDesignerStub () with
                            | Ok p -> Some(StubDesigner p)
                            | Error _ -> None

                    let allPaths =
                        match designer with
                        | Some d -> d.Path :: refDlls
                        | None -> refDlls

                    Resolved(refFolder, allPaths, designer)
