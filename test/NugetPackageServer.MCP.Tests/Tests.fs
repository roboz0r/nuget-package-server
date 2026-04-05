module NugetPackageServer.MCP.Tests.Tests

open System
open System.IO
open System.Collections.Generic
open System.Collections.ObjectModel
open Expecto
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol

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

let private targetProjectFile =
    Path.Combine(serverProjectDir, "NugetPackageServer.fsproj")

let private createClient () =
    task {
        let options =
            StdioClientTransportOptions(
                Name = "nuget-package-server",
                Command = "dotnet",
                Arguments = [| "run"; "--no-build"; "--project"; serverProjectDir |]
            )

        let transport = new StdioClientTransport(options)

        let! client =
            McpClient.CreateAsync(
                transport,
                McpClientOptions(ClientInfo = Implementation(Name = "test-client", Version = "0.1.0"))
            )

        return client
    }

let private makeArgs (pairs: (string * obj) list) : IReadOnlyDictionary<string, obj> =
    let d = Dictionary<string, obj>()

    for k, v in pairs do
        d.[k] <- v

    ReadOnlyDictionary(d)

let private getTextContent (result: CallToolResult) =
    let block = result.Content |> Seq.head

    match block with
    | :? TextContentBlock as tc -> tc.Text
    | other -> failtest $"Expected TextContentBlock but got {other.GetType().Name}"

let private callTool (client: McpClient) (name: string) (args: IReadOnlyDictionary<string, obj> option) =
    task {
        let! result =
            match args with
            | Some a -> client.CallToolAsync(name, a)
            | None -> client.CallToolAsync(name, null)

        return result
    }

let private loadProject (client: McpClient) =
    task {
        let args = makeArgs [ "projectPath", box targetProjectFile ]
        let! result = callTool client "load_project" (Some args)
        return getTextContent result
    }

let private serverDll =
    // The build output path depends on the configuration; check common locations.
    let debugPath =
        Path.Combine(serverProjectDir, "bin", "Debug", "net10.0", "NugetPackageServer.dll")

    let releasePath =
        Path.Combine(serverProjectDir, "bin", "Release", "net10.0", "NugetPackageServer.dll")

    if File.Exists(debugPath) then Some debugPath
    elif File.Exists(releasePath) then Some releasePath
    else None

[<Tests>]
let preconditionTests =
    testList "MCP E2E preconditions" [
        test "repo root was found" { Expect.isTrue (Directory.Exists repoRoot) $"Repo root should exist: {repoRoot}" }

        test "server project directory exists" {
            Expect.isTrue (Directory.Exists serverProjectDir) $"Server project dir should exist: {serverProjectDir}"
        }

        test "server project file exists" {
            Expect.isTrue (File.Exists targetProjectFile) $"Server .fsproj should exist: {targetProjectFile}"
        }

        test "server has been built" {
            Expect.isSome
                serverDll
                $"Server DLL not found. Run 'dotnet build' on the server project before running E2E tests. Checked: {serverProjectDir}/bin/[Debug|Release]/net10.0/NugetPackageServer.dll"
        }
    ]

[<Tests>]
let mcpTests =
    testList "MCP E2E" [
        testTask "server lists tools including load_project" {
            use! (client: McpClient) = createClient ()
            let! tools = client.ListToolsAsync()
            let toolNames = tools |> Seq.map (fun (t: McpClientTool) -> t.Name) |> Seq.toList
            Expect.contains toolNames "load_project" "should have load_project"
            Expect.contains toolNames "load_package" "should have load_package"
            Expect.contains toolNames "unload_project" "should have unload_project"
            Expect.contains toolNames "list_project_packages" "should have list_project_packages"
            Expect.contains toolNames "list_namespaces" "should have list_namespaces"
            Expect.contains toolNames "search_types" "should have search_types"
            Expect.contains toolNames "search_members" "should have search_members"
            Expect.contains toolNames "get_type_definition" "should have get_type_definition"
            Expect.contains toolNames "get_xml_documentation" "should have get_xml_documentation"
            Expect.contains toolNames "refresh_project_context" "should have refresh_project_context"
        }

        testTask "tools return error hint when no project loaded" {
            use! client = createClient ()
            let! result = callTool client "list_project_packages" None
            let text = getTextContent result
            Expect.stringContains text "No project loaded" "should hint to load project"
        }

        testTask "load_project loads project" {
            use! client = createClient ()
            let! text = loadProject client
            Expect.stringContains text "Project loaded" "should confirm loading"
            Expect.stringContains text "Types indexed" "should report type count"
            Expect.stringContains text "Members indexed" "should report member count"
        }

        testTask "list_project_packages returns packages after load" {
            use! client = createClient ()
            let! _ = loadProject client
            let! result = callTool client "list_project_packages" None
            let text = getTextContent result
            Expect.stringContains text "ModelContextProtocol" "should list ModelContextProtocol"
            Expect.stringContains text "Direct Dependencies" "should have direct deps section"
        }

        testTask "search_types finds types after load" {
            use! client = createClient ()
            let! _ = loadProject client
            let args = makeArgs [ "query", box "Implementation" ]
            let! result = callTool client "search_types" (Some args)
            let text = getTextContent result
            Expect.stringContains text "Found" "should find matching types"
        }

        testTask "get_type_definition returns type info after load" {
            use! client = createClient ()
            let! _ = loadProject client

            let args =
                makeArgs [ "fullTypeName", box "ModelContextProtocol.Protocol.Implementation" ]

            let! result = callTool client "get_type_definition" (Some args)
            let text = getTextContent result
            Expect.stringContains text "class" "should describe a class"
        }

        testTask "refresh_project_context succeeds after load" {
            use! client = createClient ()
            let! _ = loadProject client
            let! result = callTool client "refresh_project_context" None
            let text = getTextContent result
            Expect.stringContains text "refreshed successfully" "should succeed"
        }

        testTask "load_package loads a NuGet package into isolated context" {
            use! client = createClient ()

            let args =
                makeArgs [
                    "name", box "Humanizer.Core"
                    "version", box "2.14.1"
                    "tfm", box "net10.0"
                    "workingDirectory", box repoRoot
                ]

            let! result = callTool client "load_package" (Some args)
            let text = getTextContent result
            Expect.stringContains text "Package loaded" "should confirm loading"
            Expect.stringContains text "Types indexed" "should report type count"
        }

        testTask "unload_project removes project" {
            use! client = createClient ()
            let! _ = loadProject client
            let args = makeArgs [ "projectPath", box targetProjectFile ]
            let! result = callTool client "unload_project" (Some args)
            let text = getTextContent result
            Expect.stringContains text "unloaded" "should confirm unload"
            // After unload, tools should error
            let! result2 = callTool client "list_project_packages" None
            let text2 = getTextContent result2
            Expect.stringContains text2 "No project loaded" "should hint to load again"
        }
    ]
