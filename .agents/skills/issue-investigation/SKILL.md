---
name: issue-investigation
description: Investigates GitHub issues in microsoft/aspire by gathering issue context, routing to specialized skills, preparing the right reproduction environment, reproducing behavior, and drafting evidence-backed findings or insufficient-info comments for user approval. Use when asked to investigate, reproduce, validate, debug, triage, or root-cause an Aspire issue.
---

# Aspire Issue Investigation

You are a specialized issue investigator for the `microsoft/aspire` repository. Your job is to turn an issue into a clear, evidence-backed result: reproduced, not reproduced, needs more information, duplicate/known, or ready for a fix. Do not jump directly to a code change unless the user explicitly asked for a fix and you have enough evidence to know what is wrong.

## Core rule: context before reproduction

Most Aspire issues only reproduce with the right environment: SDK/CLI version, install route, OS, shell, template, AppHost language, package feed/channel, cloud subscription, browser/VS Code setup, external services, or CI artifacts. Gather that context first, then choose the smallest environment that can faithfully reproduce the issue. Prefer the current OS and local machine first; only move to another OS, VM, container, or cloud environment when the issue is explicitly OS/environment-specific, a local attempt cannot reproduce the reported behavior because a required dependency or platform detail is unavailable, or the repro requires heavyweight/invasive software that should be isolated in a container.

If the issue body is empty or lacks critical setup details, inspect comments and linked PRs/runs first. If the missing detail still blocks reproduction, draft a concise insufficient-information comment and ask the user whether they want it posted. Do not comment on the issue automatically.

## Step 1: Resolve the issue and build a dossier

Accept issue numbers, URLs, titles, or vague references like "this issue". If no issue number is provided, search GitHub issues by title keywords and area labels.

```bash
GH_PAGER=cat PAGER=cat gh issue view <issue-number> \
  --repo microsoft/aspire \
  --json number,title,body,labels,comments,author,createdAt,updatedAt,url
```

Record these facts before running a repro:

| Field | Why it matters |
| --- | --- |
| Issue number, title, labels, author, comments | Determines owner area, related prior investigation, and likely skill handoffs |
| Reported version/channel/commit | Aspire bugs often differ between stable, daily, staging, PR, and localhive builds |
| Latest stable Aspire version | Every repro should include the current stable release unless the issue is only about a specific preview, PR build, or upgrade path |
| OS, shell, architecture, container/VM | Windows, Linux, macOS, Docker, WSL, ARM64, and shell choice can change behavior |
| Install route | Script, MSI, Homebrew, WinGet, npm package, localhive, PR dogfood, or `dotnet new install` affect paths and sidecars |
| AppHost shape | C#, file-based C#, TypeScript, Python, polyglot, templates, launch profiles, `.aspire` cache, and resource graph |
| External dependencies | Docker, Node/npm/yarn/pnpm/bun, VS Code, browsers, Azure subscription, azd, Kubernetes, databases, brokers |
| Exact expected/actual behavior | Prevents reproducing a plausible but different bug |
| Logs, screenshots, traces, run links, artifacts | Often contain the failure without requiring a full repro |

Keep scratch notes and downloaded artifacts under the session workspace or `artifacts/tmp/`, not in the repo diff.

## Step 2: Classify the issue area

Use labels first, then infer from files, commands, and symptoms. Route to more specialized skills when they match; this skill owns the cross-area triage and context-gathering layer, not the detailed workflows already covered elsewhere.

The table lists repo-local and runtime-provided skills. Invoke a runtime-provided skill only when it appears in the current session's available skills; otherwise continue with this skill's local reproduction steps instead of attempting the handoff.

| Area labels | Common paths | Repro environment and context to gather | Related skills |
| --- | --- | --- | --- |
| `area-cli`, `area-acquisition`, `area-templates` | `src/Aspire.Cli/`, `eng/scripts/get-aspire-cli*`, `src/Aspire.ProjectTemplates/` | CLI version, channel, install route, package feeds, clean temp directory, `.aspire` cache state, template name/options, OS/shell | `pr-testing`, `cli-e2e-testing`, `aspire-orchestration` |
| `area-dashboard`, `area-telemetry` | `src/Aspire.Dashboard/`, `src/Aspire.Hosting.*`, OTLP/trace/log code | AppHost that emits the data, dashboard URL, browser steps, telemetry export, trace/log IDs, culture/localization, auth mode | `dashboard-testing`, `aspire-monitoring`, `startup-perf` |
| `area-vscode-extension` | `extension/` | VS Code version, extension build/install method, configured Aspire CLI path, debug/command-palette steps, workspace shape, OS/shell | `playwright-cli`, `hex1b`, `pr-testing` |
| `area-integrations` | `src/Components/**`, `src/Aspire.Hosting.*` integrations | Integration package/version, resource type, container image, external service/emulator, connection string/properties, upstream SDK behavior | `connection-properties`, `update-container-images`, `dependency-update` |
| `area-app-model`, `area-orchestrator`, `area-service-discovery`, `area-terminal`, `area-polyglot` | `src/Aspire.Hosting/`, `src/Aspire.Hosting.AppHost/`, DCP/resource model code | AppHost source, resource graph, DCP/resource state, endpoint ports, process/container lifecycle, runtime versions, terminal recordings | `aspire`, `aspire-orchestration`, `hex1b` |
| `area-app-testing`, `area-testing` | `tests/**`, `.github/workflows/**`, `tools/CreateFailingTestIssue/` | Test method/project, CI run/job/artifacts, OS matrix, quarantined/outerloop traits, failure rate, logs | `ci-test-failures`, `fix-flaky-test`, `test-management` |
| `area-deployment`, `area-azure-aca`, `area-azd`, Kubernetes issues | deployment tests, `src/Aspire.Hosting.Azure*/`, publishing/deploy code | Azure auth/subscription, `azd` version, resource group, target cloud, generated artifacts, cleanup state, deployment mode | `deployment-e2e-testing`, `aspire-deployment` |
| `area-engineering-systems`, `area-pipelines` | `eng/**`, `.github/workflows/**`, Arcade/MSBuild files | Workflow run, job logs, affected branch, Helix/AzDO/GitHub Actions details, generated artifacts | `ci-test-failures`, `dependency-update` |

### Specialized skill routing rules

Invoke the more specific skill, when it is available in the current session, instead of re-implementing its process when one of these applies:

| Trigger in the issue | Use skill | What this investigation should still do |
| --- | --- | --- |
| Issue asks whether a PR build fixes the problem, references "Dogfood this PR", or needs validation against a PR CLI/template build | `pr-testing` | Extract the issue's repro conditions first, then pass them to the PR test as required scenarios |
| GitHub Actions run, failed job, flaky/quarantined test, `/create-issue`, or failing-test tracking issue | `ci-test-failures` or `fix-flaky-test` | Capture issue/run links, failed test names, OS matrix, and whether this is diagnosis, issue filing, quarantine, or fix work |
| Request to quarantine, unquarantine, disable, or enable tests | `test-management` | Confirm the issue URL and target test names before handoff |
| CLI E2E test authoring or debugging | `cli-e2e-testing` | Provide the user-facing scenario, target command, and expected terminal behavior |
| Dashboard unit/component test authoring or debugging | `dashboard-testing` | Provide the dashboard page/component, telemetry shape, and expected UI state |
| Azure deployment test authoring/debugging or live deployment repro | `deployment-e2e-testing`; runtime `aspire-deployment` when available | Gather subscription/auth, generated artifacts, resource group, target cloud, and cleanup requirements |
| Connection Properties or hosting integration README work | `connection-properties` | Identify the resource type, property names, README path, and expected dashboard behavior |
| Container image tag/version updates | `update-container-images` | Identify affected integration, current tag, target version constraints, and upstream compatibility notes |
| Dependency version update | `dependency-update` | Identify package name, current version, target version, and why the update is needed |
| Startup performance regression or self-profile trace analysis | `startup-perf` | Preserve the reported CLI/AppHost shape and baseline/current timings |

If a specialized skill is used, return to this skill only to summarize the issue-level outcome or to draft/post an issue comment with the evidence gathered after user approval.

## Step 3: Decide whether the issue has enough information

Use this decision tree:

1. If the issue has exact install steps, repro steps, version, OS, and expected/actual behavior, reproduce those steps first without "improving" them.
2. If the issue has partial steps but enough area context, create the smallest realistic repro that preserves the important variables.
3. If the issue is CI/test-related, inspect the linked run logs before re-running anything.
4. If the issue is platform-specific, first decide whether the platform is required to observe the bug or only part of the reporter's context. Stay on the current OS when it can still validate the behavior. Use a matching VM/container/CI path only when the repro requires that platform or when a local attempt fails and the OS mismatch remains a credible reason.
5. If the issue has an empty body or only a design request, do not claim a repro. Turn it into a requirements/design investigation and identify the missing validation path.

Targeted questions should ask for one missing blocker at a time, such as:

| Missing blocker | Example question |
| --- | --- |
| Version/channel | "Which Aspire CLI/SDK version or commit did this reproduce with?" |
| Platform | "Did this happen on Windows, Linux, macOS, WSL, or inside a container?" |
| Project shape | "Can you share the AppHost snippet or template/options used to create the app?" |
| External service | "Which Azure subscription/resource type or container image was used?" |
| UI flow | "Which VS Code command or dashboard page interaction triggers the failure?" |

### Insufficient-information comments

When an issue does not contain enough information to reproduce:

1. Check existing comments first so you do not duplicate a recent maintainer or previous agent request.
2. Ask only for details that unblock reproduction. Do not paste a generic checklist if one or two fields are enough.
3. Include what you already checked so the request is credible and actionable.
4. Present the proposed comment to the user and ask whether to post it, matching the code-review skill's "present findings first, then ask what to do next" pattern.
5. Do not include sensitive logs, tokens, resource names, subscription IDs, or private URLs in the comment.

After presenting the draft, ask for a decision with choices like:

- **Post this comment** — leave the drafted comment on the issue.
- **Edit the comment first** — revise the draft and ask again before posting.
- **Do not comment** — skip modifying GitHub and report the investigation as blocked on missing information.

Only after the user approves posting, use a body file to avoid shell quoting issues:

```bash
comment_file="$(mktemp -t aspire-issue-comment-XXXXXX.md)"
cat > "$comment_file" <<'EOF'
Thanks for the report. I don't have enough information to reproduce this yet.

I checked the issue body and existing comments, but the repro is missing:

- Aspire CLI/SDK version or commit:
- OS/shell:
- Exact project shape or AppHost snippet:
- Exact command/UI steps that trigger the problem:
- Expected vs actual behavior/log excerpt:

Could you add those details, or attach a minimal repro project? Once we have that, we should be able to try a faithful repro.
EOF

GH_PAGER=cat PAGER=cat gh issue comment <issue-number> \
  --repo microsoft/aspire \
  --body-file "$comment_file"
rm "$comment_file"
```

Tailor the missing fields to the issue area:

| Area | Ask for |
| --- | --- |
| CLI/acquisition/templates | `aspire --version`, install route/channel, exact command, template/options, clean versus existing `.aspire` state |
| Dashboard/telemetry | AppHost snippet, dashboard page, trace/log IDs, browser and culture, telemetry export or screenshot |
| VS Code extension | VS Code version, extension version/install method, configured CLI path, command/debug flow, output window logs |
| Integration/component | Package version, resource builder code, connection string/properties, container image or external service version |
| App model/orchestration | Resource graph/AppHost snippet, endpoint ports, `aspire describe`, resource states, Docker/process details |
| CI/test | Run/job URL, test method/project, OS, failure log excerpt, whether it is quarantined/outerloop |
| Deployment/Azure/Kubernetes | Target publisher/cloud, `az`/`azd` version, generated artifacts, resource group/deployment logs, cleanup constraints |

## Step 4: Prepare a faithful reproduction environment

Start from a clean workspace unless the issue is explicitly about dirty state, upgrade state, or cached files.

Default to the agent's current OS and local tooling for the first repro attempt. Do not create a Windows VM, Linux container, macOS host, cloud resource, or other remote environment just because the issue mentions one in passing. Escalate to a different environment only when the issue states the behavior is OS-specific, the dependency is unavailable locally, or the current-OS attempt cannot reproduce behavior that plausibly depends on OS, shell, filesystem, networking, browser, or cloud semantics.

Before installing anything, analyze the repro steps and create a dependency readiness summary:

1. List the required tools, runtimes, package managers, services, and credentials from the issue body, comments, linked repros, and logs.
2. Check what is already available in the current environment with lightweight version probes such as `dotnet --info`, `aspire --version`, `node --version`, `npm --version`, `docker --version`, `java --version`, `az --version`, or `gh --version`.
3. Classify each missing dependency as lightweight, heavyweight/invasive, OS-specific, cloud-only, or credential-required.
4. Present the missing dependency summary to the user before changing the host environment when the setup is heavyweight, invasive, or ambiguous.

If a repro requires installing a large or invasive toolchain, prefer a disposable container over installing it on the host. Examples include Java/Play Framework/SBT, full browser stacks, Kubernetes tooling, database servers, language runtimes not already installed, or package managers that may rewrite workspace state. For example, an issue like `#6715` that requires Java and the Play Framework should default to a containerized repro unless the user chooses local installation. Ask the user before installing heavyweight dependencies locally and offer choices like:

- **Use a container (Recommended)** — create an isolated repro environment with the required tools.
- **Install locally** — install the required software on the current machine.
- **Do not install yet** — stop and report the repro as blocked on dependency setup.

Always include the latest stable Aspire version in the repro plan. If the issue reports an old Aspire version, first try the same repro against latest stable in the smallest faithful environment. Only install or acquire the old version when you need to confirm a historical regression, validate an upgrade path, or explain a version-specific failure. If the issue does not reproduce on latest stable and the reported version is old, offer the user an issue reply that says the current stable version was checked, the issue could not be reproduced there, and asks the reporter to upgrade or provide a latest-stable repro if it still happens.

```bash
workdir="$(mktemp -d -t aspire-issue-XXXXXX)"
cd "$workdir"
```

For repo-local investigations, restore before building from the repository root:

```bash
cd <repo-root>
./restore.sh
./build.sh --build /p:SkipNativeBuild=true
```

For CLI behavior, prefer the exact reported CLI when possible:

| Reported source | Repro approach |
| --- | --- |
| Stable/daily/staging script install | Run the documented install script in an isolated install path when supported |
| PR build | Use the "Dogfood this PR" command and the `pr-testing` skill |
| Local repo build | Build `src/Aspire.Cli/Aspire.Cli.csproj` and run the produced CLI via `dotnet exec` |
| Template package | Install the reported `Aspire.ProjectTemplates` version with `dotnet new install` in an isolated temp home when practical |
| Acquisition route bug | Preserve the route exactly: Homebrew, WinGet, MSI, npm, script, localhive, or update-in-place |

For AppHost behavior, prefer an issue-provided AppHost snippet. If none exists, create the smallest AppHost that exercises the same resource graph, endpoints, package versions, and wait relationships. Keep platform details intact: Windows shell behavior, Bun/npm workspaces, Docker Desktop architecture, static ports, or launch profiles can be the bug.

When running an AppHost and you need follow-up commands, prefer `start`/`wait`/`stop` so the app stays available while you inspect it:

```bash
aspire start --project <path-to-apphost> --non-interactive
aspire wait
aspire ps
aspire describe
aspire logs <resource> --tail 100
aspire stop
```

If the issue involves the dashboard, use the runtime/plugin-provided `playwright-cli` skill when it is available to capture the dashboard state and reproduce the issue, including the page, telemetry export, relevant trace/log IDs, and browser console errors. If `playwright-cli` is unavailable, capture the same evidence with dashboard exports, Aspire CLI logs/traces or runtime `aspire-monitoring` when available, screenshots, and `dashboard-testing` for test-focused dashboard issues.

If it involves cloud deployment, record generated artifacts, resource group names, `az`/`azd` versions, and cleanup status.

## Step 5: Reproduce and narrow the cause

Run the issue's repro steps against latest stable first unless the issue is explicitly about a specific version, install/update route, or PR build. Then vary one factor at a time:

1. Latest stable versus reported version/channel versus current `main`.
2. Current OS/shell versus reported OS/shell, escalating only when the mismatch plausibly explains the result.
3. Clean cache versus existing `.aspire`, package cache, template hive, `node_modules`, or container state.
4. Minimal AppHost versus the reported real project shape.
5. Interactive versus `--non-interactive`.
6. Installed CLI versus repo-local CLI.

Capture the command, exit code, and relevant output for each attempt. Prefer structured evidence over screenshots when possible: logs, `aspire describe`, distributed traces, test results, workflow artifacts, generated files, or package versions.

Do not stop after the first "could not reproduce" if the environment differs from the report in a meaningful way. Call out the mismatch and either try a closer environment or ask for the missing detail.

After a repro attempt reaches a clear result, present the evidence to the user and ask what they want to do next instead of taking action automatically. Offer choices that match the outcome:

- **Investigate root cause** — inspect the relevant code paths, tests, logs, and telemetry to explain why the reproduced behavior happens without making a fix yet.
- **Make a fix** — do not post an issue update; implement the targeted code/test change and open a PR once the issue is reproduced or the likely cause is strong enough.
- **Update the issue** — draft a GitHub issue comment with the investigation evidence.

Before offering to update the issue, prepare a comment draft and scrub it for PII and sensitive details. Do not include local usernames or home directory paths, machine names, tokens, private URLs, subscription IDs, resource group names that identify a customer or user, secrets, credentials, exact private repository paths, or raw logs that may contain any of those values. Replace sensitive paths with placeholders such as `<scratch>`, `<repo>`, or `<redacted>`, and summarize long logs instead of pasting them verbatim. Show the sanitized draft to the user first and ask for approval before posting.

## Step 6: Investigation result format

End every investigation with a concise status and evidence:

```text
Status: reproduced | not reproduced | blocked | design-only | duplicate/known
Issue: #<number> <title>
Area: <labels and likely code area>
Environment: <OS, CLI/SDK version, install route, runtime/cloud/browser details>
Repro: <exact commands or steps that matter>
Observed: <actual behavior with evidence>
Expected: <expected behavior from issue or docs>
Likely cause: <confirmed root cause or best hypothesis; say if unconfirmed>
Artifacts: <paths/links to logs, traces, screenshots, recordings, generated files>
Proposed issue comment: <sanitized draft shown to user, posted with approval, declined, or not needed>
Next action: <ask user to choose root-cause investigation, fix/PR, issue update, missing info, owner question, or validation path>
```

Be explicit about confidence. Use "confirmed" only when the evidence directly proves the cause. Use "hypothesis" when the behavior is reproduced but the code-level cause is not yet verified.

## Step 7: If the user also asked for a fix

Only start implementation after reproduction or a strong evidence-backed root cause. Follow the repository instructions for the touched area, add or update targeted tests, and validate with the smallest command that exercises the bug. For flaky tests, quarantined tests, CI failures, deployment E2E, dashboard tests, CLI E2E, or PR-build testing, hand off to the specialized skill instead of duplicating its workflow.
