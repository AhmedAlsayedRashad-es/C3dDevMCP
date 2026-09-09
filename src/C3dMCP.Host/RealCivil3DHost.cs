using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using C3dMCP.Engine.Reload;
using C3dMCP.Sdk;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace C3dMCP.Host
{
    /// <summary>The live Civil3D adapter (lifted from the Patcher's RealCivil3DHost). Supplies the
    /// seams the reload engine and the run manager need: payload loading with the bridge resolver,
    /// the one-shot Application.Idle marshal onto the main thread, the command run under a document
    /// lock, baseline/reset through UNDO, and a main-thread liveness probe.</summary>
    public sealed class RealCivil3DHost
    {
        public RealCivil3DHost()
        {
            EnsureResolverRegistered();
        }

        public object AppContext => AcadApp.DocumentManager;

        // --- reload -------------------------------------------------------------------------------

        public ExecutorGeneration BuildGeneration(string dllPath, string addinName)
        {
            if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
                throw new FileNotFoundException("Payload assembly not found.", dllPath ?? "(null)");

            AddPayloadSearchDir(Path.GetDirectoryName(Path.GetFullPath(dllPath)));

            var name = string.IsNullOrWhiteSpace(addinName) ? Path.GetFileNameWithoutExtension(dllPath) : addinName;
            var identity = AssemblyName.GetAssemblyName(dllPath).Name;
            var version = VersionedIdentity.ValidateAndExtractVersion(identity, name);

            // Load INTO our AppDomain; never ExtensionLoader.Load (that pops the unsigned-DLL prompt
            // on every fresh build). Commands run through the IC3dCommand executor, never AutoCAD
            // command registration.
            var payload = Assembly.LoadFrom(dllPath);

            var app = CreateInstance(payload, name + ".App");
            var command = CreateInstance(payload, name + ".Command");
            if (!(command is IC3dCommand))
                throw new InvalidCastException(name + ".Command must implement C3dMCP.Sdk.IC3dCommand.");
            var patchService = CreateInstance(payload, name + ".PatchService") as IC3dPatchService
                ?? throw new InvalidCastException(name + ".PatchService must implement C3dMCP.Sdk.IC3dPatchService.");

            return new ExecutorGeneration(app, command, patchService, name, identity);
        }

        public bool Start(object extensionApp)
        {
            var ext = extensionApp as IExtensionApplication
                ?? throw new InvalidCastException("Payload App does not implement IExtensionApplication.");
            ExecuteInAppContext(ext.Initialize);
            return true;
        }

        /// <summary>Marshal onto the main thread and BLOCK until the action ran (5 min cap).</summary>
        public void ExecuteInAppContext(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            using (var done = new ManualResetEventSlim(false))
            {
                System.Exception captured = null;
                PostToMainThread(() =>
                {
                    try { action(); }
                    catch (System.Exception ex) { captured = ex; }
                    finally { done.Set(); }
                });
                if (!done.Wait(TimeSpan.FromMinutes(5)))
                    throw new TimeoutException("The action did not run on the AutoCAD main thread within 5 minutes.");
                if (captured != null)
                    throw new InvalidOperationException("The action failed on the main thread: " + captured.Message, captured);
            }
        }

        /// <summary>Run on AutoCAD's UI thread via a ONE-SHOT Application.Idle subscription. This
        /// deliberately replaces DocumentManager.ExecuteInApplicationContext, which pumped Idle on the
        /// calling thread and crashed the ribbon with a cross-thread violation. Non-blocking.</summary>
        public static void PostToMainThread(Action action)
        {
            EventHandler handler = null;
            handler = (s, e) =>
            {
                AcadApp.Idle -= handler;
                action();
            };
            AcadApp.Idle += handler;
        }

        /// <summary>Is the main thread reaching Idle? Answers from any thread within the budget.</summary>
        public static bool ProbeMainThread(int budgetMs, out double roundTripMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var done = new ManualResetEventSlim(false))
            {
                PostToMainThread(() => done.Set());
                bool ok = done.Wait(budgetMs);
                roundTripMs = sw.Elapsed.TotalMilliseconds;
                return ok;
            }
        }

        // --- command channel ----------------------------------------------------------------------

        /// <summary>Run the payload command on the UI thread with the document locked. Non-blocking:
        /// <paramref name="onReturned"/> fires from the main thread with the exception, if any.</summary>
        public void RunCommand(IC3dCommand executor, string command, IRunContext ctx, Action onStarted, Action<System.Exception> onReturned)
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (executor == null) { onReturned(new InvalidOperationException("No payload command is loaded; reload a payload first.")); return; }
            if (doc == null) { onReturned(new InvalidOperationException("No active drawing.")); return; }

            PostToMainThread(() =>
            {
                System.Exception error = null;
                try
                {
                    onStarted();
                    using (doc.LockDocument())
                        executor.Run(command, ctx);
                }
                catch (System.Exception ex) { error = ex; }
                onReturned(error);
            });
        }

        // --- baseline / reset ---------------------------------------------------------------------

        private string _baselineDrawingPath;

        public void Baseline()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) throw new InvalidOperationException("No active drawing.");
            _baselineDrawingPath = doc.Name;
            doc.SendStringToExecute("_.UNDO _Mark\n", true, false, false);
        }

        public void Reset(bool hard)
        {
            var dm = AcadApp.DocumentManager;
            var doc = dm.MdiActiveDocument;
            if (doc == null) throw new InvalidOperationException("No active drawing.");

            var canHardReset = hard && !string.IsNullOrWhiteSpace(_baselineDrawingPath) && File.Exists(_baselineDrawingPath);
            if (!canHardReset)
            {
                doc.SendStringToExecute("_.UNDO _Back\n", true, false, false);
                return;
            }
            var path = _baselineDrawingPath;
            ExecuteInAppContext(() =>
            {
                var previous = dm.MdiActiveDocument;
                dm.Open(path, false);
                if (previous != null && !string.Equals(previous.Name, path, StringComparison.OrdinalIgnoreCase) && previous.IsReadOnly == false)
                    previous.CloseAndDiscard();
            });
        }

        public static string ActiveDrawingName()
        {
            try { return AcadApp.DocumentManager?.MdiActiveDocument?.Name; } catch { return null; }
        }

        // --- the assembly resolver (proactive bridge delegation) ----------------------------------

        private static readonly Dictionary<string, Assembly> HostBridges =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase)
            {
                ["C3dMCP.Sdk"] = typeof(IC3dCommand).Assembly,
                ["C3dMCP.Engine"] = typeof(ExecutorGeneration).Assembly,
            };

        private static readonly string HostDir = Path.GetDirectoryName(typeof(RealCivil3DHost).Assembly.Location) ?? "";
        private static readonly object _payloadDirsSync = new object();
        private static readonly List<string> _payloadDirs = new List<string>();
        private static int _resolverRegistered;

        public static void EnsureResolverRegistered()
        {
            if (Interlocked.CompareExchange(ref _resolverRegistered, 1, 0) == 0)
                AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        }

        private static void AddPayloadSearchDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            lock (_payloadDirsSync)
            {
                if (!_payloadDirs.Exists(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)))
                    _payloadDirs.Insert(0, dir);
            }
        }

        private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            AssemblyName requested;
            try { requested = new AssemblyName(args.Name); } catch { return null; }

            var bridge = BridgeAssemblyResolver.Resolve(requested, HostBridges);
            if (bridge != null) return bridge;

            var fromHost = TryLoadFrom(HostDir, requested.Name);
            if (fromHost != null) return fromHost;

            string[] dirs;
            lock (_payloadDirsSync) dirs = _payloadDirs.ToArray();
            foreach (var dir in dirs)
            {
                var fromPayload = TryLoadFrom(dir, requested.Name);
                if (fromPayload != null) return fromPayload;
            }
            return null;
        }

        private static Assembly TryLoadFrom(string dir, string simpleName)
        {
            if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(simpleName)) return null;
            try
            {
                var candidate = Path.Combine(dir, simpleName + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            }
            catch { return null; }
        }

        private static object CreateInstance(Assembly assembly, string typeName)
        {
            var type = assembly.GetType(typeName)
                ?? throw new TypeLoadException("Payload type not found: " + typeName + " in " + assembly.GetName().Name);
            return Activator.CreateInstance(type);
        }
    }
}
