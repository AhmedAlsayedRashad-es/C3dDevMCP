using System;
using System.Collections.Generic;

namespace C3dMCP.Engine
{
    /// <summary>Writes <c>run.json</c>, the run's summary document, atomically on every transition.
    /// Extra fields (instance, task, counts) are supplied by the host.</summary>
    public static class RunDocument
    {
        public static string Render(RunLifecycle life, IDictionary<string, object> extra = null, long logSeq = 0)
        {
            var fields = new List<KeyValuePair<string, object>>
            {
                new KeyValuePair<string, object>("runId", life.RunId),
                new KeyValuePair<string, object>("command", life.Command),
                new KeyValuePair<string, object>("state", life.State.ToString().ToLowerInvariant()),
                new KeyValuePair<string, object>("status", life.Status),
                new KeyValuePair<string, object>("by", life.By),
                new KeyValuePair<string, object>("inferred", life.Inferred ? (object)true : null),
                new KeyValuePair<string, object>("reason", life.Reason),
                new KeyValuePair<string, object>("deferred", life.Deferred ? (object)true : null),
                new KeyValuePair<string, object>("quietSeconds", life.Deferred ? (object)life.QuietSeconds : null),
                new KeyValuePair<string, object>("createdAt", life.CreatedUtc),
                new KeyValuePair<string, object>("startedAt", life.StartedUtc),
                new KeyValuePair<string, object>("runReturnedAt", life.RunReturnedUtc),
                new KeyValuePair<string, object>("endedAt", life.EndedUtc),
                new KeyValuePair<string, object>("wallClockMs", life.EndedUtc.HasValue ? (object)(long)(life.EndedUtc.Value - life.CreatedUtc).TotalMilliseconds : null),
                new KeyValuePair<string, object>("logSeq", logSeq),
                new KeyValuePair<string, object>("error", life.Error),
                new KeyValuePair<string, object>("data", life.Data),
            };
            if (extra != null) foreach (var kv in extra) fields.Add(kv);
            return JsonLine.Object(fields);
        }

        public static void Write(string path, RunLifecycle life, IDictionary<string, object> extra = null, long logSeq = 0)
        {
            AtomicFileWriter.WriteAllText(path, Render(life, extra, logSeq) + "\n");
        }
    }
}
