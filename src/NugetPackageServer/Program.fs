namespace NugetPackageServer

open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open ModelContextProtocol

module Program =

    [<EntryPoint>]
    let main _argv =
        let builder = Host.CreateApplicationBuilder()

        builder.Logging.ClearProviders() |> ignore

        builder.Services.AddSingleton<ProjectContextManager>() |> ignore

        builder.Services
            .AddMcpServer(fun options ->
                options.ServerInfo <- Protocol.Implementation(Name = "nuget-package-server", Version = "0.1.0")
            )
            .WithStdioServerTransport()
            .WithTools<NugetTools>()
        |> ignore

        let host = builder.Build()
        host.Run()
        0
