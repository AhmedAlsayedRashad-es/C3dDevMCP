using System;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.Runtime;
using C3dMCP.Host;

[assembly: ExtensionApplication(typeof(HostApp))]
[assembly: CommandClass(typeof(HostCommands))]

namespace C3dMCP.Host
{
    /// <summary>The stable C3dMCP shell: loads once per Civil3D process, acquires a port, serves
    /// the HTTP API, registers the instance, and hosts the palette. Never hot-reloaded.</summary>
    public sealed class HostApp : IExtensionApplication
    {
        internal const string CivilVersion =
#if R2024
            "2024";
#elif R2025
            "2025";
#elif R2026
            "2026";
#else
#error Build with -c R2024 / R2025 / R2026 so the host knows its Civil3D version.
#endif

        internal static HostApp Current { get; private set; }
        internal HostState State { get; private set; }
        internal RealCivil3DHost Civil { get; private set; }
        internal HttpApi Api { get; private set; }
        internal InstanceRegistry Registry { get; private set; }
        internal HotReloadService Reload { get; private set; }
        internal RunManager Runs { get; private set; }
        internal Palette.PaletteHost PaletteHost { get; set; }

        public void Initialize()
        {
            Current = this;
            State = new HostState { CivilVersion = CivilVersion, HostVersion = typeof(HostApp).Assembly.GetName().Version.ToString(3) };
            HostLog.Init(State.Pid);
            HostLog.Write("C3dMCP host " + State.HostVersion + " loading (Civil3D " + CivilVersion + ", pid " + State.Pid + ").");
            try
            {
                Civil = new RealCivil3DHost();
                Reload = new HotReloadService(this);
                Runs = new RunManager(this);
                Runs.Changed += _ => PaletteHost?.Refresh();

                var acquired = PortAcquirer.Acquire(State.Pid);
                State.Port = acquired.Port;
                State.ListenerState = acquired.State;
                State.ListenerError = acquired.Error;
                if (acquired.Listener != null)
                {
                    Api = new HttpApi(acquired.Listener, State.Token);
                    Routes.Register(this);
                    Api.Start();
                    HostLog.Write("Listening on " + acquired.Prefix);
                }
                else
                {
                    HostLog.Write("Listener NOT started: " + acquired.State + " — " + acquired.Error);
                }

                Registry = new InstanceRegistry(State);
                Registry.Start();
                HostLog.Write("Instance file: " + Registry.Path);

                Application.DocumentManager.DocumentActivated += (s, e) => { RefreshDrawing(); };
                Application.DocumentManager.DocumentCreated += (s, e) => { RefreshDrawing(); };
                RefreshDrawing();

                // The palette needs AutoCAD's UI to exist; create it on the first Idle tick.
                RealCivil3DHost.PostToMainThread(() =>
                {
                    try { PaletteHost = new Palette.PaletteHost(this); PaletteHost.Show(); }
                    catch (System.Exception ex) { HostLog.Write("palette failed (listener unaffected): " + ex.Message); }
                });
            }
            catch (System.Exception ex)
            {
                HostLog.Write("Initialize failed: " + ex);
            }
        }

        public void Terminate()
        {
            try { Api?.Dispose(); } catch { }
            try { Registry?.Dispose(); } catch { }
        }

        internal void RefreshDrawing()
        {
            State.Drawing = RealCivil3DHost.ActiveDrawingName();
            Registry?.Write();
            PaletteHost?.Refresh();
        }

        internal static void WriteToCommandLine(string message)
        {
            var doc = Application.DocumentManager?.MdiActiveDocument;
            doc?.Editor.WriteMessage("\n" + message + "\n");
        }
    }

    /// <summary>AutoCAD commands the host itself registers (listed in PackageContents.xml).</summary>
    public static class HostCommands
    {
        /// <summary>Show the palette.</summary>
        [CommandMethod("C3DMCP", CommandFlags.Modal)]
        public static void ShowPalette()
        {
            var app = HostApp.Current;
            if (app == null) return;
            try
            {
                if (app.PaletteHost == null) app.PaletteHost = new Palette.PaletteHost(app);
                app.PaletteHost.Show();
            }
            catch (System.Exception ex) { HostLog.Write("palette failed: " + ex.Message); }
        }

        /// <summary>The idle-ping sentinel: a no-op the host sends through the command queue to
        /// measure whether the queue is empty. Leaves no history and no undo mark.</summary>
        [CommandMethod("C3DMCP_IDLEPING", CommandFlags.Modal | CommandFlags.NoHistory)]
        public static void IdlePing()
        {
        }
    }
}
