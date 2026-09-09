using System.Diagnostics;
using C3dMCP.Server.Core;

namespace C3dMCP.Server;

/// <summary>`c3dmcp doctor`: environment checks, one line each, exit 1 when a required one fails.</summary>
public static class Doctor
{
    public static int Run()
    {
        var checks = Checks();
        foreach (var (name, ok, detail, required) in checks)
            Console.WriteLine($"{(ok ? "OK  " : required ? "FAIL" : "WARN")} {name,-28} {detail}");
        return checks.Any(c => !c.ok && c.required) ? 1 : 0;
    }

    public static List<(string name, bool ok, string detail, bool required)> Checks()
    {
        var list = new List<(string, bool, string, bool)>();
        // dotnet SDK
        try
        {
            var psi = new ProcessStartInfo("dotnet", "--list-sdks") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!; var sdks = p.StandardOutput.ReadToEnd(); p.WaitForExit(10000);
            bool has8 = sdks.Contains("8.");
            list.Add(("dotnet SDK 8.x", has8, sdks.Trim().Replace("\n", "; "), true));
        }
        catch (Exception ex) { list.Add(("dotnet SDK 8.x", false, ex.Message, true)); }
        // bundle per year
        var bundle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "ApplicationPlugins", "C3dMCP.bundle");
        list.Add(("bundle manifest", File.Exists(Path.Combine(bundle, "PackageContents.xml")), bundle, true));
        foreach (var year in new[] { "2024", "2025", "2026" })
        {
            var dll = Path.Combine(bundle, "Contents", year, "C3dMCP.Host.dll");
            var acad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk", "AutoCAD " + year, "acad.exe");
            bool installed = File.Exists(acad);
            list.Add(("host " + year, File.Exists(dll) || !installed, File.Exists(dll) ? dll : installed ? "missing (Civil3D " + year + " is installed)" : "n/a (no Civil3D " + year + ")", false));
        }
        // data folders
        list.Add(("data folder", Directory.Exists(Paths.Root) || TryCreate(Paths.Root), Paths.Root, true));
        // instances
        var inst = InstanceDiscovery.List(probe: true);
        list.Add(("instances", true, inst.Count == 0 ? "none running" : string.Join("; ", inst.Select(i => $"{i.Port} pid {i.Pid} C3D {i.Version} {(i.Alive ? "alive" : "NOT answering: " + i.Why)}")), false));
        // env + hooks (Phase 6 fills these)
        list.Add(("env synced", Directory.Exists(Path.Combine(Paths.Env, ".claude")), Paths.Env, false));
        var mcpJson = Path.Combine(Environment.CurrentDirectory, ".mcp.json");
        list.Add((".mcp.json in cwd", File.Exists(mcpJson) && File.ReadAllText(mcpJson).Contains("c3dmcp"), mcpJson, false));
        var settings = Path.Combine(Environment.CurrentDirectory, ".claude", "settings.json");
        list.Add(("hooks in cwd", File.Exists(settings) && File.ReadAllText(settings).Contains("c3dmcp hook"), settings, false));
        try { var ctx = ProjectStore.Resolve(null); list.Add(("project", true, ctx.Name + " (" + ctx.Config + ") " + ctx.Csproj, false)); }
        catch (Exception ex) { list.Add(("project", false, ex.Message, false)); }
        return list;
    }

    private static bool TryCreate(string dir) { try { Directory.CreateDirectory(dir); return true; } catch { return false; } }
}
