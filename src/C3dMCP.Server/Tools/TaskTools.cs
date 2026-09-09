using System.ComponentModel;
using C3dMCP.Server.Core;
using ModelContextProtocol.Server;

namespace C3dMCP.Server.Tools;

[McpServerToolType]
public sealed class TaskTools
{
    [McpServerTool(Name = "c3d_task_new"), Description("Create a task with its expectations and make it the active task. Runs need an active task. Expectations are the checks the analyzer judges the run against.")]
    public static string TaskNew(
        [Description("Short title, e.g. 'valve at bend'")] string title,
        [Description("Expectations, one per entry, each a verifiable statement.")] string[] expectations,
        [Description("Project folder (holds c3d.json). Defaults to the current directory.")] string? projectDir = null,
        [Description("Git branch the task is on, for the record.")] string? branch = null)
    {
        try
        {
            var ctx = ProjectStore.Resolve(projectDir);
            var t = ProjectStore.NewTask(ctx, title, expectations ?? Array.Empty<string>(), branch);
            CycleState.OnNewTask(ctx, t.TaskId);
            return Compact.Render(new { ok = true, taskId = t.TaskId, expectations = t.Expectations.Count, dir = ProjectStore.TaskDir(ctx, t.TaskId) });
        }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }

    [McpServerTool(Name = "c3d_task_list"), Description("List recent tasks of the project with their latest run and verdict.")]
    public static string TaskList(
        [Description("Project folder (holds c3d.json). Defaults to the current directory.")] string? projectDir = null,
        [Description("How many, newest first (default 10).")] int limit = 10)
    {
        try
        {
            var ctx = ProjectStore.Resolve(projectDir);
            var active = ProjectStore.ActiveTask(ctx);
            return Compact.Render(new
            {
                ok = true, project = ctx.Name, activeTask = active,
                tasks = ProjectStore.ListTasks(ctx, limit).Select(t => new { t.TaskId, t.Title, expectations = t.Expectations.Count, t.LatestRun, t.LatestVerdict }),
            });
        }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }

    [McpServerTool(Name = "c3d_task_activate"), Description("Make an existing task the active one.")]
    public static string TaskActivate([Description("Task id, e.g. 0003-valve-at-bend")] string taskId, string? projectDir = null)
    {
        try
        {
            var ctx = ProjectStore.Resolve(projectDir);
            if (ProjectStore.LoadTask(ctx, taskId) == null) throw new ToolError("no-task", "task " + taskId + " does not exist");
            ProjectStore.SetActiveTask(ctx, taskId);
            CycleState.OnNewTask(ctx, taskId);
            return Compact.Render(new { ok = true, activeTask = taskId });
        }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }
}
