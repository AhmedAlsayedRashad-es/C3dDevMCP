namespace C3dMCP.Sdk
{
    /// <summary>Implemented by the payload's <c>{AddinName}.Command</c>. The host calls
    /// <see cref="Run"/> directly on AutoCAD's main thread with the document locked; it never
    /// registers the payload's commands with AutoCAD, so a fresh build never trips the
    /// unsigned-DLL prompt. Dispatch on <paramref name="command"/>; report through
    /// <paramref name="ctx"/>. If work continues after Run returns (a SendStringToExecute queue),
    /// call <see cref="IRunContext.Defer"/> before returning and <see cref="IRunContext.Complete"/>
    /// from the completion callback.</summary>
    public interface IC3dCommand
    {
        void Run(string command, IRunContext ctx);
    }
}
