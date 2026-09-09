using Autodesk.AutoCAD.Runtime;

namespace C3dPayload
{
    /// <summary>The payload's application executor. The host discovers this as {AddinName}.App and
    /// calls Initialize through the reload lifecycle. Not auto-loaded by AutoCAD: no
    /// [assembly: ExtensionApplication], no bundle, so a fresh build never trips the unsigned-DLL prompt.</summary>
    public sealed class App : IExtensionApplication
    {
        public void Initialize() { }
        public void Terminate() { }
    }
}
