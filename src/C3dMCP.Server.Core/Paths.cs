namespace C3dMCP.Server.Core;

/// <summary>Every file C3dMCP writes lives under %LOCALAPPDATA%\First Option\C3dMCP.</summary>
public static class Paths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "First Option", "C3dMCP");
    public static string Instances => Path.Combine(Root, "instances");
    public static string Projects => Path.Combine(Root, "projects");
    public static string Payloads => Path.Combine(Root, "payloads");
    public static string Env => Path.Combine(Root, "env");
    public static string Bin => Path.Combine(Root, "bin");
    public static string Screenshots => Path.Combine(Root, "screenshots");

    public static string Project(string name) => Path.Combine(Projects, name);
}

/// <summary>A tool-level failure with a machine code the agent can branch on.</summary>
public sealed class ToolError : Exception
{
    public string Code { get; }
    public string? Stage { get; }
    public object? Extra { get; }
    public ToolError(string code, string message, string? stage = null, object? extra = null) : base(message) { Code = code; Stage = stage; Extra = extra; }
}
