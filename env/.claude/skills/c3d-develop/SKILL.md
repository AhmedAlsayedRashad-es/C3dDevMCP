---
name: c3d-develop
description: Use to develop a Civil3D add-in through the C3dMCP loop (build, hot-reload, run, judge) with the pipeline of c3d-* agents, or to run ad-hoc drawing commands through a payload. Requires the c3dmcp MCP server and a Civil3D instance with the C3dMCP palette.
---

# c3d-develop — the loop, in the fewest tokens

You are the orchestrator. You keep almost nothing in your head: the state lives in `cycle.json`
(read it with `c3d_cycle_get`), the evidence lives in the run logs (read slices with
`c3d_log_query`), the briefs and results live in the task folder. Sub-agents read files; you
dispatch them with one-line prompts.

## Vocabulary

- **Instance** — one Civil3D window. It has a port, shown in the C3dMCP palette. `c3d_instances`
  lists them. **When more than one is running, the user tells you the port**; pass it as
  `instance` on every run/wait/status call. Never guess.
- **Run** — one invocation of a payload command (`c3d_run`). `state` `draining` means the command
  has an async tail; the run is finished only when `c3d_wait_run` returns `terminal: true`.
  `by` says who finished it: `payload` (the code called Complete), `idle-ping` (the host inferred
  it: `inferred: true`), `button` (the user), `unlock`, `drain-timeout`.
- **Round** — plan → implement → run → analyze → verdict. **Two failed rounds on the same problem
  escalate to the user.** That is the rule; do not work around it.
- **Rung** — which pipeline runs (rung 1: planner, implementer, analyzer; rung 2 adds the
  diagnostician and a stronger planner). `pipeline.json` in the project's `.c3d/` folder defines it.

## Setup (once per task)

1. `c3d_instances` → if several, ask the user for the port.
2. `c3d_status` → confirm the drawing and that no run is live.
3. `c3d_task_new` with the title and the **expectations** (each one a verifiable statement).
4. `c3d_cycle_record kind=problem problem="<one sentence>"`.
5. Create `BACKLOG.md` at the project root from `env/templates/BACKLOG.md` if it does not exist.
   Read the pitfall ledger (`%APPDATA%\First Option\Civil3D Add-ins\<AddinName>\c3d-pitfalls.md`).
6. Write the brief: `<task folder>/brief.md` from `env/templates/brief.md` (task folder path comes
   from `c3d_task_new`). The brief carries: task, expectations, problem, instance port, constraints
   (from `c3d_cycle_get`), last verdict/failed checks, run ids, ledger highlights. Update it every round.

## One round

`c3d_cycle_get` tells you `next.step` and its agent/model/effort. Dispatch that agent with this
prompt and nothing more:

```
Read <task folder>\brief.md and do your role. Instance port: <port>. Project: <projectDir>.
```

Then:

- **planner** → it writes `plan.json`. If `blocked`, record `c3d_cycle_record kind=plan blocked=true reason=…`
  and escalate: `c3d_cycle_record kind=escalate reason=…`.
- **implementer** → it runs `c3d_run` itself and writes `implement.json`. If its run is not
  terminal, call `c3d_wait_run` yourself (timeoutSec 600, resume with sinceSeq).
- **analyzer** → it writes `runs/<runId>/assessment.json`. Record the verdict:
  `c3d_cycle_record kind=verdict verdict=<pass|partial|fail> runId=<id> reason=<…> failedChecks=[…] progress=<bool>`.
- Update `BACKLOG.md`: the round's result, refuted hypotheses, baseline numbers. The Stop hook will
  not let you end a turn with a run that never reached the backlog.

The hooks enforce the order. A blocked Agent call names the tool that unblocks it; do what it says.
Never send a running c3d-* agent a message; let it finish or stop it and relaunch with a full brief.

## Escalation (the user's turn)

When `c3d_cycle_get` shows `state: escalated`, ask the user with AskUserQuestion. Show: the problem,
the two verdicts (reason + failed checks), what each round changed, and the four options:
**climb** (stronger pipeline), **retry** with the user's note, **new-approach** (restate the
problem), **stop**. Record the answer with `c3d_escalation_resolve` (add `constraint` when the user
stated a standing rule). The palette shows the same card.

## Infrastructure chores (never a finding, never a cycle)

- `run-in-flight` → `c3d_wait_run`; if nothing moves for minutes and the palette shows no activity,
  `c3d_unlock reason=…`.
- `no-task` → `c3d_task_new`. `no-instance` → ask the user to start Civil3D / give the port.
- `build-failed` → fix the compile error inside the same round.
- `version-mismatch` → the project's `c3d.json` config does not match the instance's Civil3D year.
- Never restart Civil3D yourself. `c3d_recover` captures screenshots and can send ESC when the user agrees.

## Ad-hoc commands

For a one-off drawing action, use the template payload (`c3d_project_init` on a copy of
`templates/Payload`), add a case to `Command.Run`, and `c3d_run` it with `keepState=true`.

## Ship

When the verdict is `pass`: reconcile `BACKLOG.md`, move durable lessons (3+ rounds, solved,
confirmed by a run) into the pitfall ledger, delete `BACKLOG.md` if the branch is grafted back, commit.
