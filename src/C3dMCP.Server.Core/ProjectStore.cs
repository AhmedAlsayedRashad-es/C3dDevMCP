using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace C3dMCP.Server.Core;

/// <summary>The project as declared by its c3d.json (next to the payload csproj).</summary>
public sealed class ProjectContext
{
    public string Name { get; set; } = "";
    public string Config { get; set; } = "R2024";
    public string AddinName { get; set; } = "";
    public string Dir { get; set; } = "";          // folder holding c3d.json
    public string Csproj { get; set; } = "";
    public string Version => Config.StartsWith("R") ? Config[1..] : Config;
    public string StoreDir => Paths.Project(Name);
    public string TasksDir => Path.Combine(StoreDir, "tasks");
    public string CurrentFile => Path.Combine(StoreDir, "current.json");
    public string CycleFile => Path.Combine(StoreDir, "cycle.json");
    public string PipelineFile => Path.Combine(Dir, ".c3d", "pipeline.json");
}

/// <summary>Resolves the project from a directory (walk up to c3d.json) and owns the on-disk
/// task/run store under %LOCALAPPDATA%\First Option\C3dMCP\projects\&lt;name&gt;.</summary>
public static class ProjectStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static ProjectContext Resolve(string? dir)
    {
        var start = string.IsNullOrWhiteSpace(dir) ? Environment.CurrentDirectory : Path.GetFullPath(dir);
        var probe = new DirectoryInfo(start);
        while (probe != null)
        {
            var file = Path.Combine(probe.FullName, "c3d.json");
            if (File.Exists(file)) return Load(file);
            probe = probe.Parent;
        }
        throw new ToolError("no-project", "no c3d.json found in " + start + " or its parents; run c3d_project_init on the payload csproj, or pass projectDir");
    }

    public static ProjectContext Load(string c3dJson)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(c3dJson))!;
        var node = JsonNode.Parse(File.ReadAllText(c3dJson)) as JsonObject ?? throw new ToolError("bad-project", c3dJson + " is not a JSON object");
        var ctx = new ProjectContext
        {
            Dir = dir,
            Config = node["config"]?.GetValue<string>() ?? "R2024",
            Csproj = node["csproj"]?.GetValue<string>() ?? "",
        };
        if (!string.IsNullOrEmpty(ctx.Csproj) && !Path.IsPathRooted(ctx.Csproj)) ctx.Csproj = Path.GetFullPath(Path.Combine(dir, ctx.Csproj));
        if (string.IsNullOrEmpty(ctx.Csproj))
        {
            var all = Directory.GetFiles(dir, "*.csproj");
            if (all.Length == 1) ctx.Csproj = all[0];
            else throw new ToolError("bad-project", "c3d.json must name the csproj (\"csproj\": \"X.csproj\") when the folder holds " + all.Length + " project files");
        }
        ctx.AddinName = node["addinName"]?.GetValue<string>() ?? RootNamespaceOf(ctx.Csproj) ?? Path.GetFileNameWithoutExtension(ctx.Csproj);
        ctx.Name = node["project"]?.GetValue<string>() ?? ctx.AddinName;
        Directory.CreateDirectory(ctx.TasksDir);
        return ctx;
    }

    public static string? RootNamespaceOf(string csproj)
    {
        try
        {
            var m = Regex.Match(File.ReadAllText(csproj), "<RootNamespace>([^<]+)</RootNamespace>");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
        catch { return null; }
    }

    // ---- tasks --------------------------------------------------------------------------------

    public sealed class TaskRecord
    {
        public string TaskId { get; set; } = "";
        public string Title { get; set; } = "";
        public List<string> Expectations { get; set; } = new();
        public string? Branch { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? LatestRun { get; set; }
        public string? LatestVerdict { get; set; }
    }

    public static TaskRecord NewTask(ProjectContext ctx, string title, IEnumerable<string> expectations, string? branch)
    {
        Directory.CreateDirectory(ctx.TasksDir);
        int next = Directory.GetDirectories(ctx.TasksDir).Select(d => { var m = Regex.Match(Path.GetFileName(d), @"^(\d{4})"); return m.Success ? int.Parse(m.Groups[1].Value) : 0; }).DefaultIfEmpty(0).Max() + 1;
        var slug = Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        var id = next.ToString("D4") + (slug.Length > 0 ? "-" + slug : "");
        var rec = new TaskRecord { TaskId = id, Title = title, Expectations = expectations.ToList(), Branch = branch, CreatedAt = DateTime.UtcNow };
        var dir = Path.Combine(ctx.TasksDir, id);
        Directory.CreateDirectory(Path.Combine(dir, "runs"));
        File.WriteAllText(Path.Combine(dir, "task.json"), JsonSerializer.Serialize(rec, Json));
        SetActiveTask(ctx, id);
        return rec;
    }

    public static void SetActiveTask(ProjectContext ctx, string taskId)
    {
        File.WriteAllText(ctx.CurrentFile, JsonSerializer.Serialize(new { activeTask = taskId, updatedAt = DateTime.UtcNow }, Json));
    }

    public static string? ActiveTask(ProjectContext ctx)
    {
        try
        {
            if (!File.Exists(ctx.CurrentFile)) return null;
            var id = JsonNode.Parse(File.ReadAllText(ctx.CurrentFile))?["activeTask"]?.GetValue<string>();
            return id != null && Directory.Exists(Path.Combine(ctx.TasksDir, id)) ? id : null;
        }
        catch { return null; }
    }

    public static TaskRecord? LoadTask(ProjectContext ctx, string taskId)
    {
        var f = Path.Combine(ctx.TasksDir, taskId, "task.json");
        if (!File.Exists(f)) return null;
        try { return JsonSerializer.Deserialize<TaskRecord>(File.ReadAllText(f), Json); } catch { return null; }
    }

    public static void SaveTask(ProjectContext ctx, TaskRecord rec) =>
        File.WriteAllText(Path.Combine(ctx.TasksDir, rec.TaskId, "task.json"), JsonSerializer.Serialize(rec, Json));

    public static List<TaskRecord> ListTasks(ProjectContext ctx, int limit)
    {
        if (!Directory.Exists(ctx.TasksDir)) return new();
        return Directory.GetDirectories(ctx.TasksDir).OrderByDescending(d => d).Take(limit)
            .Select(d => LoadTask(ctx, Path.GetFileName(d))).Where(t => t != null).Select(t => t!).ToList();
    }

    public static string TaskDir(ProjectContext ctx, string taskId) => Path.Combine(ctx.TasksDir, taskId);
    public static string RunsDir(ProjectContext ctx, string taskId) => Path.Combine(TaskDir(ctx, taskId), "runs");

    /// <summary>Next run number, unique across every task of the project (the host refuses a
    /// reused id, and c3d_log_query finds a run by id alone): max existing + 1, four digits.</summary>
    public static string AllocateRun(ProjectContext ctx, string taskId)
    {
        var runs = RunsDir(ctx, taskId);
        Directory.CreateDirectory(runs);
        int next = Directory.GetDirectories(ctx.TasksDir)
            .SelectMany(t => Directory.Exists(Path.Combine(t, "runs")) ? Directory.GetDirectories(Path.Combine(t, "runs")) : Array.Empty<string>())
            .Select(d => int.TryParse(Path.GetFileName(d), out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
        var id = next.ToString("D4");
        Directory.CreateDirectory(Path.Combine(runs, id));
        return id;
    }

    public static string RunDir(ProjectContext ctx, string taskId, string runId) => Path.Combine(RunsDir(ctx, taskId), runId);

    /// <summary>Find a run folder by id across tasks (newest task first) when the task is not given.</summary>
    public static string? FindRunDir(ProjectContext ctx, string? taskId, string runId)
    {
        if (taskId != null) { var d = RunDir(ctx, taskId, runId); return Directory.Exists(d) ? d : null; }
        if (!Directory.Exists(ctx.TasksDir)) return null;
        foreach (var t in Directory.GetDirectories(ctx.TasksDir).OrderByDescending(d => d))
        {
            var d = Path.Combine(t, "runs", runId);
            if (Directory.Exists(d)) return d;
        }
        return null;
    }
}
