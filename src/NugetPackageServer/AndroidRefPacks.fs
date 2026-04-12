namespace NugetPackageServer

open System
open System.IO
open System.Reflection
open System.Reflection.Emit
open System.Runtime.InteropServices
open System.Text.RegularExpressions
open NuGet.Versioning

type AndroidTfm = { NetVersion: string; ApiLevel: int }

type AndroidRefResult =
    | NotAndroid
    | Resolved of refFolder: string * paths: string list
    | MissingPack of message: string

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

    let getAndroidRefAssemblyPaths (tfm: string) : AndroidRefResult =
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

                    let stubPaths =
                        match ensureResourceDesignerStub () with
                        | Ok path -> [ path ]
                        | Error _ -> []

                    Resolved(refFolder, stubPaths @ refDlls)
