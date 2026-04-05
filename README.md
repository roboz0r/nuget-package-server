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

### load_package

Load a NuGet package by name into an isolated context for exploration. Useful for evaluating packages before adding them to a project.

```mcp
load_package(name: "Humanizer.Core", version: "2.14.1", workingDirectory: "/path/to/repo")
```

Creates a temporary project under `{workingDirectory}/tmp/`, runs `dotnet restore`, and indexes the package. Respects `nuget.config` and Central Package Management (`Directory.Packages.props`) from the working directory. The `tfm` parameter is optional if a project is already loaded.

### unload_project

Free memory by unloading a previously loaded project or package.

### list_project_packages

List all NuGet packages (direct and transitive) referenced by the loaded project.

### list_namespaces

List all namespaces across loaded packages, grouped by package. Useful for discovering the structure of an unfamiliar package before drilling into types. Supports an optional `packageName` filter.

### search_types

Search for types across all loaded package assemblies. Supports optional filters:

- `query` -- case-insensitive substring match against full type name
- `packageName` -- filter to a specific NuGet package
- `namespace` -- prefix match on namespace
- `typeKind` -- filter by `class`, `abstract class`, `sealed class`, `static class`, `interface`, `struct`, `enum`, or `delegate`

Returns up to 50 matches.

### search_members

Search for methods, properties, events, and fields across all loaded package types. Supports optional filters:

- `query` -- case-insensitive substring match against member name
- `packageName` -- filter to a specific NuGet package
- `typeName` -- substring match on declaring type name
- `memberKind` -- filter by `method`, `property`, `constructor`, `event`, `field`, or `enum value`

Returns up to 50 lightweight matches. Use `get_type_definition` for full signatures.

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
dotnet test
```

### Running from source

```bash
# From the repo root
dotnet pack src/NugetPackageServer/NugetPackageServer.fsproj -o ./artifacts

# In another repo, install as a local tool
dotnet tool install NugetPackageServer --add-source /absolute/path/to/nuget-package-server/artifacts

# Or as a global tool
dotnet tool install -g NugetPackageServer --add-source /absolute/path/to/nuget-package-server/artifacts
```

Then add the MCP config file described above.

## License

MIT
