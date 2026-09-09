using C3dMCP.Server;
using C3dMCP.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// c3dmcp — verbs:
//   (none) | serve      the MCP server over stdio (what Claude Code launches)
//   hook <event>        Claude Code hook: reads the hook JSON on stdin, prints a decision (Phase 5)
//   doctor              environment checks
//   install             register the server in Claude Code (Phase 6)
//   env sync [ref]      sync the Claude Code environment from the public repo (Phase 6)
var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "serve";
switch (verb)
{
    case "serve":
        return await Serve();
    case "hook":
        return HookCli.Run(args.Skip(1).ToArray());
    case "doctor":
        return Doctor.Run();
    case "install":
        return Install.Run(args.Skip(1).ToArray());
    case "env":
        return Install.EnvVerb(args.Skip(1).ToArray());
    case "version":
    case "--version":
        Console.WriteLine("c3dmcp " + typeof(Program).Assembly.GetName().Version?.ToString(3));
        return 0;
    default:
        Console.Error.WriteLine("unknown verb '" + verb + "'; use: serve | hook <event> | install | env sync | doctor | version");
        return 2;
}

static async Task<int> Serve()
{
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);   // stdout is the MCP channel
    builder.Services.AddMcpServer(o =>
        {
            o.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "c3dmcp", Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" };
            o.ServerInstructions = ToolDocs.Instructions;
        })
        .WithStdioServerTransport()
        .WithTools<InstanceTools>()
        .WithTools<TaskTools>()
        .WithTools<RunTools>()
        .WithTools<ProjectTools>()
        .WithTools<CycleTools>()
        .WithTools<EnvTools>();
    await builder.Build().RunAsync();
    return 0;
}

public partial class Program { }
