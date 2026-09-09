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

## Status 2026-09-09

Phases 0–6 built and verified live on Civil3D 2024 (host, palette, HTTP runs, idle ping, MCP server,
hooks, env sync, installer, release zip in `artifacts/`). Phase 7 acceptance (IMPORTIRRG on Bridgetown)
reproduced the old project's "placement queue never drains after P29" freeze on two fresh processes; the
harness reported it correctly (draining, idle-ping timeouts, unlock). Open harness items:
1. installer: add the bundle folder to Civil3D TRUSTEDPATHS (the unsigned-DLL dialog blocks every start);
2. idle pinger: add a COM `SendCommand` probe and report `queued-input-dead` instead of `mainthreadbusy`;
3. the MCP-hosted `c3d_env_sync` call hung once (CLI path fine); child processes now redirect stdin, unverified.
