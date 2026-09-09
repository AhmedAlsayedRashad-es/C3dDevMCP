using C3dMCP.Server.Core;
using Microsoft.Win32;

namespace C3dMCP.Server;

/// <summary>`c3dmcp install`: copy this build into %LOCALAPPDATA%\First Option\C3dMCP\bin (when
/// run from elsewhere), put that folder on the user's PATH, create the data folders, and run
/// doctor. `c3dmcp env sync [--ref main] [--local &lt;repo&gt;] [--project &lt;dir&gt;]` links the environment.</summary>
public static class Install
{
    public static int Run(string[] args)
    {
        Directory.CreateDirectory(Paths.Root);
        Directory.CreateDirectory(Paths.Instances);
        Directory.CreateDirectory(Paths.Projects);
        var here = Path.GetDirectoryName(Environment.ProcessPath!)!;
        if (!string.Equals(Path.GetFullPath(here).TrimEnd('\\'), Path.GetFullPath(Paths.Bin).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Paths.Bin);
            foreach (var f in Directory.GetFiles(here))
            {
                var dst = Path.Combine(Paths.Bin, Path.GetFileName(f));
                try { File.Copy(f, dst, true); }
                catch (IOException) { Console.WriteLine("skip (in use): " + Path.GetFileName(f)); }
            }
            Console.WriteLine("copied server to " + Paths.Bin);
        }
        AddToUserPath(Paths.Bin);
        Console.WriteLine("c3dmcp.exe: " + EnvSync.ExePath);
        Console.WriteLine("Next: in your payload project folder run  c3dmcp env sync  (or the c3d_env_sync tool), then open Claude Code there.");
        return Doctor.Run();
    }

    public static int EnvVerb(string[] args)
    {
        if (args.Length == 0 || args[0] != "sync") { Console.Error.WriteLine("usage: c3dmcp env sync [--ref main] [--local <repo>] [--project <dir>]"); return 2; }
        string? gitRef = null, local = null, project = null;
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--ref") gitRef = args[++i];
            else if (args[i] == "--local") local = args[++i];
            else if (args[i] == "--project") project = args[++i];
        }
        try
        {
            var projectDir = project ?? Environment.CurrentDirectory;
            string? addin = null;
            try { addin = ProjectStore.Resolve(projectDir).AddinName; } catch { }
            var (env, commit, source) = EnvSync.Fetch(gitRef, local);
            var r = EnvSync.Link(env, projectDir, addin);
            Console.WriteLine($"env {commit} from {source}");
            foreach (var l in r.Linked) Console.WriteLine("linked  " + l);
            foreach (var s in r.Seeded) Console.WriteLine("seeded  " + s);
            foreach (var w in r.Warnings) Console.WriteLine("warn    " + w);
            return 0;
        }
        catch (ToolError e) { Console.Error.WriteLine(e.Code + ": " + e.Message); return 1; }
    }

    private static void AddToUserPath(string dir)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true)!;
            var path = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
            var parts = path.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Any(p => string.Equals(Environment.ExpandEnvironmentVariables(p).TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return;
            parts.Add(dir);
            key.SetValue("Path", string.Join(";", parts), RegistryValueKind.ExpandString);
            Console.WriteLine("added to user PATH: " + dir + " (new terminals see it)");
        }
        catch (Exception ex) { Console.WriteLine("PATH not updated: " + ex.Message); }
    }
}
