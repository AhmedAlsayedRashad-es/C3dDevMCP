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

## Status 2026-09-10 — Phase 7 acceptance PASSED

`IMPORTIRRG` on the Bridgetown fixture runs end to end through C3dMCP, twice:

| run | build | elapsed | finished | counts |
|---|---|---|---|---|
| 0003 | r1984 | 88.6 s | `by: payload` (not inferred) | pipeRuns 31, pipes 54, fittings 45, appurtenances 11, placed 43, skipped 0 |
| 0004 | r1985 | 56.9 s | `by: payload` | identical |

The counts match the 2026-08-31 baseline of the old harness exactly, and `doneMarkerWritten: true`.
Two Civil3D versions ran side by side (2025 on 48260, 2024 on 48288), each with its own port and payload.
Phases 0-7 are done: host, palette, HTTP runs, idle ping, MCP server, hooks, env sync, installer,
release zip in `artifacts/`.

Known: the placement queue only drains while a person is at the machine generating input (the old
project's P29 signature); with the machine idle the run stays `draining` and the harness reports it
correctly. Remaining harness items: add the bundle folder to Civil3D TRUSTEDPATHS in the installer so
the unsigned-DLL dialog stops blocking startup, and add a COM-channel probe to the idle pinger so it
reports `queued-input-dead` instead of `mainthreadbusy`.
