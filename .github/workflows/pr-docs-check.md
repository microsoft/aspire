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

checkout:
  # Use aspire.dev as the current workspace because that is where documentation
  # changes are authored, and keep a mirrored checkout under _repos so the
  # safeoutputs create_pull_request tool can reliably rediscover the target repo
  # in multi-repo mode.
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
  - repository: microsoft/aspire.dev
    path: _repos/aspire.dev
    github-app:
      app-id: ${{ secrets.ASPIRE_BOT_APP_ID }}
      private-key: ${{ secrets.ASPIRE_BOT_PRIVATE_KEY }}
      owner: "microsoft"
      repositories: ["aspire.dev"]

permissions:
  contents: read
  pull-requests: read
  issues: read

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
> The agent runs with `microsoft/aspire.dev` as the current workspace and also
> has a mirrored checkout at `_repos/aspire.dev`, so use GitHub tools for the
> source `microsoft/aspire` PR details and diff instead of expecting a local
> checkout of the merged PR contents to remain available.
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

## Step 1: Gather PR Information

Use the GitHub tools to read the pull request details from `microsoft/aspire` for the
PR number above, including:

- Title and description
- Author username
- Base branch (e.g., `main` or `release/X.Y`)
- Milestone (if any)
- Any issues linked via `Closes #N` / `Fixes #N` / `Resolves #N` in the PR body
- The list of changed files

Start with the PR metadata and changed-file list. Only inspect diff hunks for files that
are likely to affect user-facing behavior, configuration, or public API surface, or when
the significance is unclear from filenames alone.

If this was triggered via `workflow_dispatch`, use the `pr_number` input to look up
the PR details.

## Step 2: Identify the Subject-Matter Expert (SME)

Determine which human is the best fit to review the drafted documentation PR. The
SME is the person most familiar with the change in the source `microsoft/aspire`
PR — typically the human who reviewed/approved it, except when the PR was
authored by GitHub Copilot Coding Agent (in which case the SME is the human who
**initiated the Copilot session**, not whoever happened to approve the bot's
output).

### Step 2a: If the source PR was authored by Copilot Coding Agent

Fetch the source PR's `user.login` and `user.type`. If the PR was authored by a
Copilot bot — that is, `user.type == "Bot"` AND `user.login` matches `Copilot`,
`copilot-swe-agent`, or any login containing `copilot` and ending in `[bot]` —
then the **human session originator** (the person who assigned `@copilot` to
an issue and therefore initiated the session) is recorded in the PR's
`assignees[]` field alongside the `Copilot` bot itself. This person is the SME
because they framed the original problem and have the deepest context for the
change, even though they didn't author the code.

Apply the following logic:

1. Read `pull_request.assignees[]` from the source PR.
2. Filter out bot accounts: any login matching `Copilot`, `copilot-swe-agent`,
   anything ending in `[bot]`, or matching `dependabot`, `github-actions`,
   `aspire-bot`.
3. If exactly one human assignee remains, set `SME_LOGIN` = that login and
   **skip the rest of Step 2**. That person initiated the Copilot session and
   is the subject-matter expert.
4. If multiple human assignees remain, prefer the assignee whose latest review
   state on the source PR is `APPROVED`. If still ambiguous, pick the one whose
   login appears earliest in `assignees[]`. **Skip the rest of Step 2.**
5. If no human assignees remain (unusual — Copilot Coding Agent normally
   assigns the originator), fall through to Step 2b.

### Step 2b: For human-authored PRs (or as a fallback from Step 2a)

Use the GitHub tools to list pull request reviews for the source PR
(`GET /repos/microsoft/aspire/pulls/{N}/reviews`) and apply the following logic:

1. **Collapse reviews by reviewer.** For each unique reviewer login, keep only their
   *most recent* review event (the latest `submitted_at`).
2. **Exclude** the source PR author and any bot account (login ending in `[bot]`,
   or matching `dependabot`, `github-actions`, `aspire-bot`, `copilot`, etc.).
3. **Prefer** reviewers whose latest collapsed state is `APPROVED`. Among those,
   pick the one with the most recent `submitted_at`.
4. **Fallback A**: if no `APPROVED` reviewer exists, pick the reviewer with any
   non-`COMMENTED`-only state (for example, `CHANGES_REQUESTED`) whose latest
   `submitted_at` is most recent.
5. **Fallback B**: if no reviews exist at all, look at CODEOWNERS for the changed
   files in `microsoft/aspire` and use the first individual login (skip team
   handles). Treat this as a hint, not a strong signal.
6. **Final fallback**: leave `SME_LOGIN` empty (the workflow will draft the PR
   without an explicit reviewer rather than guess).

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
| `candidate_target_branch` | The branch the resolution *wanted* before checking existence on `microsoft/aspire.dev`. |
| `candidate_source` | Why `candidate_target_branch` was chosen: `pr_milestone`, `linked_issue_milestone`, `pr_base`, or `fallback_main`. |
| `candidate_source_detail` | The raw milestone title or base ref that drove the choice (use this in the PR description). |
| `target_resolution` | How `effective_target_branch` was finally chosen: `exact_match` (candidate exists on `microsoft/aspire.dev`), `latest_release_fallback` (candidate was `main` or missing on `microsoft/aspire.dev`, so the newest `release/*` was used), or `main_fallback` (no `release/*` exists on `microsoft/aspire.dev`). |
| `available_release_branches` | The full list of `release/*` branches that exist on `microsoft/aspire.dev` (for context only — don't second-guess the resolution). |
| `enumeration_source` | How the list was obtained (`git` for the local workspace, `gh_api` for the API fallback, or `empty`). |

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

## Step 4: Detect Significant Changes and Decide Whether a Docs PR Is Required

Review the PR metadata and candidate diffs for **significant user-facing changes**.

A docs PR is required only when **both** of these are true:

1. The PR introduces a significant user-facing change.
2. The current `microsoft/aspire.dev` documentation does **not** already cover that
   change well enough.

For each candidate docs-worthy change, identify:

- Evidence from the PR (changed files, APIs, commands, options, configuration, or
  behavior)
- Who is affected (app developers, AppHost authors, CLI users, dashboard users, etc.)
- The likely docs surface area (`get-started`, integration guide, reference page,
  command docs, migration guidance, and so on)

**Changes that are usually significant enough to consider documentation:**
- New public APIs: methods, classes, interfaces, or extension methods
- New features or capabilities: hosting integrations, client integrations, CLI commands,
  or dashboard features
- Breaking changes: removed or renamed APIs, behavioral changes, migration needs
- New configuration options: settings, environment variables, or parameters
- New resource types: Aspire hosting resources or cloud service integrations
- Significant behavioral changes: service discovery, health checks, telemetry, or deployment

**Changes that usually do NOT require a docs PR:**
- Internal refactoring with no public API surface changes
- Test-only changes
- Build/CI infrastructure changes
- Bug fixes that don't change documented behavior
- Dependency version bumps
- Code style or formatting changes

Do not create a docs PR for minor or already-understood changes just because they touch a
docs-adjacent area. If the change is small, internal, or already covered by existing docs,
treat it as **no docs PR required**.

## Step 5: If No Docs PR Is Required

If you determine that no docs PR is required because the change is not significant or the
existing docs already cover it sufficiently:

1. Emit a single `notify_source_pr` safe output with:
   - `source_pr_number`: the source PR number from Step 1.
   - `result`: `"skipped"`.
   - `sme_login`: `SME_LOGIN` from Step 2 (or an empty string if none was found).
   - `summary`: a short markdown rationale (1–3 sentences). For example:
     `"Test/build changes only — existing docs already cover this behavior."`.
2. **Stop here** — do not proceed to the remaining steps. Do **not** emit
   `create_pull_request` or any other safe output for the no-docs path.

## Step 6: Read the doc-writer Skill

Read the file `.github/skills/doc-writer/SKILL.md` from the checked-out
`microsoft/aspire.dev` workspace. This skill contains comprehensive guidelines for
writing documentation on the Aspire docs site, including:

- Site structure and file organization
- Astro/MDX conventions and frontmatter requirements
- Required component imports (Starlight components)
- Formatting rules, code block conventions, and linking patterns

**You must follow all guidelines in the doc-writer skill when writing documentation.**

## Step 7: Browse Existing Documentation

Explore the existing documentation in `src/frontend/src/content/docs/` to:

- Identify pages that cover the affected feature area
- Confirm the documentation gap you identified in Step 4
- Determine whether existing pages need updates or new pages should be created
- Understand the current documentation structure, naming conventions, and patterns
- Find related pages that should be cross-referenced

## Step 8: Write Documentation Changes

Based on your analysis, make the actual file changes in the workspace:

- **For updates to existing pages**: Edit the relevant `.mdx` files in place
- **For new pages**: Create new `.mdx` files in the appropriate directory following
  the doc-writer skill's conventions for frontmatter, imports, and structure

Keep the changes focused on the significant user-facing change that triggered this
workflow. Prefer updating the smallest correct set of pages over broad speculative edits.

Ensure all changes follow the doc-writer skill guidelines from Step 6. Include:
- Proper frontmatter (`title`, `description`)
- Required Starlight component imports
- Code examples where appropriate
- Cross-references to related documentation pages
- Correct use of Aside, Steps, Tabs, and other components

## Step 9: Create Draft PR

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

## Step 10: Notify Source PR

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
