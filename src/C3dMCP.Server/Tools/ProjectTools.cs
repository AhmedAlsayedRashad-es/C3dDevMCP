using System.ComponentModel;
using System.Text.RegularExpressions;
using C3dMCP.Server.Core;
using ModelContextProtocol.Server;

namespace C3dMCP.Server.Tools;

[McpServerToolType]
public sealed class ProjectTools
{
    [McpServerTool(Name = "c3d_project_init"), Description("Instrument a payload csproj for C3dMCP: write c3d.json, seed payload.rev above every revision already deployed, write the versioned-identity Directory.Build.props, create the project store and a default pipeline.json. Reports the manual csproj steps that remain (AssemblyName stamp, bridge references).")]
    public static string ProjectInit(
        [Description("Path to the payload .csproj")] string csproj,
        [Description("Build configuration: R2024, R2025 or R2026 (default R2024)")] string config = "R2024",
        [Description("Project name for the store; defaults to the csproj RootNamespace")] string? project = null)
    {
        try
        {
            csproj = Path.GetFullPath(csproj);
            if (!File.Exists(csproj)) throw new ToolError("no-csproj", csproj + " does not exist");
            var dir = Path.GetDirectoryName(csproj)!;
            var root = ProjectStore.RootNamespaceOf(csproj) ?? Path.GetFileNameWithoutExtension(csproj);
            project ??= root;
            var version = config.TrimStart('R');

            // payload.rev above the deployed high-water mark
            int high = 0;
            var deployed = Path.Combine(Paths.Payloads, root, version);
            if (Directory.Exists(deployed))
                high = Directory.GetDirectories(deployed).Select(d => int.TryParse(Path.GetFileName(d), out var n) ? n : 0).DefaultIfEmpty(0).Max();
            var revFile = Path.Combine(dir, "payload.rev");
            int current = File.Exists(revFile) && int.TryParse(File.ReadAllText(revFile).Trim(), out var cur) ? cur : 0;
            int seed = Math.Max(current, high + 1);
            File.WriteAllText(revFile, seed.ToString());

            File.WriteAllText(Path.Combine(dir, "c3d.json"), $"{{ \"project\": \"{project}\", \"config\": \"{config}\", \"addinName\": \"{root}\", \"csproj\": \"{Path.GetFileName(csproj)}\" }}\n");

            var props = Path.Combine(dir, "Directory.Build.props");
            bool propsWritten = false;
            if (!File.Exists(props) || !File.ReadAllText(props).Contains("StampVersionedPayload"))
            {
                File.WriteAllText(props, Templates.DirectoryBuildProps);
                propsWritten = true;
            }

            var ctx = ProjectStore.Load(Path.Combine(dir, "c3d.json"));
            Directory.CreateDirectory(ctx.TasksDir);
            var pipelineDir = Path.GetDirectoryName(ctx.PipelineFile)!;
            Directory.CreateDirectory(pipelineDir);
            bool pipelineWritten = false;
            if (!File.Exists(ctx.PipelineFile)) { File.WriteAllText(ctx.PipelineFile, Templates.PipelineJson); pipelineWritten = true; }

            var text = File.ReadAllText(csproj);
            var todo = new List<string>();
            if (!Regex.IsMatch(text, @"<AssemblyName>\s*\$\(RootNamespace\)_r\$\(PayloadRev\)\s*</AssemblyName>"))
                todo.Add("set <AssemblyName>$(RootNamespace)_r$(PayloadRev)</AssemblyName> in the csproj");
            if (!text.Contains("C3dMCP.Sdk")) todo.Add("reference C3dMCP.Sdk and C3dMCP.Engine with Private=\"false\" (ProjectReference or the bundle DLLs under %APPDATA%\\Autodesk\\ApplicationPlugins\\C3dMCP.bundle\\Contents\\<year>)");
            if (!Regex.IsMatch(text, @"<Civil3dVersion>")) todo.Add("define <Civil3dVersion>" + version + "</Civil3dVersion> in the " + config + " configuration");
            todo.Add("make " + root + ".App (IExtensionApplication), " + root + ".Command (C3dMCP.Sdk.IC3dCommand) and " + root + ".PatchService (C3dMCP.Sdk.IC3dPatchService) public types");

            return Compact.Render(new { ok = true, project, config, addinName = root, payloadRev = seed, deployedHighWater = high, c3dJson = Path.Combine(dir, "c3d.json"), propsWritten, pipelineWritten, pipeline = ctx.PipelineFile, store = ctx.StoreDir, remainingSteps = todo });
        }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }
}
