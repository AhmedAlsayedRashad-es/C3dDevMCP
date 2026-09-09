using System.Diagnostics;
using System.Text.RegularExpressions;

namespace C3dMCP.Server.Core;

/// <summary>Runs `dotnet build -c &lt;config&gt;` on the payload csproj and reads the deployed DLL path
/// from the C3DMCP_PAYLOAD line the Directory.Build.props target prints.</summary>
public static class PayloadBuilder
{
    public sealed class Result
    {
        public bool Ok { get; set; }
        public string? DeployedDll { get; set; }
        public int Rev { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Tail { get; set; } = new();
        public double Seconds { get; set; }
        public int ExitCode { get; set; }
    }

    public static Result Build(string csproj, string config, int timeoutSec = 600)
    {
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo("dotnet", $"build \"{csproj}\" -c {config} -nologo -v:m")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(csproj)!,
        };
        var lines = new List<string>();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (lines) lines.Add(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (lines) lines.Add(e.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutSec * 1000)) { try { p.Kill(true); } catch { } lines.Add("build timed out after " + timeoutSec + " s"); }
        p.WaitForExit();
        var r = new Result { ExitCode = p.ExitCode, Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1) };
        var errRx = new Regex(@"error \w+\d+:", RegexOptions.IgnoreCase);
        r.Errors = lines.Where(l => errRx.IsMatch(l)).Select(l => l.Trim()).Distinct().Take(20).ToList();
        r.Tail = lines.TakeLast(8).Select(l => l.Trim()).ToList();
        var m = lines.Select(l => Regex.Match(l, @"C3DMCP_PAYLOAD r(\d+) (.+\.dll)\s*$")).LastOrDefault(x => x.Success);
        if (m != null) { r.Rev = int.Parse(m.Groups[1].Value); r.DeployedDll = m.Groups[2].Value.Trim(); }
        r.Ok = p.ExitCode == 0 && r.Errors.Count == 0 && r.DeployedDll != null && File.Exists(r.DeployedDll);
        if (p.ExitCode == 0 && r.DeployedDll == null)
            r.Errors.Add("build succeeded but printed no C3DMCP_PAYLOAD line: the project is missing the versioned-identity Directory.Build.props (run c3d_project_init)");
        return r;
    }
}
