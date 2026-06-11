---
description: |
  Analyzes merged pull requests for significant user-facing changes. When a
  PR is merged against main or release/* branches, this workflow determines
  whether microsoft/aspire.dev needs a documentation PR. If documentation
  updates are required, it creates a draft PR with the changes following the
  doc-writer skill conventions. The draft PR targets the aspire.dev branch
  resolved from the source PR's release reasoning (PR milestone, linked-issue
  milestone, then source PR base), using the matching release/* branch when it
  already exists and falling back to aspire.dev main otherwise. It also
  comments on the original PR with a link to the draft PR (or a "no docs
  needed" message).

max-daily-ai-credits: -1

on:
  pull_request:
    types: [closed]
    branches:
      - main
      - release/*
  workflow_dispatch:
    inputs:
      pr_number:
        description: "PR number to analyze"
        required: true
        type: string
  stale-check: false

if: >-
  (github.event.pull_request.merged == true || github.event_name == 'workflow_dispatch')
  && github.repository_owner == 'microsoft'

# Cap the number of agent iterations (compiles to the AWF proxy's `maxRuns`).
# The proxy refuses further inference once EITHER `maxRuns` OR the 25M
# `maxEffectiveTokens` rail is hit. Historically `maxRuns` defaulted to 500 and
# was never reached: a deterministic `create_pull_request` failure ("No changes
# to commit") sent the agent into a retry loop that crossed the 25M effective-
# token rail and hard-failed the run. AWF audit data on real runs: healthy
# skip-path runs use ~4-5 inference requests; the runaway failures used ~34 to
# reach the rail. This cap of 12 sits well above a legitimate run (the doc-writing
# path needs a few more requests than a skip to read the skill, browse docs,
# write files, and open the PR) yet far below the ~34-request runaway, so it
# stops any loop long before the token rail. The anti-loop guidance in "Create
# Draft PR" below stops the loop at its source; this is the hard backstop.
max-runs: 12

checkout:
  # Check out aspire.dev EXACTLY ONCE, as the current workspace, because that is
  # where documentation changes are authored and the docs branch is created.
  #
  # IMPORTANT: do NOT add a second `microsoft/aspire.dev` checkout (e.g. a mirror
  # under `_repos/aspire.dev`) to this agent-job `checkout:` block. gh-aw builds a
  # checkout manifest keyed by the lowercased repo slug, and the LAST entry for a
  # slug wins (see build_checkout_manifest.cjs). A second `microsoft/aspire.dev`
  # entry shadows this current-workspace entry, so the `create_pull_request`
  # safe-output's `findRepoCheckout("microsoft/aspire.dev")` resolves to the
  # mirror instead of the workspace where the agent actually created the docs
  # branch. The handler then pins the branch with
  # `git -C <mirror> rev-parse --verify refs/heads/<branch>^{commit}`, which fails
  # with `fatal: Needed a single revision` because the branch only exists in the
  # workspace (microsoft/aspire#18319, run 27765082872). The manifest already
  # maps `microsoft/aspire.dev -> path=""` (the workspace) here, so a mirror is
  # not needed for the handler to rediscover the target repo. The safe-outputs
  # job keeps its own separate `_repos/aspire.dev` checkout for bundle apply.
  - repository: microsoft/aspire.dev
    github-app:
      app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
      private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
      owner: "microsoft"
      repositories: ["aspire.dev"]
    current: true
    # Fetch release/* refs in addition to the default branch so the
    # `Resolve target aspire.dev branch` pre-agent step (and the agent
    # itself, when it switches the workspace to the effective branch) can
    # enumerate aspire.dev's release/* branches locally from
    # `refs/remotes/origin/release/*`. If this fetch silently produces
    # nothing (e.g., the action ignores the refspec), the resolver still
    # falls back to a `gh api /repos/microsoft/aspire.dev/branches` call
    # using the aspire-bot installation token, so target-branch selection
    # remains correct — the local refs are just a faster, offline path.
    fetch: ["release/*"]

permissions:
  contents: read
  pull-requests: read
  issues: read
  copilot-requests: write

network:
  allowed:
    - defaults
    - github

tools:
  github:
    toolsets: [repos, issues, pull_requests]
    # Keep the guard policy explicit so gh-aw does not inject a separate
    # auto-lockdown github-script step with an independently resolved action pin.
    min-integrity: approved
    allowed-repos:
      - microsoft/*
    github-app:
      app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
      private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
      owner: "microsoft"
      repositories: ["aspire.dev", "aspire"]

safe-outputs:
  github-app:
    app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
    private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
    owner: "microsoft"
    repositories: ["aspire.dev", "aspire"]
  steps:
    - name: Mirror target repo checkout
      if: contains(needs.agent.outputs.output_types, 'create_pull_request')
      uses: actions/checkout@v6.0.2
      with:
        repository: microsoft/aspire.dev
        # Seed the mirrored workspace at aspire.dev main. The safe-outputs
        # handler will fetch and use the agent-provided `base` override when
        # creating the PR, restricted by `allowed-base-branches` below.
        ref: main
        token: ${{ steps.safe-outputs-app-token.outputs.token }}
        persist-credentials: false
        path: _repos/aspire.dev
        fetch-depth: 1
    - name: Configure mirrored target repo Git credentials
      if: contains(needs.agent.outputs.output_types, 'create_pull_request')
      working-directory: _repos/aspire.dev
      env:
        REPO_NAME: "microsoft/aspire.dev"
        SERVER_URL: ${{ github.server_url }}
        GIT_TOKEN: ${{ steps.safe-outputs-app-token.outputs.token }}
      run: |
        git config --global user.email "github-actions[bot]@users.noreply.github.com"
        git config --global user.name "github-actions[bot]"
        git config --global am.keepcr true
        SERVER_URL_STRIPPED="${SERVER_URL#https://}"
        git remote set-url origin "https://x-access-token:${GIT_TOKEN}@${SERVER_URL_STRIPPED}/${REPO_NAME}.git"
        echo "Mirrored checkout configured with standard GitHub Actions identity"
  create-pull-request:
    title-prefix: "[docs] "
    labels: [docs-from-code]
    # NOTE: do NOT set a static `reviewers:` here. The drafted PR's reviewer is
    # the SME identified from the source aspire PR's reviews at runtime, and
    # that decision can't live in static frontmatter. The `notify-source-pr`
    # safe-output job below requests the SME on the drafted PR after creation.
    draft: true
    # Default to aspire.dev main, but allow the agent to override the PR base
    # per run using the milestone/linked-issue/source-base reasoning in the
    # prompt body. Restrict overrides to main and release/*.
    base-branch: main
    allowed-base-branches:
      - main
      - release/*
    target-repo: "microsoft/aspire.dev"
    # Keep protected-file handling explicit in the source of truth. Copilot
    # workflows automatically protect AGENTS.md alongside dependency manifests
    # and repository security config unless this policy is intentionally relaxed.
    protected-files: blocked
    fallback-as-issue: true
  jobs:
    notify-source-pr:
      name: "Notify source PR"
      description: |
        Post the documentation analysis result as a comment on the source
        microsoft/aspire pull request and (when a draft documentation PR was
        opened on microsoft/aspire.dev) request a review from the SME
        identified from the source PR.

        Emit exactly one `notify_source_pr` item per run, after you've finished
        any `create_pull_request` or no-docs-needed reasoning. Use `result:
        "drafted"` when you just emitted a `create_pull_request`; use `result:
        "skipped"` when no docs PR is needed. DO NOT try to embed the drafted
        PR's URL or number in the `summary` text — the workflow knows them
        from the safe-outputs handler and will substitute the real values.
      runs-on: ubuntu-latest
      needs: [safe_outputs]
      permissions:
        contents: read
      inputs:
        source_pr_number:
          description: "PR number on microsoft/aspire that triggered this run."
          required: true
          type: number
        result:
          description: "'drafted' if a docs PR was opened on microsoft/aspire.dev, or 'skipped' if no docs PR was needed."
          required: true
          type: string
        sme_login:
          description: "GitHub login of the SME from the source PR (preferred reviewer for the drafted docs PR). Empty string if no SME was identified."
          required: false
          type: string
        target_branch:
          description: "Effective target branch on microsoft/aspire.dev (only meaningful when result is 'drafted')."
          required: false
          type: string
        summary:
          description: "Short markdown summary of the documentation changes (when drafted) or rationale (when skipped). 1-3 sentences plus optional bullet list. DO NOT include the drafted PR URL or number — the workflow injects those automatically."
          required: true
          type: string
      steps:
        - name: Mint aspire-bot token (microsoft/aspire)
          id: aspire-token
          uses: actions/create-github-app-token@v3.1.1
          with:
            app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
            private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
            owner: microsoft
            repositories: aspire
        - name: Mint aspire-bot token (microsoft/aspire.dev)
          id: aspire-dev-token
          if: needs.safe_outputs.outputs.created_pr_url != ''
          uses: actions/create-github-app-token@v3.1.1
          with:
            app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
            private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
            owner: microsoft
            repositories: aspire.dev
        - name: Post status comment on source PR
          uses: actions/github-script@v9
          env:
            DRAFT_PR_URL: ${{ needs.safe_outputs.outputs.created_pr_url }}
            DRAFT_PR_NUMBER: ${{ needs.safe_outputs.outputs.created_pr_number }}
          with:
            github-token: ${{ steps.aspire-token.outputs.token }}
            script: |
              const fs = require('fs');
              const MARKER = '<!-- pr-docs-check:notify-source-pr -->';
              const SUMMARY_MAX = 2000;

              const outputPath = process.env.GH_AW_AGENT_OUTPUT;
              if (!outputPath || !fs.existsSync(outputPath)) {
                core.warning(`Agent output file not found at ${outputPath}; skipping comment.`);
                return;
              }

              let payload;
              try {
                payload = JSON.parse(fs.readFileSync(outputPath, 'utf8'));
              } catch (e) {
                core.warning(`Failed to parse agent output: ${e.message}`);
                return;
              }
              const items = (payload && Array.isArray(payload.items)) ? payload.items : [];
              const item = items.find(i => i && i.type === 'notify_source_pr');
              if (!item) {
                core.info('No notify_source_pr item in agent output; nothing to post.');
                return;
              }

              // Source PR number is supplied by the agent. Validate it as a
              // positive integer with a sane upper bound; the safe-jobs framework
              // does not pass workflow-context expressions through env: cleanly,
              // and threat detection has already gated this output.
              const agentNumber = parseInt(String(item.source_pr_number), 10);
              if (!Number.isInteger(agentNumber) || agentNumber <= 0 || agentNumber > 10_000_000) {
                core.warning(`Invalid source_pr_number from agent: ${item.source_pr_number}; skipping comment.`);
                return;
              }
              const sourcePrNumber = agentNumber;

              const result = (item.result || '').toString().trim().toLowerCase();
              const targetBranch = (item.target_branch || '').toString().trim();
              const draftUrl = (process.env.DRAFT_PR_URL || '').trim();
              const draftNumber = (process.env.DRAFT_PR_NUMBER || '').trim();

              // Bound the agent-supplied summary so a malformed item can't blow up the comment.
              let summary = (item.summary || '').toString().trim();
              if (summary.length > SUMMARY_MAX) {
                summary = summary.slice(0, SUMMARY_MAX) + '\n\n_(summary truncated)_';
              }

              let body;
              if (result === 'drafted' && draftUrl) {
                const branchSuffix = targetBranch ? ` targeting \`${targetBranch}\`` : '';
                const numberDisplay = draftNumber || '?';
                body = [
                  MARKER,
                  `📝 Documentation has been drafted in [microsoft/aspire.dev#${numberDisplay}](${draftUrl})${branchSuffix}.`,
                  '',
                  summary,
                  '',
                  '> [!NOTE]',
                  '> This draft PR needs human review before merging.'
                ].join('\n');
              } else if (result === 'drafted') {
                // Agent intended to draft a PR but the safe-outputs handler did not produce
                // a created_pr_url. Surface this as a failure rather than a "skipped" result.
                body = [
                  MARKER,
                  '⚠️ Documentation drafting was attempted but the draft PR could not be confirmed.',
                  '',
                  `See the workflow run for details: ${process.env.GITHUB_SERVER_URL}/${process.env.GITHUB_REPOSITORY}/actions/runs/${process.env.GITHUB_RUN_ID}`,
                  '',
                  summary
                ].join('\n');
              } else {
                body = [
                  MARKER,
                  '✅ No documentation update needed.',
                  '',
                  summary
                ].join('\n');
              }

              // Best-effort hide-older-comments: minimize prior comments on this PR that
              // carry our marker. Mirrors the framework's hide-older-comments behavior
              // for re-runs of the same workflow on the same PR.
              try {
                const existing = await github.paginate(github.rest.issues.listComments, {
                  owner: 'microsoft',
                  repo: 'aspire',
                  issue_number: sourcePrNumber,
                  per_page: 100,
                });
                for (const c of existing) {
                  if (c.body && c.body.includes(MARKER)) {
                    try {
                      await github.graphql(
                        `mutation($id: ID!) { minimizeComment(input: { subjectId: $id, classifier: OUTDATED }) { minimizedComment { isMinimized } } }`,
                        { id: c.node_id }
                      );
                    } catch (e) {
                      core.warning(`Failed to minimize comment ${c.id}: ${e.message}`);
                    }
                  }
                }
              } catch (e) {
                core.warning(`Failed to enumerate prior comments: ${e.message}`);
              }

              await github.rest.issues.createComment({
                owner: 'microsoft',
                repo: 'aspire',
                issue_number: sourcePrNumber,
                body,
              });
              core.info(`Posted ${result || 'unknown'} comment on microsoft/aspire#${sourcePrNumber}`);
        - name: Request SME review on draft PR
          if: needs.safe_outputs.outputs.created_pr_url != ''
          uses: actions/github-script@v9
          env:
            DRAFT_PR_NUMBER: ${{ needs.safe_outputs.outputs.created_pr_number }}
          with:
            github-token: ${{ steps.aspire-dev-token.outputs.token }}
            script: |
              const fs = require('fs');

              const outputPath = process.env.GH_AW_AGENT_OUTPUT;
              if (!outputPath || !fs.existsSync(outputPath)) {
                core.info('Agent output file not found; skipping reviewer request.');
                return;
              }
              let payload;
              try {
                payload = JSON.parse(fs.readFileSync(outputPath, 'utf8'));
              } catch (e) {
                core.warning(`Failed to parse agent output: ${e.message}`);
                return;
              }
              const items = (payload && Array.isArray(payload.items)) ? payload.items : [];
              const item = items.find(i => i && i.type === 'notify_source_pr');
              if (!item) {
                core.info('No notify_source_pr item; skipping reviewer request.');
                return;
              }
              const sme = (item.sme_login || '').toString().trim().replace(/^@/, '');
              if (!sme) {
                core.info('No SME login provided; leaving draft PR without an explicit reviewer.');
                return;
              }

              const draftNumber = parseInt(String(process.env.DRAFT_PR_NUMBER || ''), 10);
              if (!Number.isInteger(draftNumber) || draftNumber <= 0) {
                core.warning(`Invalid draft PR number: ${process.env.DRAFT_PR_NUMBER}`);
                return;
              }
              try {
                await github.rest.pulls.requestReviewers({
                  owner: 'microsoft',
                  repo: 'aspire.dev',
                  pull_number: draftNumber,
                  reviewers: [sme],
                });
                core.info(`Requested @${sme} as reviewer on microsoft/aspire.dev#${draftNumber}`);
              } catch (e) {
                // Best-effort: an SME may not be assignable on aspire.dev (no write access,
                // outside collaborator, etc.). Don't fail the job over this.
                core.warning(`Failed to request reviewer @${sme} on microsoft/aspire.dev#${draftNumber}: ${e.message}`);
              }

# The agent that follows used to resolve the target microsoft/aspire.dev
# branch itself (PR milestone -> linked-issue milestone -> PR base, then
# "does that release branch exist on aspire.dev?"). That worked only
# inconsistently because it depended on the model running the right git/gh
# commands and interpreting them correctly.
#
# This pre-agent step does the resolution deterministically before the
# agent starts and writes the result to .pr-docs-check/target.json. The
# agent reads that file verbatim and never re-derives the branch.
pre-agent-steps:
  # Mint a short-lived installation token from the aspire-bot GitHub App so
  # the resolver below can read PR/issue metadata from microsoft/aspire AND
  # list branches on microsoft/aspire.dev. The default GITHUB_TOKEN is scoped
  # to the repository running this workflow (microsoft/aspire) and cannot
  # reliably read branches of microsoft/aspire.dev, even though it is public —
  # cross-repo reads with the workflow token are blocked by GitHub's scoping
  # and can be further restricted by org policy.
  #
  # The same app installation is already trusted by this workflow's
  # `checkout:` block, `tools.github.github-app`, and `safe-outputs.github-app`
  # entries with `repositories: ["aspire.dev", "aspire"]`, so we just mint a
  # token with the same two repos here.
  - name: Mint app token for target-branch resolver
    id: resolve-target-app-token
    uses: actions/create-github-app-token@v3.1.1
    with:
      app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
      private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
      owner: microsoft
      repositories: |
        aspire
        aspire.dev
  - name: Resolve target aspire.dev branch
    env:
      GH_TOKEN: ${{ steps.resolve-target-app-token.outputs.token }}
      # event.pull_request.number is set on `pull_request: closed` triggers;
      # inputs.pr_number is set when a maintainer manually re-runs via
      # workflow_dispatch. The activation `if:` already guarantees one of
      # these is present.
      PR_NUMBER: "${{ github.event.pull_request.number || github.event.inputs.pr_number }}"
    run: |
      set -euo pipefail

      mkdir -p .pr-docs-check
      OUT=.pr-docs-check/target.json

      if [ -z "${PR_NUMBER}" ]; then
        echo "ERROR: PR_NUMBER is empty; cannot resolve target branch." >&2
        exit 1
      fi
      # PR_NUMBER reaches this script either from `github.event.pull_request.number`
      # (already an integer at the GitHub Actions layer) or from the
      # `workflow_dispatch` `pr_number` string input (free-form, supplied by a
      # maintainer). Reject anything that isn't a positive integer up front so
      # downstream `jq --argjson` and `gh api` calls produce a clear error
      # instead of an opaque parse failure.
      if ! [[ "${PR_NUMBER}" =~ ^[1-9][0-9]*$ ]]; then
        echo "ERROR: PR_NUMBER '${PR_NUMBER}' is not a positive integer." >&2
        exit 1
      fi

      echo "Resolving target microsoft/aspire.dev branch for microsoft/aspire#${PR_NUMBER}"
      # --- 1. Fetch source PR metadata from microsoft/aspire ---------------
      # We need the PR's own milestone, base branch, and body. The body is
      # later scanned for `Closes/Fixes/Resolves #N` linked-issue refs.
      PR_JSON="$(mktemp)"
      gh api "/repos/microsoft/aspire/pulls/${PR_NUMBER}" > "${PR_JSON}"

      PR_MILESTONE_TITLE="$(jq -r '.milestone.title // empty' "${PR_JSON}")"
      PR_BASE_REF="$(jq -r '.base.ref // empty' "${PR_JSON}")"
      PR_BODY="$(jq -r '.body // ""' "${PR_JSON}")"

      echo "PR milestone : '${PR_MILESTONE_TITLE}'"
      echo "PR base ref  : '${PR_BASE_REF}'"

      # --- 2. Extract linked issue numbers from the PR body ----------------
      # GitHub recognizes these closing keywords (case-insensitive), with an
      # optional `:` after the keyword, optional `owner/repo`, and a `#N`:
      #
      #   close, closes, closed,
      #   fix,   fixes,  fixed,
      #   resolve, resolves, resolved
      #
      # Examples it must accept:
      #   Fixes #123
      #   Fixes: #123
      #   Closes microsoft/aspire#456
      #   Resolves: microsoft/aspire#789
      #
      # See:
      # https://docs.github.com/en/issues/tracking-your-work-with-issues/linking-a-pull-request-to-an-issue#linking-a-pull-request-to-an-issue-using-a-keyword
      #
      # Only same-repo refs (no owner/repo, or `microsoft/aspire`) are kept;
      # cross-repo references in linked issues don't help us pick a docs
      # release branch.
      #
      # Implemented in python3 (preinstalled on ubuntu-latest) because POSIX
      # grep -E lacks reliable case-insensitive multiline matching with the
      # exact keyword set we need. We deliberately do NOT swallow parser
      # errors with `|| true`: if python3 fails (e.g., missing on the runner,
      # body too large to pass as argv), the resolver should fail loudly
      # rather than silently pick the wrong branch from an empty linked-issue
      # set.
      LINKED_FILE="$(mktemp)"
      : > "${LINKED_FILE}"
      python3 - "${PR_BODY}" > "${LINKED_FILE}" <<'PY'
      import re, sys
      body = sys.argv[1] if len(sys.argv) > 1 else ""
      pat = re.compile(
          r'\b(close[sd]?|fix(?:es|ed)?|resolve[sd]?)\s*:?\s+'
          r'(?:([A-Za-z0-9._-]+/[A-Za-z0-9._-]+))?#(\d+)\b',
          re.IGNORECASE,
      )
      seen = set()
      for m in pat.finditer(body):
          repo = (m.group(2) or "microsoft/aspire").lower()
          if repo != "microsoft/aspire":
              continue
          n = m.group(3)
          if n in seen:
              continue
          seen.add(n)
          print(n)
      PY

      LINKED_ISSUES=()
      while IFS= read -r n; do
        [ -n "${n}" ] && LINKED_ISSUES+=("${n}")
      done < "${LINKED_FILE}"
      rm -f "${LINKED_FILE}"

      if [ "${#LINKED_ISSUES[@]}" -gt 0 ]; then
        echo "Linked issues : ${LINKED_ISSUES[*]}"
      else
        echo "Linked issues : <none>"
      fi

      # --- 3. Fetch milestone for each linked issue ------------------------
      # Stop at the first non-empty milestone for the priority resolution
      # below, but record every issue's milestone (or null) for observability.
      LINKED_ISSUE_MILESTONE_TITLE=""
      LINKED_ARR=()
      for n in "${LINKED_ISSUES[@]:-}"; do
        [ -z "${n}" ] && continue
        ISSUE_JSON="$(mktemp)"
        # A linked-issue ref may be deleted, transferred, or otherwise
        # unreadable. Treat fetch failure as "no milestone" rather than
        # aborting the entire workflow.
        if gh api "/repos/microsoft/aspire/issues/${n}" > "${ISSUE_JSON}" 2>/dev/null; then
          m="$(jq -r '.milestone.title // empty' "${ISSUE_JSON}")"
        else
          m=""
          echo "  WARN: could not fetch microsoft/aspire#${n}; treating milestone as empty"
        fi
        rm -f "${ISSUE_JSON}"
        LINKED_ARR+=("$(jq -n --arg num "${n}" --arg ms "${m}" \
          '{number: ($num|tonumber), milestone: (if ($ms|length)>0 then $ms else null end)}')")
        if [ -z "${LINKED_ISSUE_MILESTONE_TITLE}" ] && [ -n "${m}" ]; then
          LINKED_ISSUE_MILESTONE_TITLE="${m}"
        fi
      done

      if [ "${#LINKED_ARR[@]}" -gt 0 ]; then
        LINKED_ISSUES_JSON="$(printf '%s\n' "${LINKED_ARR[@]}" | jq -s '.')"
      else
        LINKED_ISSUES_JSON="[]"
      fi

      # --- 4. Pick a candidate target branch -------------------------------
      # Priority (first match wins):
      #   1. PR milestone title (normalized)
      #   2. Linked-issue milestone title (normalized)
      #   3. PR base branch if it matches release/X.Y[.Z]
      #   4. main
      #
      # Milestone normalization: `^v?(\d+)\.(\d+)(?:\.(\d+))?` matched as a
      # prefix, so titles like "13.3", "v13.3", "13.3 - Preview 1" all map
      # to release/13.3, and "13.2.1" maps to release/13.2.1.
      normalize_milestone() {
        local title="$1"
        local m
        m="$(printf '%s' "${title}" | grep -oE '^v?[0-9]+\.[0-9]+(\.[0-9]+)?' | head -n1 || true)"
        if [ -z "${m}" ]; then
          return 1
        fi
        m="${m#v}"
        printf 'release/%s' "${m}"
      }

      CANDIDATE=""
      CANDIDATE_SOURCE=""
      CANDIDATE_SOURCE_DETAIL=""

      if [ -z "${CANDIDATE}" ] && [ -n "${PR_MILESTONE_TITLE}" ]; then
        if c="$(normalize_milestone "${PR_MILESTONE_TITLE}")"; then
          CANDIDATE="${c}"
          CANDIDATE_SOURCE="pr_milestone"
          CANDIDATE_SOURCE_DETAIL="${PR_MILESTONE_TITLE}"
        fi
      fi

      if [ -z "${CANDIDATE}" ] && [ -n "${LINKED_ISSUE_MILESTONE_TITLE}" ]; then
        if c="$(normalize_milestone "${LINKED_ISSUE_MILESTONE_TITLE}")"; then
          CANDIDATE="${c}"
          CANDIDATE_SOURCE="linked_issue_milestone"
          CANDIDATE_SOURCE_DETAIL="${LINKED_ISSUE_MILESTONE_TITLE}"
        fi
      fi

      if [ -z "${CANDIDATE}" ] && [[ "${PR_BASE_REF}" =~ ^release/[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
        CANDIDATE="${PR_BASE_REF}"
        CANDIDATE_SOURCE="pr_base"
        CANDIDATE_SOURCE_DETAIL="${PR_BASE_REF}"
      fi

      if [ -z "${CANDIDATE}" ]; then
        CANDIDATE="main"
        CANDIDATE_SOURCE="fallback_main"
        CANDIDATE_SOURCE_DETAIL="no milestone, linked-issue milestone, or release/* base ref resolved"
      fi

      echo "Candidate     : ${CANDIDATE} (source: ${CANDIDATE_SOURCE})"

      # --- 5. Enumerate release/* branches on microsoft/aspire.dev ---------
      # Primary: local git on the current workspace, which is checked out at
      # microsoft/aspire.dev with `release/*` refs fetched into
      # `refs/remotes/origin/release/*` via the workflow `checkout:` block.
      #
      # Fallback: `gh api /repos/microsoft/aspire.dev/branches` paginated.
      # Used if the local fetch produced nothing (e.g., no release branches
      # have been pushed yet, or the fetch silently failed). The GH_TOKEN
      # used here is the aspire-bot installation token minted at the top of
      # `pre-agent-steps`, which has explicit `contents: read` on both
      # microsoft/aspire and microsoft/aspire.dev — the default GITHUB_TOKEN
      # is scoped only to the workflow's own repo and cannot read aspire.dev's
      # branch list reliably.
      ENUMERATION_SOURCE=""
      RELEASE_BRANCHES_FILE="$(mktemp)"
      : > "${RELEASE_BRANCHES_FILE}"

      if git -C "${GITHUB_WORKSPACE}" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
        git -C "${GITHUB_WORKSPACE}" for-each-ref \
          --format='%(refname:short)' 'refs/remotes/origin/release/*' \
          | sed 's|^origin/||' > "${RELEASE_BRANCHES_FILE}" || true
      fi

      if [ -s "${RELEASE_BRANCHES_FILE}" ]; then
        ENUMERATION_SOURCE="git"
      else
        echo "Local git enumeration returned no release/* branches; falling back to gh api"
        if gh api --paginate "/repos/microsoft/aspire.dev/branches?per_page=100" \
            | jq -r '.[].name | select(startswith("release/"))' \
            > "${RELEASE_BRANCHES_FILE}" 2>/dev/null; then
          ENUMERATION_SOURCE="gh_api"
        else
          echo "  WARN: gh api fallback for aspire.dev branches failed; treating list as empty"
          : > "${RELEASE_BRANCHES_FILE}"
          ENUMERATION_SOURCE="empty"
        fi
      fi

      # De-duplicate and sort so the JSON output is deterministic across runs.
      sort -u -o "${RELEASE_BRANCHES_FILE}" "${RELEASE_BRANCHES_FILE}"

      AVAILABLE_BRANCHES_JSON="$(jq -R -s 'split("\n") | map(select(length > 0))' "${RELEASE_BRANCHES_FILE}")"
      echo "Release branches on aspire.dev (source=${ENUMERATION_SOURCE}):"
      if [ -s "${RELEASE_BRANCHES_FILE}" ]; then
        sed 's/^/  - /' "${RELEASE_BRANCHES_FILE}"
      else
        echo "  <none>"
      fi

      # --- 6. Compute the effective target branch --------------------------
      # Policy (in priority order):
      #   1. exact_match: CANDIDATE is a release/* branch that exists on
      #      aspire.dev → use it as-is.
      #   2. latest_release_fallback: aspire.dev has at least one release/*
      #      branch → use the highest-versioned one. This covers both:
      #        - CANDIDATE was `main` (no milestone/linked-issue/base-ref
      #          signal) — docs for upcoming-release work should still land
      #          on the staged release/* branch, not on aspire.dev's main.
      #        - CANDIDATE was a release/* (e.g. release/13.3) that no
      #          longer exists on aspire.dev (already shipped, branch
      #          deleted). The docs site only keeps a release/* branch for
      #          the upcoming release; older release-branch content is
      #          merged into main on aspire.dev as those releases ship.
      #   3. main_fallback: aspire.dev has no release/* at all → use main.
      #
      # Sort release branches with sort -V *after* stripping the
      # "release/" prefix so the numeric version compares cleanly:
      # "release/13.4" beats "release/9.5" (without -V, lexicographic
      # ordering would put 9.5 last; with -V on the bare versions, 13.4
      # comes last).
      LATEST_RELEASE=""
      if [ -s "${RELEASE_BRANCHES_FILE}" ]; then
        LATEST_RELEASE="$(
          sed -n 's|^release/||p' "${RELEASE_BRANCHES_FILE}" \
            | sort -V \
            | tail -n1 \
            | sed 's|^|release/|'
        )"
      fi

      EFFECTIVE=""
      RESOLUTION=""
      if [ "${CANDIDATE}" != "main" ] && grep -Fxq "${CANDIDATE}" "${RELEASE_BRANCHES_FILE}"; then
        EFFECTIVE="${CANDIDATE}"
        RESOLUTION="exact_match"
      elif [ -n "${LATEST_RELEASE}" ]; then
        EFFECTIVE="${LATEST_RELEASE}"
        RESOLUTION="latest_release_fallback"
        if [ "${CANDIDATE}" = "main" ]; then
          echo "Candidate was main; using latest aspire.dev release branch ${EFFECTIVE}"
        else
          echo "Candidate ${CANDIDATE} not present on microsoft/aspire.dev; using latest release branch ${EFFECTIVE} instead"
        fi
      else
        EFFECTIVE="main"
        RESOLUTION="main_fallback"
        echo "No release/* branches on microsoft/aspire.dev; falling back to main"
      fi

      rm -f "${RELEASE_BRANCHES_FILE}" "${PR_JSON}"

      echo "Effective     : ${EFFECTIVE} (resolution=${RESOLUTION})"

      # --- 7. Emit target.json ---------------------------------------------
      jq -n \
        --argjson pr_number "${PR_NUMBER}" \
        --arg pr_base_ref "${PR_BASE_REF}" \
        --arg candidate "${CANDIDATE}" \
        --arg candidate_source "${CANDIDATE_SOURCE}" \
        --arg candidate_source_detail "${CANDIDATE_SOURCE_DETAIL}" \
        --arg effective "${EFFECTIVE}" \
        --arg resolution "${RESOLUTION}" \
        --argjson available "${AVAILABLE_BRANCHES_JSON}" \
        --arg enumeration_source "${ENUMERATION_SOURCE}" \
        --argjson linked_issues "${LINKED_ISSUES_JSON}" \
        '{
           source_pr_number: $pr_number,
           source_pr_base_ref: $pr_base_ref,
           candidate_target_branch: $candidate,
           candidate_source: $candidate_source,
           candidate_source_detail: $candidate_source_detail,
           effective_target_branch: $effective,
           target_resolution: $resolution,
           available_release_branches: $available,
           enumeration_source: $enumeration_source,
           linked_issues: $linked_issues
         }' > "${OUT}"

      echo "--- ${OUT} ---"
      cat "${OUT}"
  # Compute deterministic "is this PR user-facing?" signals from the PR
  # diff and body before the agent starts. Historically the agent reasoned
  # about this directly from the prompt's prose ("is this a significant
  # user-facing change?"), and PRs like microsoft/aspire#16939 — which adds
  # a new `--search` CLI option, a new MCP tool input parameter, a new
  # dashboard API query parameter, and even an explicit "User-facing
  # usage" section in the PR body — slipped past as "no docs needed".
  #
  # This step writes `.pr-docs-check/signals.json` with a broad catalog
  # of boolean triggers derived from objective evidence: changed-file
  # path patterns, diff hunk contents (added or removed lines), PR body
  # regexes, and PR labels. Each signal targets a specific user-facing
  # concern an end user would need to be told about:
  #
  #   - Public API surface (additions, deprecations, breaking removals)
  #   - New shipping packages and new integrations
  #   - CLI commands, options/switches, and user-facing CLI strings
  #   - MCP tools and their input schema
  #   - Dashboard pages and dashboard HTTP API endpoints
  #   - Container image names / tags / digests (image version bumps
  #     that alter user expectations)
  #   - Project templates (`dotnet new aspire-*` / `aspire init`)
  #   - Roslyn analyzers and the shipped diagnostic catalog
  #   - Default values and shipping constants
  #   - Preview / experimental API markers
  #   - Target-framework changes
  #   - Integration READMEs (which ship to nuget.org)
  #   - Security advisories (CVE / GHSA / `security` label)
  #   - Breaking-change markers (PR body or label)
  #
  # The agent reads the file verbatim (it must not re-derive the
  # signals) and treats `recommendation == "docs_required"` as a hard
  # "draft a docs PR" gate. The catalog deliberately favors high
  # recall over precision: false positives fall into the Step 5
  # allowlist and the worst case is a drafted docs PR that a human
  # closes (drafted PRs never auto-merge), while a false negative
  # ships an undocumented user-facing change — the failure mode this
  # step exists to fix.
  #
  # The same robustness rationale applies as for `target.json` above:
  # this decision needs to be reliable across model versions, so it lives
  # in a deterministic shell step instead of relying on prompt judgment.
  # The catalog itself lives in a standalone Python script
  # (`.github/workflows/pr-docs-check/compute_signals.py`) with a
  # matching unittest suite (`test_compute_signals.py`), so it can be
  # reviewed with syntax highlighting and exercised locally with
  # `python3 -m unittest discover -s .github/workflows/pr-docs-check -v`.
  - name: Check out pre-agent scripts
    # The `checkout:` block above made microsoft/aspire.dev the current
    # workspace because that's where the doc PR is authored. We need a
    # sparse, side-by-side checkout of microsoft/aspire to bring the
    # pre-agent scripts (signal computation + PR context) into the runner.
    # A sparse checkout keeps this fast — only
    # `.github/workflows/pr-docs-check` is fetched.
    #
    # Default `ref` resolves to the trigger ref (refs/pull/<N>/merge for
    # pull_request: closed, or the dispatcher-selected branch for
    # workflow_dispatch). That's the correct version of the script for
    # the merged state being analyzed.
    uses: actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd # v6.0.2
    with:
      repository: microsoft/aspire
      path: _repos/aspire
      sparse-checkout: |
        .github/workflows/pr-docs-check
      sparse-checkout-cone-mode: false
  - name: Compute user-facing signals and PR context
    env:
      GH_TOKEN: ${{ steps.resolve-target-app-token.outputs.token }}
      PR_NUMBER: "${{ github.event.pull_request.number || github.event.inputs.pr_number }}"
    run: |
      set -euo pipefail

      mkdir -p .pr-docs-check
      OUT=.pr-docs-check/signals.json

      if [ -z "${PR_NUMBER}" ]; then
        echo "ERROR: PR_NUMBER is empty; cannot compute signals." >&2
        exit 1
      fi
      # Same input validation as the target-branch resolver above — reject
      # anything that isn't a positive integer so a bad `workflow_dispatch`
      # input fails loudly here rather than producing an opaque gh api error.
      if ! [[ "${PR_NUMBER}" =~ ^[1-9][0-9]*$ ]]; then
        echo "ERROR: PR_NUMBER '${PR_NUMBER}' is not a positive integer." >&2
        exit 1
      fi

      echo "Computing user-facing signals for microsoft/aspire#${PR_NUMBER}"

      # --- 1. Fetch PR metadata (body) and changed files (paths + patches) -
      # The files endpoint returns at most 30 files per page and is capped
      # at 300 files / 3000 lines total per the GitHub API; that's fine
      # for our purposes because signal detection only needs path patterns
      # and patch hunks, and any change large enough to overflow that cap
      # is almost certainly user-facing on path patterns alone. The
      # script also emits `diff_scan_skipped_due_to_missing_patch` when
      # the API omits a `patch` for a file matched by a diff trigger,
      # so very-large diffs gate conservatively.
      #
      # https://docs.github.com/en/rest/pulls/pulls#list-pull-requests-files
      PR_JSON="$(mktemp)"
      FILES_JSON="$(mktemp)"
      REVIEWS_JSON="$(mktemp)"
      gh api "/repos/microsoft/aspire/pulls/${PR_NUMBER}" > "${PR_JSON}"
      gh api --paginate "/repos/microsoft/aspire/pulls/${PR_NUMBER}/files?per_page=100" \
        | jq -s 'add // []' > "${FILES_JSON}"
      # Reviews drive SME resolution below. One paginated call here replaces a
      # GitHub tool round-trip the agent would otherwise make inside the loop.
      gh api --paginate "/repos/microsoft/aspire/pulls/${PR_NUMBER}/reviews?per_page=100" \
        | jq -s 'add // []' > "${REVIEWS_JSON}"

      FILE_COUNT="$(jq 'length' "${FILES_JSON}")"
      echo "Files in PR  : ${FILE_COUNT}"

      # --- 2. Run signal detection ----------------------------------------
      # The signal catalog lives in a standalone Python script (preinstalled
      # on ubuntu-latest) so a malformed regex fails the step loudly instead
      # of being silently absorbed by a shell pipeline, the catalog can be
      # reviewed with syntax highlighting, and it has its own unittest suite.
      python3 _repos/aspire/.github/workflows/pr-docs-check/compute_signals.py \
        "${PR_JSON}" "${FILES_JSON}" "${OUT}"

      # --- 3. Build compact PR context ------------------------------------
      # Reuse the same PR + files payloads (already fetched above) to write
      # .pr-docs-check/pr.json — the curated metadata the agent reads in
      # Step 1 instead of re-gathering it with several GitHub tool calls.
      # That gathering is fully deterministic, so doing it once here removes
      # those round-trips and the verbose API responses they add to context.
      # See compute_pr_context.py.
      PR_CONTEXT_OUT=.pr-docs-check/pr.json
      python3 _repos/aspire/.github/workflows/pr-docs-check/compute_pr_context.py \
        "${PR_JSON}" "${FILES_JSON}" "${PR_CONTEXT_OUT}"

      # --- 4. Resolve the subject-matter expert (SME) ---------------------
      # SME selection from assignees/reviews is a deterministic algorithm, so
      # it runs once here (reading the curated pr.json + reviews) instead of
      # inside the agent loop. The agent reads .pr-docs-check/sme.json in
      # Step 2. Only the fuzzy CODEOWNERS hint is left to the agent, signalled
      # via "needs_codeowners_fallback". See resolve_sme.py.
      SME_OUT=.pr-docs-check/sme.json
      python3 _repos/aspire/.github/workflows/pr-docs-check/resolve_sme.py \
        "${PR_CONTEXT_OUT}" "${REVIEWS_JSON}" "${SME_OUT}"

      rm -f "${PR_JSON}" "${FILES_JSON}" "${REVIEWS_JSON}"

      echo "--- ${OUT} ---"
      cat "${OUT}"
      echo "--- ${PR_CONTEXT_OUT} ---"
      cat "${PR_CONTEXT_OUT}"
      echo "--- ${SME_OUT} ---"
      cat "${SME_OUT}"

timeout-minutes: 20
---

# PR Documentation Check

Analyze a merged pull request in `microsoft/aspire` and determine whether documentation
updates are needed on the `microsoft/aspire.dev` documentation site. If updates are
needed, create a draft PR with the actual documentation changes.

## Context

- **Source repository**: `microsoft/aspire`
- **PR Number**: `${{ github.event.pull_request.number || github.event.inputs.pr_number }}`
- **PR Title**: `${{ github.event.pull_request.title }}`

> [!NOTE]
> The agent runs with `microsoft/aspire.dev` as the current workspace, so use
> GitHub tools for the source `microsoft/aspire` PR details and diff instead of
> expecting a local checkout of the merged PR contents to remain available.
>
> The target `microsoft/aspire.dev` branch (`release/X.Y[.Z]` or `main`) has
> already been resolved deterministically by a `pre-agent-steps:` shell step
> and written to `.pr-docs-check/target.json` in the workspace. **Do not**
> re-derive the target branch from milestones, linked issues, or the source
> PR base — read `effective_target_branch` from that file and use it verbatim.
>
> For security, this workflow only auto-activates for merged PRs whose head
> repository is `microsoft/aspire`. Unlike the old `pull_request_target` hook,
> fork-based PRs are intentionally excluded from automatic activation; use
> `workflow_dispatch` with `pr_number` when a maintainer wants to run the docs
> check manually for a merged fork PR.

## Step 1: Read PR Information

The source PR's metadata was gathered deterministically by a `pre-agent-steps:`
shell step and written to `.pr-docs-check/pr.json` in the workspace. **Read that
file** — do **not** re-fetch this metadata with GitHub tools. The fields you will
use are:

| Field | Purpose |
| --- | --- |
| `number`, `title`, `body` | Source PR identity and the author's own description of the change. |
| `author.login`, `author.type` | Author identity; `type` (`User`/`Bot`) drives Step 2's Copilot-authored branch. |
| `base_ref`, `milestone`, `labels` | Context for the change. |
| `assignees` | Used by Step 2 to find the SME for Copilot-authored PRs. |
| `linked_issues` | Same-repo issue numbers from `Closes`/`Fixes`/`Resolves #N` in the body. |
| `changed_files` | Each `{filename, status, additions, deletions}`. |

Diff hunks (`patch`) are intentionally omitted to keep this file small. Inspect a
file's diff only for files likely to affect user-facing behavior, configuration,
or public API surface (or when significance is unclear from the filename), and
only on the doc-drafting path — fetch the patch for that specific file with the
GitHub tools in Step 9. Do **not** fetch diffs on the cheap skip path.

**Defer the expensive comment-thread reads until you actually need them.** They
are only required when you are writing documentation (Step 9), so do **not**
fetch them on the cheap skip path. When — and only when — Step 5 puts you on the
docs-drafting path, fetch:

- **Issue conversation comments** (`GET /repos/microsoft/aspire/issues/{N}/comments`)
  — author intent, reviewer Q&A, follow-up clarifications, and any "this is how a
  user will experience it" prose that doesn't appear in the PR body.
- **Review comments** (`GET /repos/microsoft/aspire/pulls/{N}/comments`) — reviewer
  concerns about wording, default values, and error messages that affect what
  users see.

**Treat the PR description, the changed files, and (on the drafting path) the
PR/review comment threads together as the canonical context.** Steps 9 and 10
must paraphrase the explanation the author and reviewers wrote, so the docs
reflect the change as it was reviewed — not as a model might re-imagine it from
filenames. Step 11 must cite at least one piece of evidence per triggered signal
category, and the comment threads are often where that evidence lives in
human-readable form.

## Step 2: Identify the Subject-Matter Expert (SME)

The SME — the human best placed to review the drafted documentation PR — has
already been resolved deterministically by a `pre-agent-steps:` shell step
(`resolve_sme.py`) and written to `.pr-docs-check/sme.json`. **Read that file**;
do **not** re-fetch assignees or reviews or re-run the selection logic.

| Field | Meaning |
| --- | --- |
| `sme_login` | The chosen reviewer login (no `@`), or `""` if none could be resolved. |
| `sme_source` | How it was chosen — e.g. `copilot_originator` (the human who initiated the Copilot session), `approved_reviewer`, `substantive_reviewer`, or `none`. |
| `needs_codeowners_fallback` | `true` only when there was no usable assignee/review signal and you should consult CODEOWNERS as a last-resort hint. |
| `candidates` | The eligible reviewers (login + latest state), for context. |

Use `sme_login` directly as `SME_LOGIN`. **Only** when it is empty **and**
`needs_codeowners_fallback` is `true`, look at CODEOWNERS for the changed files
in `microsoft/aspire` and use the first individual login (skip team handles) as
a weak hint. If `sme_login` is empty and the fallback flag is `false`, leave
`SME_LOGIN` empty — the workflow drafts the PR without an explicit reviewer
rather than guess.

Capture the chosen login as `SME_LOGIN`. Do NOT include the `@` prefix. You will pass
this to the `notify_source_pr` safe output later.

## Step 3: Read the Pre-Resolved Target Branch

The target `microsoft/aspire.dev` branch was resolved before the agent started
by a deterministic `pre-agent-steps:` shell step. Its result is at
`.pr-docs-check/target.json` in the workspace. **Do not re-derive the target
branch from milestones, linked issues, or the source PR base** — those inputs
were already considered and the final answer is in this file.

Read `.pr-docs-check/target.json`. The fields you will use are:

| Field | Purpose |
| --- | --- |
| `effective_target_branch` | The branch you must base all docs edits and the draft PR on (`main` or `release/X.Y[.Z]`). |
| `candidate_source` | Why the candidate was chosen: `pr_milestone`, `linked_issue_milestone`, `pr_base`, or `fallback_main`. Use it in the PR description. |
| `candidate_source_detail` | The raw milestone title or base ref that drove the choice. Use it in the PR description. |
| `target_resolution` | How `effective_target_branch` was chosen: `exact_match`, `latest_release_fallback`, or `main_fallback`. Use it in the PR description. |

The remaining fields (`candidate_target_branch`, `available_release_branches`,
`enumeration_source`) are context only — don't second-guess the resolution.

The current workspace is `microsoft/aspire.dev`. Switch it to
`effective_target_branch` before editing docs:

- If `effective_target_branch` is `main`, you are already on the right branch
  by default; no switch is required.
- If `effective_target_branch` starts with `release/`, run
  `git checkout <effective_target_branch>` (the workflow `checkout:` block has
  already fetched `release/*` refs into `refs/remotes/origin/release/*`).

Do **not** create new branches or modify the resolution. The
`create_pull_request` safe output's `base` field must be set to exactly
`effective_target_branch`.

## Step 4: Read the Pre-Computed User-Facing Signals

Whether a docs PR is required is gated by a fixed catalogue of objective
signals computed by the `Compute user-facing signals` pre-agent step.
The result is at `.pr-docs-check/signals.json` in the workspace. **Do
not re-derive these signals from the diff or the PR body** — the file is
the source of truth, and the goal of this design is to make the
decision reproducible across model versions.

Read `.pr-docs-check/signals.json`. The fields you will use are:

| Field | Purpose |
| --- | --- |
| `recommendation` | `"docs_required"` if any gating signal fired, otherwise `"docs_optional"`. This is the primary gate for Step 5. |
| `triggered_signals` | The names of the boolean signals that fired (advisory `only_test_or_build_changes` is excluded from this list and never forces `docs_required`). |
| `signal_count` | `len(triggered_signals)`. |
| `signals` | The full boolean map, including the advisory `only_test_or_build_changes`. |
| `evidence` | Per-triggered-signal list of `{ file, hint }` entries showing the changed file and the matching diff fragment or PR-body snippet. Use these to write the PR description and the `notify_source_pr` summary. |

The catalog favors recall over precision: a false positive at worst
drafts a docs PR a human closes (drafted PRs never auto-merge), while a
false negative ships an undocumented user-facing change. You do **not**
need the full catalog definition here — `triggered_signals` and
`evidence` in `signals.json` tell you exactly which signals fired and
why, and the catalog itself is maintained in `compute_signals.py`.
Signal names are self-describing; they are grouped by source of evidence:

- **Group A — path-pattern** (which files changed): new/changed CLI
  commands, MCP tools, and CLI resource strings; new packages and
  hosting/client integrations; integration READMEs; public-API surface
  files (`src/*/api/*.cs`); dashboard pages; container image-tag files;
  project templates; the diagnostic catalog; analyzers; and
  `*Defaults.cs` / `*Constants.cs`.
- **Group B — diff-content** (what the patch added/removed): new CLI
  `Option<...>`; dashboard API endpoint changes;
  `[Obsolete]` / `[Experimental]` / `[DefaultValue]` attributes; new
  public types; breaking API removals from `api/*.cs`; container image
  version assignments; and target-framework changes.
- **Group C — PR-body**: a user-facing / usage / breaking-change heading,
  a long-form `--flag` mention, or breaking-change / security /
  deprecation prose.
- **Group D — PR-label**: a `breaking`-named or `security`-named label.
- **Advisory** `only_test_or_build_changes` (never gating; only narrows
  the Step 5 allowlist) and the conservative-recall gating fallback
  `diff_scan_skipped_due_to_missing_patch` (a Group B file whose `patch`
  the API omitted — treat as docs-required).

Before deciding in Step 5, **enumerate the triggered signals in your
internal reasoning** like:

> Triggered signals (5): `cli_command_added`, `cli_command_file_changed`, `cli_option_added`, `cli_resource_strings_changed`, `mcp_tool_file_changed`. Evidence: `LogsCommand.cs` is a new command file that adds `Option<string?>("--search")`; `LogsCommandStrings.resx` adds `SearchOptionDescription`; `ListConsoleLogsTool.cs` was modified to wire up the new search option.

This enumeration is not optional. The PR description you write in
Step 10 and the `summary` you emit in Step 11 must both cite at least
one `evidence` entry per triggered signal category so a human auditor
can verify the decision.

## Step 5: Decide Whether a Docs PR Is Required

The decision is driven by `recommendation` in `.pr-docs-check/signals.json`:

### When `recommendation == "docs_required"`

A documentation PR is **mandatory**. Proceed to Step 7 and beyond.

There is exactly one allowed exception, and it has a hard evidentiary bar.
You may switch to the `skipped` path **only** when every triggered signal
is already documented by name in the existing `microsoft/aspire.dev`
docs — that is, the docs already mention the specific new flag, option,
API, tool, integration, page, endpoint, or behavior that the signal
identifies. To use this exception you **must** do all of the following:

1. For each triggered signal, search `src/frontend/src/content/docs/`
   (the docs content tree) for the exact identifier from the signal's
   `evidence` (for example, the flag name `--search`, the resx key
   `SearchOptionDescription`, the JSON property name, the API symbol).
2. Open the matching docs file and quote a sentence or code block that
   mentions the identifier by name.
3. In the `notify_source_pr` `summary` (Step 11), include — per
   triggered signal — the docs file path **and** the quoted text. Plain
   statements like *"the existing docs cover this area"* or *"this is
   internal"* are not acceptable; the audit trail must show the
   identifier appears in the docs verbatim.

If you cannot meet this bar for **every** triggered signal, draft the
docs PR. Partial coverage is not enough — if only some of the new
surface is documented, the PR must cover the rest.

### When `recommendation == "docs_optional"`

No deterministic user-facing signal fired. A docs PR is still required
when the change matches any of these positive triggers that the
pre-step cannot detect mechanically:

- A user-visible behavior change to an already-documented feature
  (for example, default values, output formats, error messages, or
  exit codes that ship to users and that the docs describe).
- A new environment variable, configuration key, or connection-string
  field exposed through existing public surface.
- A new supported version, runtime target, or platform mentioned in a
  docs `prerequisites` list.
- A change to user-visible localization strings already referenced by
  the docs site.

Otherwise, the `skipped` path is allowed **only** when the change
falls cleanly into one of these explicit allowlist categories. Pick
the single best fit:

| Allowlist category | Definition |
| --- | --- |
| `test_only` | Only files under `tests/` changed (matches `only_test_or_build_changes` *and* no source files changed). |
| `build_or_ci_only` | Only files under `eng/`, `.github/`, `playground/`, or top-level build config (`.editorconfig`, `global.json`, `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`) changed. |
| `dependency_bump` | Only `Directory.Packages.props` (or equivalent package version files) changed and the change is purely a version bump with no behavior or surface change in this PR. |
| `internal_refactor` | The change touches `src/` but introduces no new or changed public types, methods, options, or strings. The PR title and body confirm this is purely internal. |
| `formatting_or_comment_only` | Pure typo fix, formatting, or code-comment-only change with no behavioral effect. |
| `bug_fix_restores_documented_behavior` | A bug fix that brings the implementation back in line with already-documented behavior. The docs already describe the intended behavior — the bug was the discrepancy. |
| `agent_or_skill_content` | Only files under `.agents/` changed (agent / skill content does not ship to `microsoft/aspire.dev`). |

If the change does not match exactly one of these categories, draft the
docs PR.

### Ambiguity rule

When the evidence is mixed or you are unsure, **draft the PR** (recall over
precision, as explained in Step 4). The drafted PR is in `draft:` state; it does
not merge until a human flips it out of draft.

## Step 6: Emit the No-Docs Outcome (only when Step 5 allowed it)

This step runs **only** when Step 5 produced an allowed `skipped`
result. Emit a single `notify_source_pr` safe output with:

- `source_pr_number`: the source PR number from Step 1.
- `result`: `"skipped"`.
- `sme_login`: `SME_LOGIN` from Step 2 (or an empty string if none was found).
- `summary`: a structured markdown rationale that proves the decision.
  It **must** include:
  1. The Step 5 branch you took, named explicitly: either
     `"docs_required → already documented by name"` *or*
     `"docs_optional → <allowlist_category>"` (use the category name
     from the table above).
  2. The list of triggered signals from `.pr-docs-check/signals.json`
     (or "no signals triggered" when `signal_count == 0`).
  3. For the `already documented by name` branch: the per-signal docs
     file path and quoted sentence/code block from Step 5.
  4. For the allowlist branch: the changed-file globs that justify the
     chosen category (for example, *"all 4 changed files match `tests/**`"*).

Then **stop**. Do **not** emit `create_pull_request` or any other safe
output on the no-docs path.

## Step 7: Read the doc-writer Skill

Read the file `.github/skills/doc-writer/SKILL.md` from the checked-out
`microsoft/aspire.dev` workspace. This skill contains comprehensive guidelines for
writing documentation on the Aspire docs site, including:

- Site structure and file organization
- Astro/MDX conventions and frontmatter requirements
- Required component imports (Starlight components)
- Formatting rules, code block conventions, and linking patterns

**You must follow all guidelines in the doc-writer skill when writing documentation.**

## Step 8: Browse Existing Documentation

Explore the existing documentation in `src/frontend/src/content/docs/` to:

- Identify pages that cover the affected feature area
- Confirm the documentation gap you identified in Step 5
- Determine whether existing pages need updates or new pages should be created
- Understand the current documentation structure, naming conventions, and patterns
- Find related pages that should be cross-referenced

**Keep this targeted.** Browse only the pages for the specific feature area the
triggering change touches — use the changed file paths and triggered signals to
narrow the search (e.g., grep the docs tree for the affected integration,
command, or API name). Do not read the whole docs tree or unrelated sections;
open at most a handful of candidate pages.

## Step 9: Write Documentation Changes

Based on your analysis, make the actual file changes in the workspace:

- **For updates to existing pages**: Edit the relevant `.mdx` files in place
- **For new pages**: Create new `.mdx` files in the appropriate directory following
  the doc-writer skill's conventions for frontmatter, imports, and structure

**Ground the documentation in the context you gathered in Step 1.** Specifically:

- Use the **source PR description** as the primary statement of what the
  change does and why. If the description includes "User-facing usage",
  "Breaking change", or example commands, those map directly to the
  user-oriented sections you write in the docs.
- Use the **list of changed files** (and their diff hunks for the
  surface-area files identified in Step 4's signals) to confirm the
  exact identifiers, flag names, option names, default values, and
  package IDs you cite in the docs. Never re-invent names; copy them
  verbatim from the diff.
- Use the **PR conversation and review comments** to capture nuance
  that doesn't appear in the PR body — reviewer-driven naming changes,
  follow-up clarifications about defaults, edge cases the author
  acknowledged, and any "we decided to do X for this reason" exchanges.
  These often answer the *why* questions a docs reader will ask.

If the PR description and comments contradict the diff (for example, the
body claims a flag is called `--search` but the resx says `--query`),
trust the diff for identifiers and ask the SME via the
`notify_source_pr` summary to clarify before merging the docs PR.

Keep the changes focused on the significant user-facing change that triggered this
workflow. Prefer updating the smallest correct set of pages over broad speculative edits.

Ensure all changes follow the doc-writer skill guidelines from Step 7. Include:
- Proper frontmatter (`title`, `description`)
- Required Starlight component imports
- Code examples where appropriate
- Cross-references to related documentation pages
- Correct use of Aside, Steps, Tabs, and other components

## Step 10: Create Draft PR

> [!IMPORTANT]
> Emit `create_pull_request` **exactly once**, and only after you have actually
> written documentation file changes to the workspace in Step 9. The safe output
> builds the PR from those workspace changes.
>
> **Treat any `create_pull_request` failure as non-retryable and never re-emit the
> same safe output after it.** Re-emitting after a deterministic error (no commits
> found, no diff to commit, an empty/invalid patch, a base-branch or validation
> error, a protected-file rejection, etc.) is a failure loop that burns the run's
> token budget without making progress. Handle a failure exactly once:
>
> - If it failed because you had not yet written any doc changes, write them now
>   (Step 9) and emit `create_pull_request` one more time — at most.
> - For any other failure, or if it still reports no changes to commit (the
>   triggering change has no concrete documentation edit you can make — for
>   example a signal fired on a string that is not actually an Aspire user-facing
>   feature), **stop drafting**. Emit a single `notify_source_pr` with
>   `result: "skipped"` whose `summary` states that no docs PR could be produced,
>   names the failure reason, and lists the triggered signals, then end the run.
>   Do not loop.

Create a draft pull request on `microsoft/aspire.dev` with:

**Base branch**: the `effective_target_branch` value from
`.pr-docs-check/target.json` (read in Step 3). When emitting the
`create_pull_request` safe output, set its `base` field to that exact string
(for example, `release/13.3`, `release/13.2.1`, or `main`). Do not derive or
modify this value.

**Title**: A clear, concise title describing the documentation work
(the `[docs]` prefix will be added automatically)

**Description** that includes:
- A prominent link to the source PR: `Documents changes from microsoft/aspire#<number>`
- The PR author mention: `@<author>`
- The target branch and how it was chosen, using `candidate_source`,
  `candidate_source_detail`, and `target_resolution` from
  `.pr-docs-check/target.json`. For example:
  - When `target_resolution` is `exact_match`: "Targeting `release/13.4`
    based on the source PR milestone `13.4`."
  - When `target_resolution` is `latest_release_fallback` and the candidate
    was a release branch: "Targeting `release/13.4` — the latest release
    branch on `microsoft/aspire.dev` — because `release/13.3` (from the
    source PR milestone `13.3`) does not exist there."
  - When `target_resolution` is `latest_release_fallback` and the candidate
    was `main`: "Targeting `release/13.4` — the latest release branch on
    `microsoft/aspire.dev` — because the source PR has no milestone or
    `release/*` base ref to derive a more specific target."
  - When `target_resolution` is `main_fallback`: "Falling back to `main`
    because `microsoft/aspire.dev` currently has no `release/*` branches."
- Why this PR is needed (the significant change and the docs gap it addresses)
- A summary of what documentation was added or changed
- A list of files modified or created
- Whether pages were updated or newly created

Do **not** include `reviewers` in the `create_pull_request` emission. The SME
identified in Step 2 is requested as a reviewer by the `notify_source_pr`
safe-output job, not by `create_pull_request`.

## Step 11: Notify Source PR

After emitting `create_pull_request`, emit a single `notify_source_pr` safe output
with:

- `source_pr_number`: the source PR number from Step 1.
- `result`: `"drafted"`.
- `sme_login`: `SME_LOGIN` from Step 2 (or an empty string if none was found).
- `target_branch`: the `effective_target_branch` value from
  `.pr-docs-check/target.json` (read in Step 3) — for example,
  `release/13.3` or `main`. Do not derive or modify this value.
- `summary`: a short markdown summary (1–3 sentences plus optional bullet list)
  of the documentation changes made. List the files modified or created. Do **not**
  describe links here — the workflow injects the drafted PR's URL automatically.

> [!IMPORTANT]
> Do **not** try to compose the drafted PR's URL or PR number yourself in the
> `summary` text. The `notify_source_pr` safe-output job knows the real values
> from the safe-outputs handler and will substitute them when posting the
> comment. Likewise, do **not** call `add_comment` for either the "drafted" or
> "skipped" path — `notify_source_pr` is the only commenting path used by this
> workflow.
