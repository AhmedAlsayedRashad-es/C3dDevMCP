using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace C3dMCP.Engine
{
    /// <summary>The one stream per run: <c>run.jsonl</c>. Every line carries <c>seq</c>, <c>t</c>
    /// and <c>src</c> (log | diag | note | feedback | host). Opened with FileShare.ReadWrite and
    /// flushed per line so the server and the palette can read it while the run is live. Never
    /// appended across runs: one file, one run. Thread-safe; never throws to callers.</summary>
    public sealed class RunLog : IDisposable
    {
        private readonly object _sync = new object();
        private readonly Func<DateTime> _clock;
        private FileStream _stream;
        private StreamWriter _writer;
        private long _seq;

        public RunLog(string path, Func<DateTime> clock = null)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            _clock = clock ?? (() => DateTime.UtcNow);
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { NewLine = "\n" };
        }

        public string Path { get; }
        public long Seq { get { lock (_sync) { return _seq; } } }

        /// <summary>Raised after a line is on disk: (seq, jsonLine). Used by the palette.</summary>
        public event Action<long, string> LineWritten;

        /// <summary>Append one line. Extra fields go after seq/t/src. Returns the seq, or -1 on failure.</summary>
        public long Append(string src, IEnumerable<KeyValuePair<string, object>> fields, bool late = false)
        {
            string line;
            long seq;
            lock (_sync)
            {
                if (_writer == null) return -1;
                seq = ++_seq;
                var all = new List<KeyValuePair<string, object>>(8)
                {
                    new KeyValuePair<string, object>("seq", seq),
                    new KeyValuePair<string, object>("t", _clock()),
                    new KeyValuePair<string, object>("src", src),
                };
                if (late) all.Add(new KeyValuePair<string, object>("late", true));
                if (fields != null) all.AddRange(fields);
                line = JsonLine.Object(all);
                try
                {
                    _writer.WriteLine(line);
                    _writer.Flush();
                }
                catch { return -1; }
            }
            var h = LineWritten;
            if (h != null) { try { h(seq, line); } catch { } }
            return seq;
        }

        public long Append(string src, params object[] keyValuePairs)
        {
            var list = new List<KeyValuePair<string, object>>(keyValuePairs.Length / 2);
            for (int i = 0; i + 1 < keyValuePairs.Length; i += 2)
                list.Add(new KeyValuePair<string, object>((string)keyValuePairs[i], keyValuePairs[i + 1]));
            return Append(src, list);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                try { _writer?.Flush(); _writer?.Dispose(); } catch { }
                _writer = null;
                _stream = null;
            }
        }
    }
}
