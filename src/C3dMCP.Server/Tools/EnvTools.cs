using System.ComponentModel;
using C3dMCP.Server.Core;
using ModelContextProtocol.Server;

namespace C3dMCP.Server.Tools;

[McpServerToolType]
public sealed class EnvTools
{
    [McpServerTool(Name = "c3d_env_sync"), Description("Pull the Claude Code environment (c3d-* agents, the c3d-develop skill, hooks, templates) from the public C3dDevMCP repo and link it into the project: copies agents/skills into .claude, merges hooks into .claude/settings.json with the absolute c3dmcp path, merges .mcp.json, seeds BACKLOG.md, pipeline.json and the pitfall ledger when absent.")]
    public static string EnvSyncTool(
        [Description("Git ref to sync (default main)")] string? gitRef = null,
        [Description("Project folder to link into (default: current directory)")] string? projectDir = null,
        [Description("Use a local checkout of the repo instead of GitHub (path to the repo root)")] string? localRepo = null)
    {
        try
        {
            var dir = projectDir ?? Environment.CurrentDirectory;
            string? addin = null;
            try { addin = ProjectStore.Resolve(dir).AddinName; } catch { }
            var (env, commit, source) = EnvSync.Fetch(gitRef, localRepo);
            var r = EnvSync.Link(env, dir, addin);
            return Compact.Render(new { ok = true, commit, source, linked = r.Linked, seeded = r.Seeded, warnings = r.Warnings, hint = "restart the Claude Code session so the new hooks, agents and MCP registration load" });
        }
        catch (ToolError e) { return Compact.Error(e); }
        catch (Exception e) { return Compact.Error(e); }
    }

    [McpServerTool(Name = "c3d_doctor"), Description("Environment checks: dotnet SDK, bundle per Civil3D year, data folder, live instances, env synced, .mcp.json and hooks in the current folder, project resolution.")]
    public static string DoctorTool()
    {
        try { return Compact.Render(new { ok = Doctor.Checks().All(c => c.ok || !c.required), checks = Doctor.Checks().Select(c => new { c.name, c.ok, c.detail, c.required }) }); }
        catch (Exception e) { return Compact.Error(e); }
    }
}
