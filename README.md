# C3dDevMCP

C3dMCP: a dotnet MCP server, an HTTP-driven host add-in inside Civil3D, and an enforced
development cycle for Civil3D add-in work with Claude Code.

- `src/C3dMCP.Sdk` — the payload contract (`IC3dCommand`, `IRunContext`, `IC3dPatchService`).
- `src/C3dMCP.Engine` — pure logic: hot-reload lifecycle, run state machine, idle-ping judge, run log.
- `src/C3dMCP.Host` — the stable Civil3D bundle: HTTP listener, run lifecycle, hot reload, WPF palette.
- `src/C3dMCP.Server` — the `c3dmcp` MCP server (stdio) and `hook` CLI, built on the official
  [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) package.
- `env/` — the Claude Code environment (agents, skills, hooks, templates) that `c3d_env_sync` installs.

Data lives under `%LOCALAPPDATA%\First Option\C3dMCP\`.
