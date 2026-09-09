IMPORTANT: Do NOT read or execute any files under ~/.claude/, ~/.agents/, .claude/skills/, or agents/. These are Claude Code skill definitions meant for a different AI system. Do NOT modify agents/openai.yaml. Stay focused on repository code only.

You are a senior product designer. Produce a UI mockup as ONE self-contained HTML file (inline CSS, no external assets, no JavaScript needed except small demo toggles) and an SVG icon set. Output them as two fenced code blocks: the first tagged ```html (the full mockup page) and the second tagged ```svg-sprite (one SVG sprite file with <symbol id="..."> entries). No prose before the first block. After the blocks, a short section "Design notes" (max 12 lines).

THE PRODUCT: "C3dMCP" — a dockable palette inside Autodesk Civil 3D (dark UI, palette width ~360-420 px, tall). It shows a developer what an AI coding agent is doing to their drawing. Aesthetic: dark, dense, engineering tool; colors close to AutoCAD's dark theme (#2b2b2b panels, #1e1e1e wells, #3c3c3c borders, light text #e6e6e6, muted #9a9a9a); one accent color for live/active state (teal or amber), green for ok, red for failed. Font: Segoe UI for text, Consolas for logs and ids. No rounded "web app" look; sharp, compact rows, 12-13 px text.

SECTIONS (top to bottom), each with a small header and a 16px icon:
1. Instance: Civil3D year + pid; the PORT as the largest number on the screen with a "Copy" icon button beside it; listener state chip (listening / acl-denied); drawing file name (ellipsis); a small main-thread dot (responsive / busy).
2. Payload: loaded identity (e.g. Civil3DInfoWorksAddin_r41), loaded time, "Reload last" button, last reload error line (collapsed when none).
3. Runs: a list of the last runs: runId (0007), command (IMPORTIRRG), state chip (running / draining animated / completed / failed / abandoned), status "by" (payload / idle-ping / button / unlock), duration. Selected row highlighted.
4. Live log: a virtualized log list for the selected run, monospace, one line per entry, colored tags for src (log / diag / note / feedback / host); diag ok=false in red with the name and data; filter row: src chips, text filter, "diag failed only" toggle, auto-scroll toggle.
5. Actions: "Mark run finished" primary button (enabled only in draining), "Idle ping now", "Baseline", "Reset" (with soft/hard menu), and an idle streak indicator "idle 2/3, last 41 ms" with three small dots filling.
6. Escalation card (shown in the mockup): "Escalation esc-2: 2 failed rounds on 'valve at bend'"; two verdict summaries side by side (round 1 / round 2, each with a one-line reason and failed check names); four buttons: Climb, Retry with note, New approach, Stop.
7. Footer: host log tail (3 lines, tiny mono), data folder path, host version.

Also render a second, narrower variant (320 px) of the Instance + Runs sections to show how it collapses.

ICONS (SVG sprite, 16x16 viewBox, 1.5px strokes, currentColor, no fill unless needed): instance, port, copy, listener-ok, listener-bad, drawing, payload, reload, runs, run-running, run-draining, run-completed, run-failed, run-abandoned, log, diag-ok, diag-fail, note, feedback, host, filter, autoscroll, mark-finished, idle-ping, baseline, reset, escalation, climb, retry, new-approach, stop, folder, settings, c3dmcp-app (the app mark: a hexagon with a pipe elbow inside).

Make every section realistic with sample data (ids, timestamps like 10:21:30.001, counts).
