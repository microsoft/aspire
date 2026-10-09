# GitHub Workflows

## Agentic workflow maintenance

Agentic workflows are authored in `.github/workflows/*.md`. Upgrade the active
compiler to the latest stable release and inspect its suggested migrations before
recompiling:

```shell
gh extension upgrade aw
gh aw version
gh aw fix
gh aw compile --force-refresh-action-pins --schedule-seed microsoft/aspire
node .github/workflows/auto-sec/publication-guard.js
gh aw compile --schedule-seed microsoft/aspire
node .github/workflows/auto-sec/publication-guard.js
```

Each compilation replaces `auto-sec.lock.yml` with raw compiler output, so run
`publication-guard.js` after every compile pass. Compare the hardened outputs of
both passes to check that the complete generation sequence is idempotent.

`gh aw fix` is a dry run unless `--write` is supplied. Its write mode also refreshes
authoring agents and skills; apply source migrations deliberately rather than
adding unrelated scaffolding. Keep the pinned `setup-cli` action and its `version`
input in `copilot-setup-steps.yml` aligned with the compiler used to generate the
workflows and with `validate-agentic-workflows.yml`. Updating only the setup
action does not update the installed compiler; its `version` input controls that.

Commit the generated `.lock.yml` files and `.github/aw/actions-lock.json` alongside
their source changes. `agentics-maintenance-microsoft-aspire.dev.yml` is generated
by the same full compilation despite not having a `.lock.yml` suffix. Do not edit
generated workflows manually. The second compilation should produce no further
changes.

Always pass `--schedule-seed microsoft/aspire` when generating workflows for this
repository. The literal `microsoft/aspire` string and each workflow's identifier
deterministically select a time for flexible schedules such as `daily around 9am`.
Without an explicit seed, gh-aw derives the repository identity from Git remotes;
forks or checkouts without a remote can generate a different cron expression than
CI. Two compilations in the same checkout can agree while still differing from CI.
The explicit seed preserves the canonical schedule regardless of checkout context.

`validate-agentic-workflows.yml` recompiles with the pinned gh-aw version, checks
for generated-file drift, runs `gh aw lint --shellcheck` as a blocking lint gate,
and runs the `Category=AgenticWorkflow` contracts in `Infrastructure.Tests`.
Class-level traits group generated-workflow, validation trigger/drift, and shared
process-runner tests without including unrelated negative-test diagnostics.
Apply this trait to new agentic contract classes so dedicated validation includes them.
Main CI covers selector routing when the trigger map or lint policy changes.
README-only changes do not trigger dedicated agentic validation; new Markdown
workflow sources still trigger it even before their generated locks exist.

Compile-time lint diagnostics alone are not a blocking gate. The lint command
owns the actionlint image and compatibility exceptions; `.github/actionlint.yaml`
is shared by both lint paths: it configures the runner label for handwritten
workflow linting and scopes the known stale-check output workaround to the
affected generated workflows. Main CI separately runs pinned core actionlint
over handwritten workflows and their local actions through
`lint-handwritten-workflows.sh`, which selects the files and excludes gh-aw output.
The version and linux_amd64 archive SHA-256 are pinned in `.github/actionlint-version.json`,
outside `.github/workflows`, so the Aspire bot can update them without workflow-write access.
The weekly `update-actionlint.yml` workflow opens a draft PR when a newer release exists. It
verifies the downloaded archive against the release API `sha256:` digest and lints the
handwritten workflows with the candidate before proposing the bump. That digest comes from the
same upstream release, so review the release notes; the committed hash then makes CI detect any
later substitution of the archive. Manual dispatch defaults to `validate`, which does not change
the updater branch or open a PR and is safe to run from a fork; `propose` runs only from the
upstream repository's default branch and follows the scheduled PR-update path when a newer release
is available. Scheduled and `propose` runs are serialized end to end by a shared concurrency
group, so each run picks its release only after the previous run has pushed.

Explicit action versions in Markdown survive recompilation, so update deprecated
inputs and action runtimes in the sources, not just the generated YAML. The
`client-id` input to `actions/create-github-app-token` replaces `app-id`; the
existing `ASPIRE_BOT_APP_ID` secret remains the identity source.

Action upgrades must update Markdown references before regenerating locks and
the action-pin cache. A generated-only pin update is not reproducible and the
drift check rejects it, even if the changed action versions are otherwise valid.

The compiler's diagnostic `agent` artifact does not include arbitrary files from
`/tmp/gh-aw/agent/`. Workflows that publish custom agent files must upload a named
artifact in `post-steps`, allowlist only the required paths, and download it in the
consuming safe-output job. CI analysis and milestone changelogs use this pattern;
their canonical safe-output JSON remains compiler-managed.

`locker.yml` still uses the archived `microsoft/vscode-github-triage-actions`
Node 20 action. There is no supported Node 24 upgrade for that dependency;
replacing it requires a separate migration of its authentication and locking
behavior, rather than merely changing an action pin.

## Auto-sec dependency reconciliation

`auto-sec.md` runs every 12 hours (and on demand). It reconciles open Dependabot
alerts, including malware alerts, and version-mapped code scanning alerts against
open Dependabot PRs. Pure code findings are out of scope. Only npm (npm, yarn,
pnpm), pip (`uv.lock`, `pyproject.toml`), NuGet (`Directory.Packages.props`), and
GitHub Actions alerts are handled; alerts in other ecosystems, such as Maven,
Gradle, Go, Cargo, or `requirements.txt`, are reported as blocked.

- **Dependabot PRs** that fix an open alert are approved by the Aspire bot App only
  when `.github/workflows/auto-sec/auto-sec.js` re-verifies every gate in the
  `approve_dependabot_pr` safe-output job. The gates are:
  - only manifest or lock files change, every commit is a GitHub-verified
    commit authored by Dependabot, and `package.json`, `pyproject.toml`, and
    `Directory.Packages.props` change only one version token on dependency-version
    lines (for `package.json`, only inside the dependency, override, and resolution
    maps, with only single-bound npm selectors eligible for edits; compound
    selectors may remain unchanged)
  - no package source or feed is added (only the dnceng public feeds and sources the
    file already uses are accepted)
  - the alert's own manifest is changed and carries the fixed version, and no copy
    of the package there stays below it or inside any range the advisory lists
  - CI and statuses are green, and the PR head is unchanged when the review is submitted
  - every package version the diff introduces, listed in the PR body or not, stays
    within the same major version (same minor for `0.x`) and was published at least
    7 days ago
  - no package the diff changes has an open malware alert (those always need a human
    review)
- Approval API, input, and summary failures fail the job with fixed
  `gate-evaluation-failed` or `approval-submission-failed` reason codes, not raw
  exception text. Unconfirmed submissions do not count as confirmed approvals,
  but still consume the submission quota because the remote review may have succeeded.
- **Remaining alerts** are fixed in a single `[auto-sec]` PR on
  `auto-sec/security-updates`, labeled `auto-sec`. Later runs update that PR
  instead of opening another one. A deterministic step in the safe-outputs job
  fails the run if the agent asks to push to any PR other than the open
  `auto-sec/security-updates` PR from this repository. A second step checks the
  agent's patch (sent with the `am` transport so the checked patch is the one
  applied) and fails the run unless each changed manifest, rebuilt in full from
  the base blob the patch names, differs only in dependency versions, or if any
  file adds an unapproved package source. NuGet bumps are made only when the fixed version
  already restores from an approved dnceng feed that `NuGet.config` package source
  mapping assigns to the package. Otherwise the alert is reported as blocked on
  mirroring.
- The PR body and run summaries intentionally contain only package and version
  summaries, never advisory details. Before publication, safe-output objects are
  rebuilt from validated primitive fields; extra properties and unused transport
  metadata are stripped rather than copied into public artifacts.
  Code-writing requests are mutually exclusive, and version policy is checked
  separately for each patch artifact so overlapping paths cannot hide an update.
  All lockfile additions accept only recognized dependency-data syntax, not new
  free-text metadata. JSON additions also require schema-defined locations in the
  reconstructed npm lockfile, not merely package/version-shaped keys; duplicate
  properties are rejected because parsing would discard earlier values. Unchanged
  pre-existing metadata is preserved. New override/resolution entries and unsupported
  lockfile metadata require human intervention and are reported as `update-failed`.
  Every patch artifact must contain a reconstructed dependency version change;
  metadata-only patches are rejected. Regenerated package metadata must belong to
  the specific installed entry whose version changes, not an unchanged copy of
  that package or an unrelated upgrade. New or re-keyed entries at a version already
  installed must preserve its existing metadata. npm project/package identities
  remain unchanged. npm workspace-root selectors and pnpm importer/snapshot
  references can regenerate only against installed targets with matching version
  transitions; they cannot authorize changing the consumer's artifact metadata.
  Other regeneration on unchanged owners requires human intervention.
  Compound npm selector edits also require human intervention: reducing `>=1 <2`
  to its first bound cannot prove changes to the other bounds non-breaking.
  Public summary rows are bound to reconstructed manifest/package/version changes,
  covering every update exactly once per manifest/package/target in the emitted batch.
  Commit and push summaries instead cover each distinct package/from/target tuple
  exactly once, including all prior versions and coalescing identical tuples across
  manifests. Each commit is bound to its own reconstructed changes; the push message
  covers the complete batch. Empty, partial, and normalized duplicate summaries
  cannot authorize publication. Intermediate-only targets absent from final files
  remain blocked by the final-state version-policy boundary.
  A created PR's fixed body explicitly identifies its table as the initial batch,
  not a cumulative inventory; subsequent updates remain in validated commit messages
  and the full branch diff. A live cumulative body refresh is not implemented.
  Counts-only reports require unique, positive blocked-reason counts whose sum equals the blocked total;
  the breakdown is omitted only when that total is zero.
  Manifest paths must be canonical repository-relative paths; dot-segment aliases,
  rooted paths, backslashes, and empty path segments cannot authorize publication.
  Reconstruction, version-only manifest edits, source authorization, and artifact
  binding are checked before public artifact upload, not just before application.
  Patch transport also rejects opaque preambles, dates, diffstat text, metadata,
  and newline markers; hunk context labels must originate in the trusted base.
  Only the canonical patch filenames are accepted; opaque bundles and symlinks
  are rejected. Framework base-commit headers are supported as typed SHAs bound,
  like output transport metadata, to the workflow snapshot or live auto-sec ref.
  The pre-upload scrub reads bases from the immutable workflow checkout and the
  bot-owned branch, not arbitrary agent-written blobs; unavailable bases block
  publication. uv local-source descriptors count as delivery sources,
  and pnpm tarball binding covers both inline and block-style resolution tables.
  The writable framework directory retains only validated safe outputs and patches:
  all other entries, including prompts and telemetry, are removed before summaries
  can consume them. Telemetry-dependent framework steps report that telemetry is
  withheld rather than parsing agent-written files or fabricating zero metrics.
  The compiler hardening also redirects the gateway and agent host-shell stdout
  and stderr to `/dev/null` before execution, including inherited child streams.
  Alert collection suppresses its own diagnostics. Pre-scrub error detection and
  secret-redaction parsers are withheld because they can re-emit private log data;
  their transcript-derived classifications are unavailable, not reported as zero.
  GitHub still records actual execution failures and exit statuses. Unsupported
  execution layouts fail compilation rather than silently losing this boundary.

Prerequisites:
- The `auto-sec` label must exist.
- The Aspire bot App needs pull request write access, and contents write access to
  push the `auto-sec` branch.

Because the compiler's Windows build mis-handles redaction paths, compile this
workflow on Linux or WSL.

After compiling `auto-sec`, run
`node .github/workflows/auto-sec/publication-guard.js`. The pinned compiler emits
`always()` on post-agent publication steps without a custom scrub-success hook.
This deterministic compilation pass gates every step after the scrub on both its
successful outcome and explicit `publication_ready` output. CI applies the same
pass before checking generated-file drift. It also restricts both uploads to
validated safe-output files and the two canonical patch filenames; no framework
logs, prompts, telemetry, or opaque bundles are uploaded. Failed or skipped scrubbing blocks
summaries, output ingestion, and both artifact uploads even when filesystem errors
leave private files on disk. Do not replace this gate with best-effort deletion.

## Quarantine/Disable Test Workflow

The `apply-test-attributes.yml` workflow allows repository maintainers to quarantine, unquarantine, disable, or enable tests directly from issue or PR comments.

### Commands

| Command | Description | Attribute Used |
|---------|-------------|----------------|
| `/quarantine-test` | Mark test(s) as quarantined (flaky) | `[QuarantinedTest]` |
| `/unquarantine-test` | Remove quarantine from test(s) | Removes `[QuarantinedTest]` |
| `/disable-test` | Disable test(s) due to an active issue | `[ActiveIssue]` |
| `/enable-test` | Re-enable previously disabled test(s) | Removes `[ActiveIssue]` |

### Syntax

```
/quarantine-test <test-name(s)> <issue-url> [--target-pr <pr-url>]
/unquarantine-test <test-name(s)> [--target-pr <pr-url>]
/disable-test <test-name(s)> <issue-url> [--target-pr <pr-url>]
/enable-test <test-name(s)> [--target-pr <pr-url>]
```

### Parameters

| Parameter | Required | Description |
|-----------|----------|-------------|
| `<test-name(s)>` | Yes | One or more test method names (space-separated) |
| `<issue-url>` | For quarantine/disable | URL of the GitHub issue tracking the problem |
| `--target-pr <pr-url>` | No | Push changes to an existing PR instead of creating a new one |

### Examples

#### Quarantine a flaky test (creates new PR)
```
/quarantine-test MyTestClass.MyTestMethod https://github.com/microsoft/aspire/issues/1234
```

#### Quarantine multiple tests
```
/quarantine-test TestMethod1 TestMethod2 TestMethod3 https://github.com/microsoft/aspire/issues/1234
```

#### Quarantine a test and push to an existing PR
```
/quarantine-test MyTestMethod https://github.com/microsoft/aspire/issues/1234 --target-pr https://github.com/microsoft/aspire/pull/5678
```

#### Unquarantine a test (creates new PR)
```
/unquarantine-test MyTestClass.MyTestMethod
```

#### Unquarantine and push to an existing PR
```
/unquarantine-test MyTestMethod --target-pr https://github.com/microsoft/aspire/pull/5678
```

#### Disable a test due to an active issue
```
/disable-test MyTestMethod https://github.com/microsoft/aspire/issues/1234
```

#### Enable a previously disabled test
```
/enable-test MyTestMethod
```

#### Comment on a PR to push changes to that PR
When you comment on a PR (not an issue), the workflow will automatically push changes to that PR's branch instead of creating a new PR. You can override this by specifying `--target-pr`.

### Behavior

1. **Permission Check**: Only users with write access to the repository can use these commands.
2. **Processing Indicator**: The workflow adds an 👀 reaction to your comment when it starts processing.
3. **Status Comments**: The workflow posts comments to indicate:
   - ⏳ Processing started
   - ✅ Success (with link to created/updated PR)
   - ℹ️ No changes needed (test already in desired state)
   - ❌ Failure (with error details)

### Target PR Behavior

| Context | `--target-pr` specified | Result |
|---------|-------------------------|--------|
| Comment on Issue | No | Creates new PR from `main` |
| Comment on Issue | Yes | Pushes to specified PR |
| Comment on PR | No | Pushes to that PR's branch |
| Comment on PR | Yes | Pushes to specified PR (overrides) |

### Restrictions

- The `--target-pr` URL must be from the same repository
- Cannot push to PRs from forks
- Cannot push to closed PRs
- The PR branch must not be protected in a way that prevents pushes

### Concurrency

The workflow uses concurrency groups based on the issue/PR number to prevent race conditions when multiple commands are issued on the same issue.

## Backmerge Release Workflow

The `backmerge-release.yml` workflow automatically creates PRs to merge changes from `release/13.3` back into `main`.

### Schedule

Runs daily at 00:00 UTC (4pm PT during standard time, 5pm PT during daylight saving time). Can also be triggered manually via `workflow_dispatch`.

### Behavior

1. **Change Detection**: Checks if `release/13.3` has commits not in `main`
2. **PR Creation**: If changes exist, creates a PR to merge `release/13.3` → `main`
3. **Auto-merge**: Enables GitHub's auto-merge feature, so the PR merges automatically once approved
4. **Conflict Handling**: If merge conflicts occur, creates an issue instead of a PR

### Assignees

PRs and conflict issues are automatically assigned to @joperezr and @radical.

### Manual Trigger

To trigger manually, go to Actions → "Backmerge Release to Main" → "Run workflow".
