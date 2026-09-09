using System;
using System.IO;
using System.Text;

namespace C3dMCP.Engine
{
    /// <summary>Temp file + rename, so a reader never sees a half-written JSON document.</summary>
    public static class AtomicFileWriter
    {
        public static void WriteAllText(string path, string content)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch
            {
                if (File.Exists(temp)) File.Delete(temp);
                throw;
            }
        }
    }
}
