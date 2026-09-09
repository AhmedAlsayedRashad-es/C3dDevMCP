using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace C3dMCP.Engine
{
    /// <summary>A minimal JSON object writer so netstandard code can emit JSONL without
    /// System.Text.Json (which a bridge assembly must not reference). Handles string, bool,
    /// numbers, DateTime (ISO-8601 UTC), dictionaries, sequences, null. Null values are omitted.</summary>
    public static class JsonLine
    {
        private const string IsoUtc = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        public static string Object(IEnumerable<KeyValuePair<string, object>> fields)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            bool first = true;
            foreach (var kv in fields)
            {
                if (kv.Value == null) continue;
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, kv.Key);
                sb.Append(':');
                WriteValue(sb, kv.Value, 0);
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Object("a", 1, "b", "x") → {"a":1,"b":"x"}.</summary>
        public static string Object(params object[] keyValuePairs)
        {
            var list = new List<KeyValuePair<string, object>>(keyValuePairs.Length / 2);
            for (int i = 0; i + 1 < keyValuePairs.Length; i += 2)
                list.Add(new KeyValuePair<string, object>((string)keyValuePairs[i], keyValuePairs[i + 1]));
            return Object(list);
        }

        public static void WriteValue(StringBuilder sb, object v, int depth)
        {
            if (v == null) { sb.Append("null"); return; }
            if (depth > 8) { WriteString(sb, v.ToString()); return; }
            switch (v)
            {
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case DateTime dt: WriteString(sb, dt.ToUniversalTime().ToString(IsoUtc, CultureInfo.InvariantCulture)); return;
                case DateTimeOffset dto: WriteString(sb, dto.UtcDateTime.ToString(IsoUtc, CultureInfo.InvariantCulture)); return;
                case int _: case long _: case short _: case byte _: case uint _: case ulong _:
                    sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return;
                case double d: WriteDouble(sb, d); return;
                case float f: WriteDouble(sb, f); return;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
                case Enum e: WriteString(sb, e.ToString()); return;
                case IDictionary<string, object> dict:
                    WriteDict(sb, dict, depth); return;
                case IDictionary<string, string> sdict:
                    sb.Append('{');
                    bool f2 = true;
                    foreach (var kv in sdict)
                    {
                        if (!f2) sb.Append(',');
                        f2 = false;
                        WriteString(sb, kv.Key); sb.Append(':'); WriteString(sb, kv.Value);
                    }
                    sb.Append('}');
                    return;
                case IEnumerable seq:
                    sb.Append('[');
                    bool f3 = true;
                    foreach (var item in seq)
                    {
                        if (!f3) sb.Append(',');
                        f3 = false;
                        WriteValue(sb, item, depth + 1);
                    }
                    sb.Append(']');
                    return;
                default:
                    WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture)); return;
            }
        }

        private static void WriteDict(StringBuilder sb, IDictionary<string, object> dict, int depth)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, kv.Key); sb.Append(':'); WriteValue(sb, kv.Value, depth + 1);
            }
            sb.Append('}');
        }

        private static void WriteDouble(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        public static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s ?? string.Empty)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
