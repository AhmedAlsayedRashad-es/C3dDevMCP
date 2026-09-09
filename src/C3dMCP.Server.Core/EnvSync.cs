using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace C3dMCP.Server.Core;

/// <summary>Clones or pulls the public environment repo and links its env/ folder into a project:
/// agents and the skill are copied into &lt;project&gt;/.claude, hooks are merged into settings.json
/// with the absolute c3dmcp.exe path, .mcp.json is merged, templates are seeded when absent.</summary>
public static class EnvSync
{
    public const string RepoUrl = "https://github.com/AhmedAlsayedRashad-es/C3dDevMCP.git";
    public static string RepoDir => Path.Combine(Paths.Root, "env-repo");
    public static string ExePath => Path.Combine(Paths.Bin, "c3dmcp.exe");

    public sealed class Result
    {
        public string Commit { get; set; } = "";
        public string Source { get; set; } = "";
        public List<string> Linked { get; set; } = new();
        public List<string> Seeded { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    /// <summary>Fetch the repo (or use a local checkout when given). Returns the env folder.</summary>
    public static (string envDir, string commit, string source) Fetch(string? gitRef, string? localRepo)
    {
        if (!string.IsNullOrEmpty(localRepo))
        {
            var envLocal = Path.Combine(localRepo, "env");
            if (!Directory.Exists(envLocal)) throw new ToolError("no-env", localRepo + " has no env/ folder");
            return (envLocal, Git(localRepo, "rev-parse --short HEAD").Trim(), localRepo);
        }
        Directory.CreateDirectory(Paths.Root);
        if (!Directory.Exists(Path.Combine(RepoDir, ".git")))
        {
            if (Directory.Exists(RepoDir)) Directory.Delete(RepoDir, true);
            Git(Paths.Root, $"clone --depth 1 --branch {gitRef ?? "main"} {RepoUrl} \"{RepoDir}\"");
        }
        else
        {
            Git(RepoDir, "fetch --depth 1 origin " + (gitRef ?? "main"));
            Git(RepoDir, "checkout -q --detach FETCH_HEAD");
        }
        var env = Path.Combine(RepoDir, "env");
        if (!Directory.Exists(env)) throw new ToolError("no-env", "the repo has no env/ folder at " + gitRef);
        return (env, Git(RepoDir, "rev-parse --short HEAD").Trim(), RepoUrl + "@" + (gitRef ?? "main"));
    }

    public static Result Link(string envDir, string projectDir, string? addinName)
    {
        var r = new Result();
        var claude = Path.Combine(projectDir, ".claude");
        Directory.CreateDirectory(claude);

        // agents + skills: copy (overwrite), so the repo is the source of truth
        foreach (var sub in new[] { "agents", "skills" })
        {
            var src = Path.Combine(envDir, ".claude", sub);
            if (!Directory.Exists(src)) continue;
            var dst = Path.Combine(claude, sub);
            CopyTree(src, dst);
            r.Linked.Add(dst);
        }

        // settings.json: merge hooks (replace our matchers, keep others) and permissions.allow (union)
        var settingsPath = Path.Combine(claude, "settings.json");
        var ours = JsonNode.Parse(File.ReadAllText(Path.Combine(envDir, ".claude", "settings.json")))!.AsObject();
        var exe = File.Exists(ExePath) ? ExePath : Environment.ProcessPath ?? "c3dmcp";
        RewriteHookCommands(ours, exe);
        var theirs = File.Exists(settingsPath) ? (JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? new JsonObject()) : new JsonObject();
        MergeHooks(theirs, ours);
        MergeAllow(theirs, ours);
        File.WriteAllText(settingsPath, theirs.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        r.Linked.Add(settingsPath);

        // .mcp.json: set the c3dmcp server entry with the absolute exe path
        var mcpPath = Path.Combine(projectDir, ".mcp.json");
        var mcp = File.Exists(mcpPath) ? (JsonNode.Parse(File.ReadAllText(mcpPath)) as JsonObject ?? new JsonObject()) : new JsonObject();
        var servers = mcp["mcpServers"] as JsonObject ?? new JsonObject();
        servers["c3dmcp"] = new JsonObject { ["type"] = "stdio", ["command"] = exe, ["args"] = new JsonArray("serve") };
        mcp["mcpServers"] = servers;
        File.WriteAllText(mcpPath, mcp.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        r.Linked.Add(mcpPath);

        // templates: seed when absent
        var tpl = Path.Combine(envDir, "templates");
        var backlog = Path.Combine(projectDir, "BACKLOG.md");
        if (!File.Exists(backlog) && File.Exists(Path.Combine(tpl, "BACKLOG.md"))) { File.Copy(Path.Combine(tpl, "BACKLOG.md"), backlog); r.Seeded.Add(backlog); }
        var pipeline = Path.Combine(projectDir, ".c3d", "pipeline.json");
        if (!File.Exists(pipeline) && File.Exists(Path.Combine(envDir, "pipeline.default.json")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pipeline)!);
            File.Copy(Path.Combine(envDir, "pipeline.default.json"), pipeline);
            r.Seeded.Add(pipeline);
        }
        if (!string.IsNullOrEmpty(addinName))
        {
            var ledger = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "First Option", "Civil3D Add-ins", addinName, "c3d-pitfalls.md");
            if (!File.Exists(ledger) && File.Exists(Path.Combine(tpl, "c3d-pitfalls.md")))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ledger)!);
                File.WriteAllText(ledger, File.ReadAllText(Path.Combine(tpl, "c3d-pitfalls.md")).Replace("<AddinName>", addinName));
                r.Seeded.Add(ledger);
            }
        }
        // keep a copy of the whole env beside the data for reference
        try { CopyTree(envDir, Paths.Env); } catch (Exception ex) { r.Warnings.Add("env copy: " + ex.Message); }
        return r;
    }

    private static void RewriteHookCommands(JsonObject settings, string exe)
    {
        var hooks = settings["hooks"] as JsonObject; if (hooks == null) return;
        foreach (var kv in hooks)
            foreach (var entry in kv.Value as JsonArray ?? new JsonArray())
                foreach (var h in entry?["hooks"] as JsonArray ?? new JsonArray())
                {
                    var cmd = h?["command"]?.GetValue<string>();
                    if (cmd != null && cmd.StartsWith("c3dmcp ")) h!["command"] = "\"" + exe + "\"" + cmd["c3dmcp".Length..];
                }
    }

    private static void MergeHooks(JsonObject theirs, JsonObject ours)
    {
        var th = theirs["hooks"] as JsonObject ?? new JsonObject();
        var oh = ours["hooks"] as JsonObject ?? new JsonObject();
        foreach (var kv in oh)
        {
            var existing = th[kv.Key] as JsonArray ?? new JsonArray();
            // drop entries that already run c3dmcp, then append ours
            var kept = new JsonArray();
            foreach (var e in existing)
            {
                var text = e?.ToJsonString() ?? "";
                if (!text.Contains("c3dmcp")) kept.Add(e?.DeepClone());
            }
            foreach (var e in kv.Value as JsonArray ?? new JsonArray()) kept.Add(e?.DeepClone());
            th[kv.Key] = kept;
        }
        theirs["hooks"] = th;
    }

    private static void MergeAllow(JsonObject theirs, JsonObject ours)
    {
        var tp = theirs["permissions"] as JsonObject ?? new JsonObject();
        var allow = tp["allow"] as JsonArray ?? new JsonArray();
        var have = new HashSet<string>(allow.Select(a => a?.GetValue<string>() ?? ""));
        foreach (var a in ours["permissions"]?["allow"] as JsonArray ?? new JsonArray())
        {
            var s = a?.GetValue<string>() ?? "";
            if (have.Add(s)) allow.Add(s);
        }
        tp["allow"] = allow;
        theirs["permissions"] = tp;
    }

    public static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src))
        {
            var name = Path.GetFileName(d);
            if (name == ".git") continue;
            CopyTree(d, Path.Combine(dst, name));
        }
    }

    public static string Git(string cwd, string args)
    {
        var psi = new ProcessStartInfo("git", args) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd };
        using var p = Process.Start(psi) ?? throw new ToolError("no-git", "git is not installed or not on PATH");
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEnd(); var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(120000);
        if (p.ExitCode != 0) throw new ToolError("git-failed", "git " + args + ": " + (stderr.Trim().Length > 0 ? stderr.Trim() : stdout.Trim()));
        return stdout;
    }
}
