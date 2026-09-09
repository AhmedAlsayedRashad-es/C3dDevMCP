namespace C3dMCP.Server;

internal static class ToolDocs
{
    public const string Instructions = """
        C3dMCP drives a Civil3D add-in under development through a host inside Civil3D.
        Every Civil3D window is an INSTANCE with its own port, shown in the C3dMCP palette. When more than
        one instance runs, the user tells you the port; pass it as `instance`. Call c3d_instances first.
        A RUN is one invocation of a payload command. `c3d_run` builds, reloads and invokes; it returns
        when Run() returned, which is NOT completion for commands with an async tail (state "draining").
        Use `c3d_wait_run` until `terminal` is true, then read the result with `c3d_log_query` (tail, grep,
        src=diag). Never read run.jsonl files directly; every answer here is capped at ~2 KB by design.
        Exactly one run may be live per instance; a second call is refused with code run-in-flight.
        """;
}
