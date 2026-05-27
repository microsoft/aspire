# CLI E2E test troubleshooting

This document is a catalog of recurring flake patterns observed in `tests/Aspire.Cli.EndToEnd.Tests/` and the recipes to diagnose them. The target audience is future agent sessions investigating a CLI E2E flake. It complements `SKILL.md` (which is a "how to write tests" guide) with diagnostic detail.

## Step 1 — Get the *right* artifact for the failing attempt

CI re-runs (manual or automatic) on a failed job upload artifacts with the **same name** as earlier attempts of the same job in the same workflow run. `gh run download` always returns the latest one, which is often a *passing* rerun. The investigation must look at the *failing* attempt's artifact.

Recipe:

```bash
# 1. From the failing job URL, get the run id and the failing attempt's started_at.
gh api repos/microsoft/aspire/actions/runs/<RUN_ID>/attempts/<ATTEMPT_NUMBER> \
  --jq '{attempt:.run_attempt, started:.run_started_at}'

# 2. List artifacts of that workflow run filtered by artifact name.
gh api -X GET "repos/microsoft/aspire/actions/artifacts" \
  -f name="logs-ChannelUpdateWorkflowTests-ubuntu-latest" \
  --jq '.artifacts[] | select(.workflow_run.id == <RUN_ID>) | {id, created_at, name}'

# 3. Pick the artifact whose created_at is closest to (and after) the failing
#    attempt's started_at — that's the one uploaded by the failing attempt.
#    Download it explicitly by id.
gh api repos/microsoft/aspire/actions/artifacts/<ARTIFACT_ID>/zip > /tmp/failed.zip
unzip -q /tmp/failed.zip -d /tmp/failed
```

`gh run download` cannot disambiguate by attempt — only `gh api .../artifacts/<id>/zip` will reliably get the failing recording.

## Step 2 — Reconstruct the terminal stream from the `.cast` file

Each test that uses Hex1b records an asciinema cast at `testresults/recordings/<TestName>.cast`. The file is JSONL: line 1 is the header (cols, rows, env), subsequent lines are `[time, "o", payload]` events. To see what the terminal looked like around a specific moment:

```python
import json, re, sys

ANSI = re.compile(r'\x1b\[[0-9;?]*[A-Za-z]|\x1b[\(\)][AB012]|\x1b\][^\x07]*\x07')
with open("/tmp/failed/<TestName>.cast") as f:
    header = json.loads(next(f))
    events = [json.loads(line) for line in f]
stream = "".join(e[2] for e in events if e[1] == "o")
clean = ANSI.sub("", stream)
# Show a window around the offending text.
i = clean.find("aspire.config.json")
print(clean[max(0, i - 2000): i + 2000])
```

For chronological analysis (was X printed before Y?), keep the event timestamps:

```python
needle = "Perform updates?"
acc = ""
for t, kind, payload in ((e[0], e[1], e[2]) for e in events if e[1] == "o"):
    acc += payload
    if needle in acc:
        print(f"t={t:.3f}s after {needle!r} appeared")
        break
```

## Step 3 — Understand the prompt-counter convention

Tests rely on a deterministic shell prompt for "command finished" detection:

- `tests/Shared/CliInstallStrategy.cs` configures bash with `PROMPT_COMMAND='s=$?;((CMDCOUNT++));PS1="[$CMDCOUNT $([ $s -eq 0 ] && echo OK || echo ERR:$s)] \$ "'`. Bash bumps `CMDCOUNT` and renders `[N OK] $ ` (or `[N ERR:<code>] $ `) every time it returns to the prompt.
- `SequenceCounter` (`tests/Shared/Hex1bTestHelpers.cs`) is the test's mirror of `CMDCOUNT`. The wait helpers (`WaitForSuccessPromptAsync`, `WaitForAnyPromptAsync`, `WaitForAspireAddSuccessAsync`) search the snapshot for `[<counter.Value> OK] $ ` as a substring, then `Increment()` the counter on a match.
- The counters are supposed to be in lockstep. The test increments its counter once per `Wait...Prompt` call; bash increments `CMDCOUNT` once per prompt display. **Anything that causes bash to display an extra prompt the test does not account for desyncs the two counters** — and turns the next "wait for prompt N" into a potential false positive on an already-on-screen prompt line that contains the substring `N OK] $ `.

## Step 4 — Diagnose the "Y/n input race"

This is the most-observed flake class so far. It is what broke `ChannelUpdateWorkflowTests.UpdateProjectChannelToStable_TypeScript_PreviewsStablePackagesAndPreservesChannel` on PR #17522 (run `26489967289`, job `78006625708`).

### Symptom

An assertion that reads on-disk state (typically `aspire.config.json` after `aspire add`) fails saying the expected content is missing. Inspecting the cast shows the CLI eventually *did* succeed — the test read the file too early.

### Recording signature

In the cast around the failing read, look for an **extra empty `[N OK] $` prompt cycle** right after a `[Y/n]:` prompt was answered, followed by the next typed command at `[N+1]`:

```
Perform updates? [Y/n]: n
[21 OK] $
[22 OK] $ aspire add Aspire.Hosting.PostgreSQL
```

A passing recording for the same test goes straight from `[Y/n]: n` to `[21 OK] $ aspire add ...` with no empty intermediate prompt.

### Mechanism

`TypeAsync("n") + EnterAsync()` writes two bytes into the TTY input queue: `n` and `\n`. Spectre.Console's `[Y/n]` prompt is a single-character reader — it returns on the `n` keystroke and tears down its stdin handler before the `\n` is consumed. Whichever process owns the TTY when that `\n` is delivered receives it. If the CLI has already exited (or is in the middle of its teardown when bash reclaims the TTY), bash reads the stray `\n` as an empty command line, fires `PROMPT_COMMAND`, and increments `CMDCOUNT`.

The test's `SequenceCounter` doesn't know about that extra cycle. It advances through the next `WaitForSuccessPromptAsync` (which still matches — the real post-command prompt is on screen), ending up coincidentally equal to bash's drifted `CMDCOUNT`. The next typed command — say `aspire add Postgres` — gets a fresh prompt at `[<N+1> OK] $ aspire add Postgres`. When the test then calls `WaitForAspireAddSuccessAsync`, the substring matcher finds `<N+1> OK] $ ` *in the typed-command header line that bash printed when accepting the command*, not in the post-completion prompt, and returns success **before** `aspire add` has done any work.

The test then reads `aspire.config.json` while the CLI is still spinning. For a polyglot (TypeScript/Java) apphost, `aspire add` writes the file via `GuestAppHostProject.SaveConfiguration` *after* a full `BuildAndGenerateSdkAsync` round-trip (which starts an `AppHostServerSession` for code generation). That step is not instant, so the early read sees the pre-add config.

### Fix

The Spectre.Console `[Y/n]` confirmation prompt accepts a single character — it does not require Enter. Drop the Enter:

```diff
- await auto.TypeAsync("n");
- await auto.EnterAsync();
+ await auto.TypeAsync("n");
```

This is the same pattern already used in `Hex1bAutomatorTestHelpers.DeclineAgentInitPromptAsync`. Its comment documents the same race.

This fix is **only required when both a character and Enter are sent** for a single-character prompt. The following do *not* have the race:

- `EnterAsync()` alone to accept a Y/n default — the `\n` *is* the commit byte for the line reader.
- `TypeAsync("/some/path") + EnterAsync()` for a text-input prompt — the line reader reads through `\n` to terminate the line.
- Arrow keys + `EnterAsync()` for a Spectre selection list — Enter is the commit byte for the selection.

### Why a "wait for the prompt text to disappear" helper does *not* work

A tempting "general" fix is to block until the prompt text is no longer visible in the snapshot. Spectre.Console typically leaves the answered prompt visible as scrollback (the question is rendered once and stays in the terminal buffer, sometimes rewritten to include the chosen answer). Such a helper would either never observe the prompt "disappear" (and time out) or would only work when subsequent output happens to scroll the prompt off-screen — neither is reliable.

The single-character-prompt fix above is more surgical and matches the established convention in the codebase.

## Other flake classes (placeholders — fill in as encountered)

- **Spinner-scroll obscuring an awaited line.** When `aspire run` / `aspire add` print a spinner that updates frequently, an awaited status line (e.g., `Update successful!`) can be redrawn off the visible 160×48 grid before `WaitUntilTextAsync` runs its next snapshot poll. Document mitigations here when first observed.
- **Race between `aspire start` and dashboard readiness.** The "dashboard at <URL>" line can appear before the dashboard's `/health` actually responds. Document the canonical post-start synchronization here.
- **`aspire add` version-picker shown vs not-shown.** Some package configurations cause the picker to appear; others auto-select. Tests that always send Down/Enter break in the auto-select case. Document the "is the picker on screen?" check here.

## Workflow-infrastructure gotchas worth knowing about

These bit the PR #17522 investigation and are worth a callout (but are not the test's fault):

- **Same-name artifact collision across rerun attempts.** See Step 1 above.
- **`CaptureWorkspaceOnFailureAttribute` captures live workspace state to `testresults/workspaces/<TestName>/`** but the CI upload globs don't include that directory, so the captured `aspire.config.json` (which would directly answer "what was on disk at failure time?") is currently lost. Worth fixing separately.
- **`testresults/recordings/<TestName>.cast` is overwritten on retry.** If the test's xUnit retry policy or a manual rerun re-executes the same method, the failing recording is clobbered by the passing one. The artifact saved with the *failing attempt* is the only durable copy — another reason Step 1 matters.
