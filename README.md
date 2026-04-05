# Nuget Package Server

An MCP server that lets AI agents browse the actual API surface of NuGet packages referenced by .NET projects. Instead of hallucinating APIs, agents can inspect real type definitions, method signatures, and XML documentation.

Uses `MetadataLoadContext` for safe, inspection-only assembly loading (no code execution) and parses `project.assets.json` for accurate dependency resolution.

## Installation

Requires the .NET 10 SDK or later.

### Global tool

```bash
dotnet tool install -g NugetPackageServer
```

### Local tool (per-project)

```bash
dotnet new tool-manifest # if you don't have one yet
dotnet tool install NugetPackageServer
```

## Setup

Add the MCP server to your agent's configuration.

### Claude Code (global)

Add to `~/.claude.json`:

```json
{
  "mcpServers": {
    "nuget-package-server": {
      "command": "nuget-package-server"
    }
  }
}
```

### Claude Code (per-project)

Add to `.mcp.json` in your project root:

```json
{
  "mcpServers": {
    "nuget-package-server": {
      "command": "dotnet",
      "args": ["nuget-package-server"]
    }
  }
}
```

Use `"command": "dotnet"` with `"args": ["nuget-package-server"]` for local tool installs, or `"command": "nuget-package-server"` for global installs.

## Tools

### load_project

Load a .NET project's NuGet package information. Call this first before using other tools.

```mcp
load_project(projectPath: "path/to/MyProject.csproj")
```

The server parses `project.assets.json` to discover all packages. Run `dotnet restore` first if the project hasn't been restored. Multiple projects can be loaded simultaneously.

### unload_project

Free memory by unloading a previously loaded project.

### list_project_packages

List all NuGet packages (direct and transitive) referenced by the loaded project.

### search_types

Search for types across all loaded package assemblies. Supports optional filters:

- `query` -- case-insensitive substring match against full type name
- `packageName` -- filter to a specific NuGet package
- `namespace` -- prefix match on namespace
- `typeKind` -- filter by `class`, `interface`, `struct`, `enum`, or `delegate`

Returns up to 50 matches.

### get_type_definition

Get the full API surface of a type: constructors, methods, properties, events, and fields. Includes inline XML doc summaries where available.

### get_xml_documentation

Get detailed XML documentation for a specific member using the standard member ID format (e.g. `T:Namespace.Type`, `M:Namespace.Type.Method`).

### refresh_project_context

Reload a project's context after dependencies have changed.

## Building from source

```bash
git clone https://github.com/roboz0r/nuget-package-server.git
cd nuget-package-server
dotnet build
```

### Running tests

```bash
dotnet run --project test/NugetPackageServer.Tests        # unit tests
dotnet run --project test/NugetPackageServer.MCP.Tests    # e2e tests
```

## License

MIT
