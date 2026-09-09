using System;
using System.Collections.Generic;
using System.IO;

namespace C3dMCP.Host
{
    /// <summary>The host's own trace: instances\&lt;pid&gt;.log (always) plus the AutoCAD command line
    /// when a document is open. Keeps the last 200 lines in memory for the palette and /v1/log.</summary>
    public static class HostLog
    {
        private static readonly object Sync = new object();
        private static readonly LinkedList<string> Recent = new LinkedList<string>();
        private static string _path;

        public static event Action<string> LineWritten;

        public static void Init(int pid)
        {
            _path = C3dPaths.InstanceLog(pid);
            try { Directory.CreateDirectory(Path.GetDirectoryName(_path)); } catch { }
        }

        public static void Write(string message)
        {
            var line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message;
            lock (Sync)
            {
                Recent.AddLast(line);
                while (Recent.Count > 200) Recent.RemoveFirst();
                try { if (_path != null) File.AppendAllText(_path, line + Environment.NewLine); } catch { }
            }
            try { HostApp.WriteToCommandLine("[C3dMCP] " + message); } catch { }
            var h = LineWritten;
            if (h != null) { try { h(line); } catch { } }
        }

        public static string[] Tail(int n)
        {
            lock (Sync)
            {
                var arr = new List<string>(Recent);
                int skip = Math.Max(0, arr.Count - n);
                return arr.GetRange(skip, arr.Count - skip).ToArray();
            }
        }
    }
}
