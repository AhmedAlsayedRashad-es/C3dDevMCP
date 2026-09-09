using System;
using C3dMCP.Engine.Reload;
using C3dMCP.Sdk;

namespace C3dMCP.Host
{
    /// <summary>Owns the RefreshLifecycle: first reload is startup activation, later reloads run
    /// Capture → SafeEnd → Refresh on the main thread. A pending refresh left mid-flight (the
    /// payload threw) is resumed, never restarted. Refused while a run is non-terminal.</summary>
    public sealed class HotReloadService
    {
        private readonly HostApp _app;
        private readonly object _sync = new object();
        private RefreshLifecycle _lifecycle;

        public HotReloadService(HostApp app) { _app = app; }

        public sealed class Result
        {
            public bool Ok;
            public string LoadedVersion;
            public string AddinName;
            public double Ms;
            public string Error;
            public string Code;
        }

        /// <summary>The loaded payload's command executor, or null.</summary>
        public IC3dCommand CurrentExecutor
        {
            get
            {
                try
                {
                    RefreshLifecycle life;
                    lock (_sync) { life = _lifecycle; }
                    return life?.RequireRunningGeneration().Command as IC3dCommand;
                }
                catch { return null; }
            }
        }

        public Result Reload(string dllPath, string addinName)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var st = _app.State;
            try
            {
                RefreshLifecycle life;
                lock (_sync) { life = _lifecycle; }

                if (life != null && life.HasPending)
                {
                    HostLog.Write("reload: resuming a pending refresh");
                    _app.Civil.ExecuteInAppContext(() => life.ExecutePending(_app.Civil.AppContext));
                    var resumed = life.RequireRunningGeneration();
                    return Loaded(resumed, sw, dllPath);
                }

                var candidate = _app.Civil.BuildGeneration(dllPath, addinName);
                bool needsExecute = false;
                lock (_sync)
                {
                    if (_lifecycle == null)
                    {
                        _lifecycle = StartupGenerationActivator.Activate(candidate, _app.Civil.Start)
                            ?? throw new InvalidOperationException("Payload failed to start.");
                    }
                    else
                    {
                        _lifecycle.BeginRefresh(candidate);
                        needsExecute = true;
                    }
                }
                if (needsExecute)
                    _app.Civil.ExecuteInAppContext(() => _lifecycle.ExecutePending(_app.Civil.AppContext));
                return Loaded(candidate, sw, dllPath);
            }
            catch (Exception ex)
            {
                st.LastReloadError = ex.Message;
                st.LastReloadDll = dllPath;
                HostLog.Write("reload FAILED: " + ex.Message);
                _app.Registry?.Write();
                _app.PaletteHost?.Refresh();
                var code = ex is System.IO.FileNotFoundException ? "dll-not-found"
                    : ex is System.IO.InvalidDataException || ex is InvalidCastException || ex is TypeLoadException ? "dll-rejected"
                    : "reload-failed";
                return new Result { Ok = false, Error = ex.Message, Code = code, Ms = sw.Elapsed.TotalMilliseconds };
            }
        }

        private Result Loaded(ExecutorGeneration gen, System.Diagnostics.Stopwatch sw, string dll)
        {
            var st = _app.State;
            st.PayloadName = gen.AddinName;
            st.PayloadVersion = gen.LoadedVersion;
            st.PayloadLoadedUtc = DateTime.UtcNow;
            st.LastReloadError = null;
            st.LastReloadDll = dll;
            HostLog.Write("reload OK: " + gen.LoadedVersion + " in " + sw.ElapsedMilliseconds + " ms");
            _app.Registry?.Write();
            _app.PaletteHost?.Refresh();
            return new Result { Ok = true, LoadedVersion = gen.LoadedVersion, AddinName = gen.AddinName, Ms = sw.Elapsed.TotalMilliseconds };
        }
    }
}
