using System.Diagnostics;
using System.IO.Compression;

// C3dMCP.Setup
//   (no args) | install   install from the release layout beside this exe:
//                           bin\c3dmcp.exe ...      -> %LOCALAPPDATA%\First Option\C3dMCP\bin
//                           bundle\PackageContents.xml + Contents\<year>\ -> %APPDATA%\Autodesk\ApplicationPlugins\C3dMCP.bundle
//                         then runs `c3dmcp install` (PATH + doctor).
//   pack <repoRoot> <outDir>   build the release from a checkout: publish the server, build the host
//                         for every Civil3D year whose SDK package restores, copy this exe, zip it.
var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "install";
try
{
    return verb switch
    {
        "install" => InstallFromRelease(),
        "pack" => Pack(args.Length > 1 ? args[1] : ".", args.Length > 2 ? args[2] : "artifacts"),
        _ => Usage(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine("setup failed: " + ex.Message);
    return 1;
}

static int Usage() { Console.Error.WriteLine("usage: C3dMCP.Setup [install] | pack <repoRoot> <outDir>"); return 2; }

static string Local() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "First Option", "C3dMCP");
static string BundleDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "ApplicationPlugins", "C3dMCP.bundle");

static int InstallFromRelease()
{
    var here = Path.GetDirectoryName(Environment.ProcessPath!)!;
    var bin = Path.Combine(here, "bin");
    var bundle = Path.Combine(here, "bundle");
    if (!Directory.Exists(bin) || !File.Exists(Path.Combine(bin, "c3dmcp.exe"))) throw new Exception("no bin\\c3dmcp.exe beside the installer; run it from the unpacked release folder");
    if (!File.Exists(Path.Combine(bundle, "PackageContents.xml"))) throw new Exception("no bundle\\PackageContents.xml beside the installer");

    if (Process.GetProcessesByName("acad").Length > 0)
        Console.WriteLine("warning: Civil3D is running; the bundle files in use are skipped. Close Civil3D and run the installer again to update the host.");

    var dstBin = Path.Combine(Local(), "bin");
    CopyTree(bin, dstBin, skipInUse: true);
    Console.WriteLine("server  -> " + dstBin);
    CopyTree(bundle, BundleDir(), skipInUse: true);
    Console.WriteLine("bundle  -> " + BundleDir());
    foreach (var year in Directory.GetDirectories(Path.Combine(bundle, "Contents")).Select(Path.GetFileName))
        Console.WriteLine("host    " + year);

    var psi = new ProcessStartInfo(Path.Combine(dstBin, "c3dmcp.exe"), "install") { UseShellExecute = false };
    using var p = Process.Start(psi)!;
    p.WaitForExit();
    Console.WriteLine();
    Console.WriteLine("Done. Start Civil3D: the C3dMCP palette shows the port. In your payload project folder run: c3dmcp env sync");
    return p.ExitCode;
}

static int Pack(string repo, string outDir)
{
    repo = Path.GetFullPath(repo);
    outDir = Path.GetFullPath(outDir);
    var stage = Path.Combine(outDir, "stage");
    if (Directory.Exists(stage)) Directory.Delete(stage, true);
    Directory.CreateDirectory(stage);

    var version = ReadVersion(repo);
    Console.WriteLine("packing C3dMCP " + version);

    // 1. server (ReadyToRun, framework-dependent)
    var serverOut = Path.Combine(stage, "bin");
    Run("dotnet", $"publish \"{Path.Combine(repo, "src", "C3dMCP.Server", "C3dMCP.Server.csproj")}\" -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o \"{serverOut}\"", repo);

    // 2. host per year
    var bundleOut = Path.Combine(stage, "bundle");
    Directory.CreateDirectory(Path.Combine(bundleOut, "Contents"));
    File.Copy(Path.Combine(repo, "src", "C3dMCP.Host", "PackageContents.xml"), Path.Combine(bundleOut, "PackageContents.xml"), true);
    foreach (var (config, year) in new[] { ("R2024", "2024"), ("R2025", "2025"), ("R2026", "2026") })
    {
        var yearOut = Path.Combine(bundleOut, "Contents", year);
        try
        {
            Run("dotnet", $"build \"{Path.Combine(repo, "src", "C3dMCP.Host", "C3dMCP.Host.csproj")}\" -c {config} -p:SkipC3DDeploy=true", repo);
            var built = Path.Combine(repo, "src", "C3dMCP.Host", "bin", config);
            var tfm = Directory.GetDirectories(built).OrderByDescending(d => d).First();
            Directory.CreateDirectory(yearOut);
            foreach (var f in Directory.GetFiles(tfm).Where(f => f.EndsWith(".dll") || f.EndsWith(".pdb") || f.EndsWith(".json")))
                File.Copy(f, Path.Combine(yearOut, Path.GetFileName(f)), true);
            Console.WriteLine("host " + year + " ok");
        }
        catch (Exception ex) { Console.WriteLine("host " + year + " skipped: " + ex.Message.Split('\n')[0]); }
    }

    // 3. this installer, the SDK for payload projects, the env folder, docs
    File.Copy(Environment.ProcessPath!, Path.Combine(stage, "C3dMCP.Setup.exe"), true);
    var sdkOut = Path.Combine(stage, "sdk");
    Directory.CreateDirectory(sdkOut);
    foreach (var name in new[] { "C3dMCP.Sdk", "C3dMCP.Engine" })
    {
        var src = Path.Combine(bundleOut, "Contents", "2024", name + ".dll");
        if (File.Exists(src)) File.Copy(src, Path.Combine(sdkOut, name + ".dll"), true);
    }
    CopyTree(Path.Combine(repo, "env"), Path.Combine(stage, "env"), skipInUse: false);
    CopyTree(Path.Combine(repo, "templates"), Path.Combine(stage, "templates"), skipInUse: false);
    File.Copy(Path.Combine(repo, "README.md"), Path.Combine(stage, "README.md"), true);

    var zip = Path.Combine(outDir, $"C3dMCP-{version}-win-x64.zip");
    if (File.Exists(zip)) File.Delete(zip);
    ZipFile.CreateFromDirectory(stage, zip, CompressionLevel.Optimal, false);
    Console.WriteLine("release: " + zip);
    return 0;
}

static string ReadVersion(string repo)
{
    var props = Path.Combine(repo, "Directory.Build.props");
    var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(props), "<Version>([^<]+)</Version>");
    return m.Success ? m.Groups[1].Value.Trim() : "0.0.0";
}

static void Run(string exe, string args, string cwd)
{
    var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0)
    {
        var errs = output.Split('\n').Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(5);
        throw new Exception(exe + " " + args.Split(' ')[0] + " failed: " + string.Join(" | ", errs));
    }
}

static void CopyTree(string src, string dst, bool skipInUse)
{
    Directory.CreateDirectory(dst);
    foreach (var f in Directory.GetFiles(src))
    {
        var d = Path.Combine(dst, Path.GetFileName(f));
        try { File.Copy(f, d, true); }
        catch (IOException) when (skipInUse) { Console.WriteLine("  skip (in use): " + d); }
    }
    foreach (var dir in Directory.GetDirectories(src))
    {
        if (Path.GetFileName(dir) == ".git") continue;
        CopyTree(dir, Path.Combine(dst, Path.GetFileName(dir)), skipInUse);
    }
}
