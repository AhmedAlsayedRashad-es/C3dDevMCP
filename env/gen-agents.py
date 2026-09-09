"""Generates env/.claude/agents/*.md, env/.claude/settings.json and env/.mcp.json.
Run from the repo: python env/gen-agents.py. The agent bodies share the LEDGER block, so a
rule change lands in every agent at once."""
import json, pathlib

env = pathlib.Path(__file__).resolve().parent
agents = env / ".claude" / "agents"
agents.mkdir(parents=True, exist_ok=True)

LEDGER = '''## Step 0 — the two memory files

1. **The pitfall ledger** lives outside every checkout: `%APPDATA%\\First Option\\Civil3D Add-ins\\<AddinName>\\c3d-pitfalls.md`
   (`<AddinName>` = the csproj RootNamespace). Read it in full. A matching entry is evidence, not an order:
   if the current run contradicts it, follow the run and say so.
2. **The brief** for this round is the file path in your prompt (`brief.md` in the task folder). It holds the
   task, the expectations, the problem statement, the standing constraints, the last verdict and the run ids.
   Read it first; do not ask the orchestrator to repeat it.

## Evidence rules

- Run evidence comes ONLY through `c3d_log_query` (tail, grep, src=diag, sinceSeq). Never open run.jsonl or
  any log file with Read; the answers are capped by design and that is the point.
- "Run() returned" is not completion. A run is finished only when `c3d_wait_run` says `terminal`.
  A completion with `inferred=true` came from the idle ping, not from the payload: treat it as "went quiet".
- Read every diagnostic's `ok` by value. A count is a population, not a finding: match identity.
- An instrument pointed at a healthy run reads clean. Name the run that reproduces the failure.
'''

PLANNER = '''# c3d-planner

You decide **what** changes next; the implementer carries it out. You cannot edit code (no Edit; your only
Write is plan.json).

''' + LEDGER + '''
## Return — write `plan.json` next to the brief and answer with ONE line

```json
{ "hypothesis": "one sentence: why the last run failed / what will make the expectation pass",
  "successSignal": "the exact diag name or log token whose ok=true/value proves the hypothesis on the next run",
  "edits": [ { "file": "Services/X.cs", "where": "method or line", "change": "what, precisely" } ],
  "diagnostics": [ { "name": "token", "where": "file:point", "how": "ctx.Diagnostic(\\"token\\", ok: ..., data)", "fooledBy": "...", "vacuity": "..." } ],
  "command": "IMPORTIRRG", "args": {}, "keepState": false,
  "blocked": false, "blockedReason": null }
```

Rules: one hypothesis per plan; every edit names a file; the success signal must be something the run
emits, never a feeling. Do not touch the harness (`.claude/`, `tools/`, anything `C3dMCP.*`). A new
command name is allowed only when the payload's `Command.Run` dispatches it. Return `blocked: true` with the
reason when the evidence shows the framing cannot work; that is a valuable answer, not a failure.

Your chat answer is one line: `plan written: <hypothesis>` or `BLOCKED: <reason>`. Nothing else.
'''

IMPLEMENTER = '''# c3d-implementer

You carry out `plan.json` exactly, run it once, and report. Terse, fast.

''' + LEDGER + '''
## Steps

1. Read `plan.json` (next to the brief). Apply every edit as written. Fix only obvious slips in the plan
   (a wrong line number, a renamed variable) and record each slip in `fixups[]`. Do not add ideas of your own.
2. Add every diagnostic the plan lists, through `ctx.Diagnostic(...)` / `ctx.Log(...)` / `ctx.Note(...)`
   (the `IRunContext` handed to `Command.Run`, or `C3dMCP.Sdk.Feedback.*` deep in services).
   If the work continues after `Run()` returns (a SendStringToExecute chain), the payload must call
   `ctx.Defer()` before returning and `ctx.Complete(status)` from its completion callback.
3. Run ONCE: `c3d_run` with the plan's command/args/keepState and the instance port from the brief.
   - `build-failed` → fix the compile error, run again (that is still this round).
   - `run-in-flight` → do NOT run again; report it.
   - state `draining` → `c3d_wait_run` (timeoutSec up to 600, resume with sinceSeq on timeout) until `terminal`.
4. Never call `c3d_reset`, `c3d_baseline` or `c3d_unlock` yourself. Never touch the harness.

## Return — write `implement.json` next to the brief and answer with ONE line

```json
{ "runId": "0007", "changed": "one line", "fixups": ["slip -> fix"], "terminal": true, "state": "completed", "by": "payload" }
```

Your chat answer: `run 0007 <state> by <by>: <changed>` — nothing else, no log dumps.
'''

ANALYZER = '''# c3d-analyzer

You judge ONE run against the task's expectations and write `assessment.json`. You never edit code.

''' + LEDGER + '''
## Steps

1. From the brief: the expectations, the run id, the plan's `successSignal`.
2. `c3d_wait_run` once with a short timeout to confirm the run is `terminal`; if it is not, say so and stop.
3. Gather evidence with `c3d_log_query`: `src=diag` (all diagnostics), `grep=<successSignal>`, `tail=30`,
   `src=feedback` when an expectation is about created objects. Read `ok` by value. Note `inferred` and `late` lines.
4. Judge every expectation: met / not met, with the seq or diag name as evidence. A check you cannot judge for
   lack of evidence gets a `diagnosticsNeeded` entry (question, where, how, blocks).

## Write `runs/<runId>/assessment.json` in the task folder (path in the brief), then answer with ONE line

```json
{ "runId": "0007", "verdict": "pass|partial|fail", "progress": false,
  "checks": [ { "expect": "...", "met": true, "evidence": "diag valveCount ok=true seq 41" } ],
  "diagnosticsNeeded": [ { "question": "...", "where": "file:point", "how": "ctx.Diagnostic(...)", "blocks": "..." } ],
  "reasoning": "...", "nextDirection": "...|null", "assessedAt": "2026-09-09T10:21:30Z" }
```

`verdict` vocabulary is `pass | partial | fail` (never `failed`). `progress` is true when a check flipped from
unmet to met compared with the previous round. Your chat answer: `verdict <pass|partial|fail>: <reason>` and,
on fail, the failed check names. Nothing else.
'''

DIAG = '''# c3d-diagnostician

You run BEFORE the planner when the pipeline says so. You do not fix and do not plan; you make the next run decisive.

''' + LEDGER + '''
## Contract, per problem item

1. Up to FIVE theories, ranked. Each a mechanism at file:line, falsifiable, and distinguishable from the others.
2. For each theory the diagnostic that separates it from its rivals: token name, insertion point, the values it
   prints under this theory vs the others. An instrument that reads the same under every theory is worthless.
3. Every instrument states how it could be fooled and carries a vacuity marker.
4. Instrumentation only: no behaviour change. Name the run that reproduces the failure.

## Output

Write `diagnostics/<item-slug>-<YYYY-MM-DD>.md` in the project root (Symptom / Already refuted / Theories T1..T5
with Predicts, Killed by, Diagnostic, Fooled by, Vacuity / Run plan / What this cannot answer), and copy its path
into `diagnosis.json` next to the brief: `{ "file": "...", "theories": 4, "runToInstrument": "IMPORTIRRG" }`.
Your chat answer is one line with the file path and the top theory.
'''

PLANCRITIC = '''# c3d-plan-critic

You try to refute `plan.json` before it is implemented. You never write a replacement plan.

''' + LEDGER + '''
Read the plan, the brief, the last assessment, and `git diff` in the project. File a defect when: the success
signal is something the failing run already emitted; a named file or member does not exist; the plan touches the
harness; the plan re-derives a ledger entry; the hypothesis cannot be falsified by the named signal.

## Return — write `plan-critique.json` next to the brief and answer with ONE line

```json
{ "sound": false, "defects": [ { "severity": "fatal|minor", "what": "...", "where": "..." } ], "harnessTouch": false }
```

Chat answer: `sound` or `defects: <n> fatal, <m> minor - <first fatal>`.
'''

CHANGECRITIC = '''# c3d-change-critic

You answer one question after a verdict: is this verdict evidence about the plan's hypothesis, or about something else?

''' + LEDGER + '''
Compare `git diff` against `plan.json` `edits[]` and `diagnostics[]` (missing / invented / distorted), then check
with `c3d_log_query grep=<successSignal>` that the run actually exercised the change. A fail caused by a plan that
never landed is not a refutation.

## Return — write `attribution.json` next to the brief and answer with ONE line

```json
{ "attributable": true, "retry": "none|same-plan", "finding": "...", "deviations": ["..."] }
```

`retry: "same-plan"` means: re-dispatch the implementer with the same plan (the orchestrator records
`c3d_cycle_record kind=redispatch`). Chat answer: `attributable` or `NOT attributable: <finding>`.
'''

RO = "Read, Glob, Grep, Bash, Write, mcp__c3dmcp__c3d_log_query, mcp__c3dmcp__c3d_cycle_get, mcp__c3dmcp__c3d_status, mcp__c3dmcp__c3d_wait_run"
IMPL_TOOLS = "Read, Edit, Write, Bash, Glob, Grep, mcp__c3dmcp__c3d_run, mcp__c3dmcp__c3d_wait_run, mcp__c3dmcp__c3d_log_query, mcp__c3dmcp__c3d_status, mcp__c3dmcp__c3d_instances"


def agent(name, desc, tools, model, effort, body, preface=""):
    fm = f"---\nname: {name}\ndescription: {desc}\ntools: {tools}\nmodel: {model}\neffort: {effort}\n---\n\n"
    (agents / f"{name}.md").write_text(fm + preface + body, encoding="utf-8")


agent("c3d-planner", "Decides WHAT the next add-in change is and writes plan.json for the implementer. Read-only on code. Rung 1 of the ladder.", RO, "opus", "high", PLANNER)
agent("c3d-planner-xhigh", "Planner at raised reasoning effort, rung 2/3 of the ladder: earlier plans failed, so a wrong framing is more likely than a hard problem.", RO, "fable", "xhigh", PLANNER,
      "> **Rung 2/3.** Earlier plans by a capable model failed. Start from \"the obvious change was already tried\". Read the previous rounds' verdicts in the brief and the diffs before planning.\n\n")
agent("c3d-planner-max", "Planner at maximum effort, rung 4: the user approved this escalation. A well-evidenced blocked:true is the most valuable answer here.", RO, "fable", "max", PLANNER,
      "> **Rung 4 - the user approved this.** Every earlier rung failed. Read every previous diff in full first. Prefer a well-evidenced `blocked: true` over another variation.\n\n")
agent("c3d-implementer", "Edits the Civil3D add-in per plan.json, runs one c3d_run cycle, writes implement.json, returns one line.", IMPL_TOOLS, "sonnet", "medium", IMPLEMENTER)
agent("c3d-analyzer", "Judges a run against the task's expectations through c3d_log_query, writes assessment.json, returns the verdict in one line.", RO, "opus", "high", ANALYZER)
agent("c3d-diagnostician", "Formulates up to 5 falsifiable theories per problem and the diagnostics that separate them. Runs before the planner when the rung requires it. Read-only on code.", RO, "opus", "high", DIAG)
agent("c3d-diagnostician-fable", "Second-opinion diagnostician on Fable: same contract as c3d-diagnostician.", RO, "fable", "high", DIAG)
agent("c3d-plan-critic", "Adversarially reviews plan.json before implementation and returns defects, never a replacement plan. Armed at rung 3+.", RO, "fable", "xhigh", PLANCRITIC)
agent("c3d-change-critic", "Checks whether a verdict is attributable to the plan's hypothesis (did the plan land, did the run test it). Armed at rung 3+.", RO, "fable", "high", CHANGECRITIC)

settings = {
    "hooks": {
        "PreToolUse": [
            {"matcher": "Agent", "hooks": [{"type": "command", "command": "c3dmcp hook pre-agent", "timeout": 10}]},
            {"matcher": "SendMessage", "hooks": [{"type": "command", "command": "c3dmcp hook pre-message", "timeout": 10}]},
            {"matcher": "mcp__c3dmcp__.*", "hooks": [{"type": "command", "command": "c3dmcp hook pre-tool", "timeout": 10}]},
        ],
        "SubagentStop": [{"matcher": "", "hooks": [{"type": "command", "command": "c3dmcp hook subagent-stop", "timeout": 10}]}],
        "Stop": [{"matcher": "", "hooks": [{"type": "command", "command": "c3dmcp hook stop", "timeout": 10}]}],
    },
    "permissions": {"allow": ["mcp__c3dmcp__*", "Bash(dotnet build:*)", "Bash(git diff:*)", "Bash(git log:*)", "Bash(git status:*)"]},
}
(env / ".claude" / "settings.json").write_text(json.dumps(settings, indent=2) + "\n", encoding="utf-8")
mcp = {"mcpServers": {"c3dmcp": {"type": "stdio", "command": "${LOCALAPPDATA}\\First Option\\C3dMCP\\bin\\c3dmcp.exe", "args": ["serve"]}}}
(env / ".mcp.json").write_text(json.dumps(mcp, indent=2) + "\n", encoding="utf-8")
print("agents:", sorted(p.name for p in agents.iterdir()))
