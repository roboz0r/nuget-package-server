namespace NugetPackageServer

open System.IO
open NuGet.ProjectModel

type ResolvedPackage =
    {
        Name: string
        Version: string
        DllPaths: string list
        XmlDocPaths: string list
        IsDirect: bool
    }

type ResolvedProject =
    {
        TargetFramework: string
        Packages: ResolvedPackage list
        PackageFolders: string list
    }

module AssetsParser =

    let findAssetsFile (projectPath: string) =
        let dir =
            if File.Exists(projectPath) then
                Path.GetDirectoryName(projectPath)
            else
                projectPath

        let assetsPath = Path.Combine(dir, "obj", "project.assets.json")

        if File.Exists(assetsPath) then
            Ok assetsPath
        else
            Error $"project.assets.json not found at {assetsPath}. Run 'dotnet restore' first."

    let parseAssetsFile (assetsPath: string) =
        let lockFile = LockFileFormat().Read(assetsPath)

        let packageFolders =
            lockFile.PackageFolders |> Seq.map (fun f -> f.Path) |> Seq.toList

        let target = lockFile.Targets |> Seq.tryHead

        match target with
        | None -> Error "No targets found in project.assets.json"
        | Some target ->

            let tfm = target.TargetFramework.GetShortFolderName()

            let directDeps =
                lockFile.PackageSpec.TargetFrameworks
                |> Seq.collect (fun tf -> tf.Dependencies)
                |> Seq.map (fun d -> d.Name.ToLowerInvariant())
                |> Set.ofSeq

            let resolvePackagePaths (lib: LockFileTargetLibrary) =
                let libraryInfo =
                    lockFile.Libraries
                    |> Seq.tryFind (fun l -> l.Name = lib.Name && l.Version = lib.Version)

                match libraryInfo with
                | None -> None
                | Some libInfo ->

                    let packageDir =
                        packageFolders
                        |> List.tryPick (fun folder ->
                            let dir = Path.Combine(folder, libInfo.Path)
                            if Directory.Exists(dir) then Some dir else None
                        )

                    match packageDir with
                    | None -> None
                    | Some pkgDir ->

                        let dllPaths =
                            lib.CompileTimeAssemblies
                            |> Seq.map (fun a -> Path.Combine(pkgDir, a.Path.Replace('/', Path.DirectorySeparatorChar)))
                            |> Seq.filter File.Exists
                            |> Seq.toList

                        let xmlDocPaths =
                            dllPaths
                            |> List.map (fun dll -> Path.ChangeExtension(dll, ".xml"))
                            |> List.filter File.Exists

                        if dllPaths.IsEmpty then
                            None
                        else
                            Some
                                {
                                    Name = lib.Name
                                    Version = string lib.Version
                                    DllPaths = dllPaths
                                    XmlDocPaths = xmlDocPaths
                                    IsDirect = directDeps.Contains(lib.Name.ToLowerInvariant())
                                }

            let packages =
                target.Libraries
                |> Seq.filter (fun l -> l.Type = "package")
                |> Seq.choose resolvePackagePaths
                |> Seq.toList

            Ok
                {
                    TargetFramework = tfm
                    Packages = packages
                    PackageFolders = packageFolders
                }
