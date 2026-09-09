using System;
using System.IO;

namespace C3dMCP.Host
{
    /// <summary>Every file C3dMCP writes lives under %LOCALAPPDATA%\First Option\C3dMCP.</summary>
    public static class C3dPaths
    {
        public static string Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "First Option", "C3dMCP");

        public static string Instances => Path.Combine(Root, "instances");
        public static string Projects => Path.Combine(Root, "projects");
        public static string Payloads => Path.Combine(Root, "payloads");

        public static string InstanceFile(int pid) => Path.Combine(Instances, pid + ".json");
        public static string InstanceLog(int pid) => Path.Combine(Instances, pid + ".log");
    }
}
