---
description: |
  Twice-daily dependency security reconciliation for microsoft/aspire. Reads the
  open Dependabot alerts (including malware alerts) and the code scanning alerts
  that map to a package or action version, then reconciles every alert against
  the open Dependabot pull requests:

    1. When an open Dependabot PR fixes an alert, the agent requests approval via
       the `approve-dependabot-pr` safe-output job. That job re-verifies every
       gate deterministically (Dependabot author, manifest-only diff, no new
       package source, green CI, non-breaking version change, 7-day cooldown)
       before the Aspire bot App submits an APPROVE review.
    2. When no Dependabot PR covers an alert, the agent upgrades the affected
       manifests itself and opens a single `auto-sec` labeled PR, or updates the
       already-open `auto-sec` PR instead of opening another.

  Alert details never leave the run: PR bodies and summaries describe the
  package upgrades only, and the run summary contains counts.

max-daily-ai-credits: -1

on:
  schedule:
    - cron: "17 */12 * * *"   # Every 12 hours (00:17 and 12:17 UTC)
  workflow_dispatch:

# Only run in the canonical repository, on main. Forks have neither the alerts
# nor the Aspire bot App secrets. A dispatch from another branch would load that
# branch's gate module and base the auto-sec branch on its manifests while
# reconciling main's alerts.
if: github.repository == 'microsoft/aspire' && github.ref == 'refs/heads/main'

# Never run two reconciliations at once: both would target the same auto-sec
# branch and could race on approvals.
concurrency:
  group: auto-sec
  cancel-in-progress: false

timeout-minutes: 60

# The agent is read-only. `security-events: read` lets the pre-agent data
# collection list Dependabot and code scanning alerts with GITHUB_TOKEN.
# Approvals, PR creation, and pushes happen in separate safe-output jobs.
permissions:
  contents: read
  pull-requests: read
  security-events: read
  copilot-requests: write

network:
  allowed:
    - defaults
    - github
    - node
    - python
    - dotnet
    # Approved dnceng feeds from NuGet.config and .npmrc.
    - pkgs.dev.azure.com
    - dnceng.pkgs.visualstudio.com

tools:
  edit:
  # Package managers are needed to regenerate lockfiles. The prompt requires
  # `--ignore-scripts` (npm, yarn) or `--lockfile-only` (pnpm, via corepack) so no
  # dependency install script runs in the agent.
  bash: ["cat", "ls", "grep", "head", "tail", "wc", "jq", "sed", "find", "diff", "git", "node", "npm", "yarn", "corepack", "uv"]
  github:
    toolsets: [repos, pull_requests]
    # Dependabot and the Aspire bot author the PRs this workflow reconciles. The
    # default integrity filter would hide them; the server stays scoped to this
    # repository through `allowed-repos`. The server uses the workflow token (not
    # the Aspire bot App) because a minted App token would also request the
    # workflow's `security-events` permission, which the App does not need.
    min-integrity: none
    allowed-repos:
      - microsoft/aspire

pre-agent-steps:
  # Pin the exact uv release so each run executes reviewed tooling; bump deliberately.
  - name: Install uv
    run: pipx install uv==0.12.22
  # The agent can write to the checkout, so the post-agent scrub step runs this
  # copy of the gate module, taken before the agent starts and stored outside the
  # agent sandbox mounts.
  - name: Copy gate module for the agent output scrub
    run: |
      mkdir -p "${RUNNER_TEMP}/auto-sec-gate"
      cp .github/workflows/auto-sec/auto-sec.js "${RUNNER_TEMP}/auto-sec-gate/auto-sec.js"
  - name: Collect alerts and Dependabot pull requests
    env:
      GH_TOKEN: ${{ github.token }}
      REPO: ${{ github.repository }}
    run: |
      set -euo pipefail
      mkdir -p .auto-sec

      # Only structured fields are kept. Advisory text (summaries, descriptions,
      # CVE prose) is dropped so it cannot be copied into a public PR or prompt.
      # A 403 here means the token lost alert access; fail loudly rather than
      # report a misleading "nothing to do". Every advisory range for the alert's
      # package is kept, not just the one the installed version fell in, so the
      # coverage check below rejects a target inside a later disjoint range, as the
      # approval job does. The projection runs in `jq`, not `gh --jq`, so the
      # linter treats the `$pkg` and `$key` bindings as jq variables.
      gh api --paginate "/repos/${REPO}/dependabot/alerts?state=open&per_page=100" --jq '.[]' \
        | jq -s 'map(.dependency.package as $pkg | ($pkg.name | ascii_downcase | gsub("[-_.]+"; "-")) as $key | {number, ecosystem: $pkg.ecosystem, package: $pkg.name, manifest_path: .dependency.manifest_path, scope: .dependency.scope, relationship: .dependency.relationship, vulnerable_version_range: .security_vulnerability.vulnerable_version_range, vulnerable_ranges: ([.security_vulnerability.vulnerable_version_range] + [(.security_advisory.vulnerabilities // [])[] | select(.package.ecosystem == $pkg.ecosystem and (.package.name | ascii_downcase | gsub("[-_.]+"; "-")) == $key) | .vulnerable_version_range] | map(select(type == "string" and . != "")) | unique), first_patched_version: .security_vulnerability.first_patched_version.identifier})' \
        > .auto-sec/dependabot-alerts.json
      gh api --paginate "/repos/${REPO}/dependabot/alerts?state=open&classification=malware&per_page=100" \
        --jq '.[] | .number' | jq -s '.' > .auto-sec/malware-alert-numbers.json
      jq --slurpfile malware .auto-sec/malware-alert-numbers.json \
        'map(. + {malware: (.number as $n | $malware[0] | index($n) != null)})' \
        .auto-sec/dependabot-alerts.json > .auto-sec/alerts.json

      # Code scanning alerts are in scope only when the rule maps to an action or
      # package version. Everything else is a code finding and is only counted.
      VERSION_RULES='["zizmor/unpinned-uses","zizmor/impostor-commit","zizmor/ref-confusion","zizmor/known-vulnerable-actions","zizmor/stale-action-refs","zizmor/archived-uses"]'
      gh api --paginate "/repos/${REPO}/code-scanning/alerts?state=open&per_page=100" \
        --jq '.[] | {number, rule: .rule.id, tool: .tool.name, path: .most_recent_instance.location.path, start_line: .most_recent_instance.location.start_line}' \
        | jq -s --argjson rules "${VERSION_RULES}" '{
            version_alerts: [ .[] | select(.rule as $r | $rules | index($r)) ],
            other_rule_counts: ([ .[] | select(.rule as $r | ($rules | index($r)) | not) ] | group_by(.rule) | map({rule: .[0].rule, count: length}))
          }' > .auto-sec/code-scanning.json

      # Open Dependabot PRs with their parsed updates, CI rollup, and the alerts each
      # one provably covers. Coverage uses the same module as the approval gate:
      # the head version of every changed manifest is fetched and must bind the
      # alert package to the new version in the alert's own manifest. The base
      # version is diffed too, so lockfile-only (transitive) upgrades count. Bodies
      # are parsed and then dropped. Alerts describe main, so only PRs into main count.
      gh pr list --repo "${REPO}" --author "app/dependabot" --state open --base main --limit 200 \
        --json number,title,body,headRefName,headRefOid,baseRefOid,isDraft,files,statusCheckRollup \
        > .auto-sec/dependabot-prs-raw.json
      node -e '
        const fs = require("node:fs");
        const { execFileSync } = require("node:child_process");
        const m = require("./.github/workflows/auto-sec/auto-sec.js");
        const prs = JSON.parse(fs.readFileSync(".auto-sec/dependabot-prs-raw.json", "utf8"));
        const alerts = JSON.parse(fs.readFileSync(".auto-sec/alerts.json", "utf8"));
        const ok = new Set(["SUCCESS", "SKIPPED", "NEUTRAL"]);
        const headText = (path, sha) => {
          const encoded = path.split("/").map(encodeURIComponent).join("/");
          try {
            return execFileSync("gh", ["api", "-H", "Accept: application/vnd.github.raw", "repos/" + process.env.REPO + "/contents/" + encoded + "?ref=" + sha],
              { encoding: "utf8", maxBuffer: 256 * 1024 * 1024, stdio: ["ignore", "pipe", "ignore"] });
          } catch {
            return null;
          }
        };
        const out = prs.map(pr => {
          const rollup = pr.statusCheckRollup ?? [];
          const state = c => c.conclusion || c.state || c.status || "";
          const ecosystem = m.ecosystemFromBranch(pr.headRefName);
          const updates = m.parseDependabotUpdates(pr.title, pr.body);
          const files = (pr.files ?? []).map(f => f.path);
          const headContents = {};
          const baseContents = {};
          const versionChanges = [];
          for (const path of files.filter(m.isAllowedManifest)) {
            const text = headText(path, pr.headRefOid);
            if (text !== null) {
              headContents[path] = text;
              baseContents[path] = headText(path, pr.baseRefOid) ?? "";
              if (ecosystem && ecosystem !== "actions") {
                versionChanges.push(...m.manifestVersionChanges(path, baseContents[path], text, ecosystem));
              }
            }
          }
          return {
            number: pr.number,
            title: pr.title,
            head_ref: pr.headRefName,
            head_sha: pr.headRefOid,
            draft: pr.isDraft,
            ecosystem,
            updates,
            files,
            covered_alerts: m.coveredAlerts(alerts, ecosystem, updates, versionChanges, headContents, baseContents),
            checks: { total: rollup.length, green: rollup.filter(c => ok.has(String(state(c)).toUpperCase())).length },
          };
        });
        fs.writeFileSync(".auto-sec/dependabot-prs.json", JSON.stringify(out, null, 2));
      '
      rm .auto-sec/dependabot-prs-raw.json .auto-sec/dependabot-alerts.json

      gh pr list --repo "${REPO}" --label auto-sec --state open --limit 20 \
        --json number,title,headRefName,headRefOid,author > .auto-sec/auto-sec-prs.json

      echo "Alerts: $(jq length .auto-sec/alerts.json) (malware: $(jq length .auto-sec/malware-alert-numbers.json))"
      echo "Version-mapped code scanning alerts: $(jq '.version_alerts | length' .auto-sec/code-scanning.json)"
      echo "Open Dependabot PRs: $(jq length .auto-sec/dependabot-prs.json)"
      echo "Open auto-sec PRs: $(jq length .auto-sec/auto-sec-prs.json)"

# Runs in the agent job right after the built-in secret redaction, before the step
# summaries, the safe-output ingestion, and the public `agent` artifact upload. The
# agent reads private alert details, so this deletes its transcript and logs, drops
# free-text outputs, and empties the outputs and patches if anything is off-template.
secret-masking:
  steps:
    - name: Scrub auto-sec agent transcript and outputs
      if: always()
      uses: actions/github-script@3a2844b7e9c422d3c10d287c895573f7108da1b3 # v9.0.0
      env:
        GH_AW_SAFE_OUTPUTS: ${{ steps.set-runtime-paths.outputs.GH_AW_SAFE_OUTPUTS }}
      with:
        script: await require(`${process.env.RUNNER_TEMP}/auto-sec-gate/auto-sec.js`).runAgentOutputScrub({ core });

safe-outputs:
  github-app:
    client-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
    private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
    owner: "microsoft"
    repositories: ["aspire"]
  # These outputs carry free agent text into public issues, and the agent sees private
  # alert details, so they are never published. The agent-job scrub also drops them.
  missing-tool:
    create-issue: false
  missing-data:
    create-issue: false
  report-incomplete:
    create-issue: false
  report-failure-as-issue: false
  create-pull-request:
    max: 1
    title-prefix: "[auto-sec] "
    labels: [auto-sec]
    draft: false
    # One long-lived branch keeps at most one auto-sec PR open. `recreate-ref`
    # lets the branch be reused after a previous auto-sec PR merged.
    preserve-branch-name: true
    recreate-ref: true
    allowed-branches: ["auto-sec/security-updates"]
    if-no-changes: ignore
    # Use the `am` patch transport so the patch content gate below inspects exactly
    # what the handler applies; a bundle would bypass it.
    patch-format: am
    allowed-files: &auto-sec-files
      - "**/package.json"
      - "**/package-lock.json"
      - "**/npm-shrinkwrap.json"
      - "**/yarn.lock"
      - "**/pnpm-lock.yaml"
      - "**/uv.lock"
      - "**/pyproject.toml"
      - "Directory.Packages.props"
      - "**/Directory.Packages.props"
      - "package.json"
      - "package-lock.json"
      - "yarn.lock"
      - "uv.lock"
      - "pyproject.toml"
    # The upgrade manifests are this workflow's purpose, so they are excluded from
    # gh-aw's protected-file guard. Feed and SDK configuration (NuGet.Config,
    # global.json, .npmrc) and anything under .github/ stay protected.
    protected-files: &auto-sec-protected
      policy: blocked
      exclude:
        - package.json
        - package-lock.json
        - npm-shrinkwrap.json
        - yarn.lock
        - pnpm-lock.yaml
        - uv.lock
        - pyproject.toml
        - Directory.Packages.props
  push-to-pull-request-branch:
    max: 1
    target: "*"
    required-labels: [auto-sec]
    required-title-prefix: "[auto-sec]"
    # The branch-protection pre-flight requests `administration: read`, which the
    # Aspire bot App installation does not grant (422 on token mint). The
    # auto-sec branch is a bot-owned topic branch and is never protected.
    check-branch-protection: false
    if-no-changes: ignore
    patch-format: am
    allowed-files: *auto-sec-files
    protected-files: *auto-sec-protected
  # Runs in the safe_outputs job after its checkout and before the handlers.
  # push-to-pull-request-branch has no branch filter, so this deterministic gate
  # fails the job unless every requested push targets the open
  # auto-sec/security-updates PR in this repository. The condition matches the
  # job's checkout step, which only runs when a code-writing output was emitted.
  steps:
    - name: Restrict pushes to the auto-sec branch
      if: (!cancelled()) && contains(needs.agent.outputs.output_types, 'push_to_pull_request_branch')
      uses: actions/github-script@3a2844b7e9c422d3c10d287c895573f7108da1b3 # v9.0.0
      env:
        GH_AW_AGENT_OUTPUT: ${{ steps.setup-agent-output-env.outputs.GH_AW_AGENT_OUTPUT }}
      with:
        script: |
          const gate = require(`${process.env.GITHUB_WORKSPACE}/.github/workflows/auto-sec/auto-sec.js`);
          await gate.runPushTargetGate({ github, context, core });
    # The file allowlist admits executable manifests, so this gate fails the job
    # unless every changed manifest is a dependency-version-only edit of its full
    # base file and no file adds an unapproved package source. Because the patch
    # is public, no added line may contain advisory text and comment-capable
    # lockfiles may not gain comments. It covers both code-writing outputs.
    - name: Validate auto-sec patch contents
      if: (!cancelled()) && (contains(needs.agent.outputs.output_types, 'create_pull_request') || contains(needs.agent.outputs.output_types, 'push_to_pull_request_branch'))
      uses: actions/github-script@3a2844b7e9c422d3c10d287c895573f7108da1b3 # v9.0.0
      with:
        script: |
          const gate = require(`${process.env.GITHUB_WORKSPACE}/.github/workflows/auto-sec/auto-sec.js`);
          await gate.runPatchContentGate({ github, context, core });
    # The agent sees private alert details, so every string the handlers publish (PR
    # title and body, push message, commit authors and messages) must match the fixed
    # templates in the prompt. Anything else fails the job before a handler runs.
    - name: Validate auto-sec public text
      if: (!cancelled()) && (contains(needs.agent.outputs.output_types, 'create_pull_request') || contains(needs.agent.outputs.output_types, 'push_to_pull_request_branch'))
      uses: actions/github-script@3a2844b7e9c422d3c10d287c895573f7108da1b3 # v9.0.0
      env:
        GH_AW_AGENT_OUTPUT: ${{ steps.setup-agent-output-env.outputs.GH_AW_AGENT_OUTPUT }}
      with:
        script: |
          const gate = require(`${process.env.GITHUB_WORKSPACE}/.github/workflows/auto-sec/auto-sec.js`);
          await gate.runPublicTextGate({ core });
  jobs:
    approve-dependabot-pr:
      name: "Approve Dependabot PR"
      description: |
        Request an approval for an open Dependabot pull request that fixes at
        least one open alert. Emit one item per pull request. The job re-checks
        every gate (Dependabot author, manifest-only diff, no new package source,
        green CI, non-breaking update, 7-day cooldown, fixes an open alert) and
        silently skips pull requests that fail any of them.
      runs-on: ubuntu-latest
      needs: [safe_outputs]
      permissions:
        contents: read
        pull-requests: read
        checks: read
        statuses: read
        security-events: read
      inputs:
        pr_number:
          description: "Number of the open Dependabot pull request."
          required: true
          type: number
        head_sha:
          description: "Full 40-character head commit SHA the request was evaluated against."
          required: true
          type: string
      steps:
        - name: Check out gate module
          uses: actions/checkout@v7.0.1
          with:
            persist-credentials: false
            path: _gate
            sparse-checkout: |
              .github/workflows/auto-sec/auto-sec.js
            sparse-checkout-cone-mode: false
        - name: Mint aspire-bot token
          id: approver-token
          uses: actions/create-github-app-token@v3.2.0
          with:
            client-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
            private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
            owner: microsoft
            repositories: aspire
            permission-pull-requests: write
        - name: Verify gates and approve
          uses: actions/github-script@3a2844b7e9c422d3c10d287c895573f7108da1b3 # v9.0.0
          env:
            APPROVER_TOKEN: ${{ steps.approver-token.outputs.token }}
            AUTO_SEC_BOT_LOGIN: aspire-repo-bot[bot]
          with:
            script: |
              const gate = require(`${process.env.GITHUB_WORKSPACE}/_gate/.github/workflows/auto-sec/auto-sec.js`);
              const approver = getOctokit(process.env.APPROVER_TOKEN);
              await gate.runApprovalJob({ github, approver, context, core });
---

# Auto-sec dependency reconciliation

You reconcile the open dependency security alerts of `microsoft/aspire` against the
open Dependabot pull requests. Every alert must end this run in exactly one of
these states:

- **dependabot-pr**: an open Dependabot PR fixes it, and you requested approval.
- **auto-sec-pr**: you fixed it in the single `auto-sec` pull request.
- **blocked**: it cannot be fixed safely this run (reason codes below).

## Rules that are never negotiable

1. **Never** change a package source or feed: do not edit `NuGet.config`,
   `.npmrc`, `.yarnrc*`, `global.json`, `uv.toml`, pip index settings, or any
   `registry`/`resolved`/`source` URL in a lockfile to a new source. Only the
   dnceng public feeds (`https://pkgs.dev.azure.com/dnceng/public/_packaging/...` and
   `https://dnceng.pkgs.visualstudio.com/public/_packaging/...`) and sources the file
   already uses are acceptable.
2. **7-day cooldown.** Never propose a version published less than 7 days ago.
   Check every candidate version with:
   `node .github/workflows/auto-sec/auto-sec.js lookup <npm|pip|nuget> <name> <version>`
   and use it only when `cooldown_satisfied` is `true`. Pick the lowest version that
   is at or above the first patched version and satisfies the cooldown.
3. **Non-breaking only.** Stay within the current major version (within the current
   minor for `0.x`). If the only fix is a breaking upgrade, mark the alert
   `blocked: breaking-upgrade-required`.
4. **No advisory details in public output.** PR titles, bodies, commit messages,
   and safe-output text may name packages and versions only. Never mention GHSA or
   CVE ids, severities, vulnerability descriptions, or the words "vulnerability",
   "exploit", or "malware".
5. Never run dependency install scripts or build backends. Always pass
   `--ignore-scripts` to `npm` and `yarn`, `--lockfile-only` to `pnpm` (which then
   never runs scripts), and `--no-build` to `uv`.
   Never edit files outside dependency manifests and lockfiles.

## Inputs

The pre-agent step wrote these files (read them with `cat`/`jq`):

- `.auto-sec/alerts.json`: open Dependabot alerts. `malware: true` marks malware
  alerts; they have no `first_patched_version`, and the only fix is moving to a
  version outside every `vulnerable_ranges` entry. Never remove a dependency; if no
  such non-breaking version exists, mark the alert `blocked: no-safe-version`.
- `.auto-sec/code-scanning.json`: `version_alerts` are code scanning alerts tied to
  an action or package version; `other_rule_counts` are code findings that are out
  of scope for this workflow.
- `.auto-sec/dependabot-prs.json`: open Dependabot PRs with parsed `updates`
  (`name`, `from`, `to`), changed `files`, a `checks` rollup, and
  `covered_alerts`: the alert numbers the PR provably fixes, computed by the same
  module the approval job uses.
- `.auto-sec/auto-sec-prs.json`: open pull requests labeled `auto-sec`.
- `.github/dependabot.yml`: the repository Dependabot configuration (its
  `cooldown.default-days: 7` is the policy the cooldown rule enforces).

## Step 1: Match alerts to Dependabot PRs

Use each PR's `covered_alerts` as the only source of truth for which alerts it
fixes. Do not match alerts to PRs yourself: a PR covers an alert only when the
alert number is listed in that PR's `covered_alerts`.

For every PR with a non-empty `covered_alerts` that is **not** a GitHub Actions
update, emit one `approve_dependabot_pr` item with `pr_number` and the full
`head_sha`. The approval job verifies green CI,
the cooldown, the manifest-only diff, the package source, and the version change
itself, and skips PRs that fail any check, so request approval even when CI is
still running. Still treat alerts as covered by the PR; do not duplicate the fix in
the auto-sec PR. The approval job never approves a PR that updates a package with a
`malware: true` alert; such PRs stay with a human reviewer, so do not request
approval for them, but still treat the alert as covered by the PR.

## Step 2: Fix the remaining alerts in the auto-sec PR

Collect every alert not covered in Step 1.

**Choose the working branch.**

- If `.auto-sec/auto-sec-prs.json` has an open PR on `auto-sec/security-updates`,
  update that PR: `git fetch origin auto-sec/security-updates` then
  `git checkout -B auto-sec/security-updates FETCH_HEAD`. Merge in the latest
  default branch only if the PR can no longer be cleanly updated
  (`git fetch origin main && git merge --no-edit FETCH_HEAD`); if that conflicts,
  mark the remaining alerts `blocked: auto-sec-pr-conflict` and stop.
- Otherwise start from the current checkout:
  `git checkout -B auto-sec/security-updates`.

Skip alerts the existing auto-sec PR already fixes.

**Fix by ecosystem.** Change only the alert's own manifest directory.

- **npm** (`package-lock.json`): in the manifest directory run
  `npm install <name>@<version> --package-lock-only --ignore-scripts` for direct
  dependencies. For transitive dependencies, prefer
  `npm update <name> --package-lock-only --ignore-scripts`; if the parent pins an
  old range, add an `overrides` entry in `package.json` with the patched version.
  Keep the registry the lockfile already uses.
- **yarn** (`yarn.lock`): use `yarn up <name>@<version> --mode=update-lockfile`
  (Berry) or `yarn upgrade <name>@<version> --ignore-scripts` (classic), whichever
  matches the directory, or a `resolutions` entry for transitive dependencies.
- **pnpm** (`pnpm-lock.yaml`): in the manifest directory run
  `corepack pnpm update <name>@<version> --lockfile-only` for direct dependencies.
  For transitive dependencies, add a `pnpm.overrides` entry in `package.json` with
  the patched version, then run `corepack pnpm install --lockfile-only`. Never run
  `npm` or `yarn` in a pnpm directory; they would write a second lockfile.
- **pip** (`uv.lock`): in the project directory run
  `uv lock --no-build --upgrade-package <name>==<version>`. `--no-build` stops uv
  from running a source distribution's build backend to read its metadata. If uv
  cannot resolve without building from source, mark the alert
  `blocked: source-build-required`.
- **nuget** (`Directory.Packages.props`): update a `PackageVersion` entry only
  when its `Version` is a literal and the lookup reports
  `available_on_approved_feed: true`. If the `Version` is an MSBuild property
  (for example `$(MicrosoftExtensionsAIVersion)`), the version is shared or flows
  from Arcade dependency flow through `eng/Versions.props`; do not replace it with
  a literal. Mark the alert `blocked: nuget-version-managed`. Otherwise, if the
  version is not mirrored, mark it `blocked: nuget-not-mirrored`. Never add a feed.
- **actions** and version-mapped code scanning alerts: do not edit workflow
  files. Mark them `blocked: actions-pin-requires-maintainer`; action pins must be
  updated together with the repository Actions allow-list.
- **Any other ecosystem or manifest** (for example Maven `pom.xml`, Gradle, Go
  modules, Cargo, or pip `requirements.txt`): this workflow cannot approve or fix
  it. Mark the alert `blocked: unsupported-ecosystem`; Dependabot PRs for these
  ecosystems stay with a human reviewer.

If no compliant version exists yet, use `blocked: no-version-past-cooldown`. If a
command fails or the result does not resolve, revert that directory with
`git checkout -- <dir>` and use `blocked: update-failed`.

After the edits, review `git diff --stat` and `git diff` for each lockfile. Confirm
only dependency manifests changed, no new registry host appears, and every version
change is within the same major. Commit once with exactly this message: the subject
`Update dependencies`, a blank line, then one line per package
(`<name> <from> -> <to>`, for example `lodash 4.17.20 -> 4.17.21`). Use the default
git identity; never pass `--author` or change `user.name`/`user.email`. The
safe-output job rejects any other commit message, author, title, or body.

**Emit the safe output.**

- Existing auto-sec PR: emit `push_to_pull_request_branch` with that PR's number and
  the same commit message as `message`.
- No existing PR, and you committed changes: emit `create_pull_request` with branch
  `auto-sec/security-updates`, title `Automated dependency updates`, and this body:

  ```markdown
  This is an automated pull request created by the auto-sec workflow.

  It updates the following dependencies to newer, non-breaking versions that have
  been published for at least 7 days and resolve from the existing package sources:

  | Package | Manifest | From | To |
  | --- | --- | --- | --- |
  | ... | ... | ... | ... |

  No package sources or feeds were changed. Please review the lockfile diffs and
  CI results before merging.
  ```

  Use exactly this text with one table row per updated package
  (`| <name> | <manifest path> | <from> | <to> |`); each row's package and target
  version must appear in your commit.

- Nothing to change: emit no PR output.

## Step 3: Report

Finish with a single `noop` message containing counts only, in exactly this form:
`alerts=12 dependabot-pr=3 auto-sec-pr=6 blocked=3 (nuget-not-mirrored=1, breaking-upgrade-required=2) code-findings-out-of-scope=344`.
Omit the parenthesized breakdown when `blocked=0`, and use only the reason codes
defined above. Do not list package names, alert numbers, or advisory ids in this
message; any other text fails the run. Do not emit `missing_tool`, `missing_data`,
or `report_incomplete`; they are dropped.
