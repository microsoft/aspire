# Aspire Team App (canvas)

A GitHub Copilot App **canvas extension** that combines the
[`davidfowl/pr-dashboard`](https://github.com/davidfowl/pr-dashboard) cross-repo PR
review queue for the **logged-in GitHub user** with repository and delivery health
for watched GitHub repositories and Azure DevOps pipelines. This is
published as the first entry in the Canvas Marketplace.

## Screenshots

The cross-repo review queue — signal pills, a "For you" focus lane, and per-repo
lanes for everything waiting on you:

![Aspire Team App review queue](media/review-queue.png)

Per-card action buttons. On github.com PRs — including Enterprise Managed User (EMU)
accounts, whose PRs live on github.com — each button is a split-button dropdown to run
the action in a new sub-session (the PR's own repo) or in the current conversation. Only
GHES (self-hosted, non-github.com) cards can't open a sub-session, so they render a single
button with no dropdown that runs the action in the current conversation. The buttons a
card shows are driven by its lane and its signal pills:

- **Test** / **Review** — someone else's PR that's waiting on you, or any card carrying
  a "re-review" pill (the author pushed after a review). Each self-routes to the repo's
  matching skill (`/pr-testing`, `/code-review`), falling back to a thorough manual pass.
- **Address review** / **Discuss review** — any review-debt card (aged without an
  approving review; a PR that was only commented on or had changes requested still counts
  until it's approved), keyed off the "review debt" pill *and* the review-debt flag so it
  shows everywhere the card appears — Needs attention, *Your PRs outside Needs attention*,
  the breakdown lanes (e.g. Stalled), and Community — regardless of whether the PR is
  yours. Address review runs a fresh review; Discuss review talks through the existing
  feedback and lays out response options without rewriting anything.
- **Address feedback** / **Discuss review** — your own PR with changes requested (the
  "Author response" case, including in *Your PRs outside Needs attention*, which would
  otherwise show no actions). Address feedback works the requested changes; Discuss
  review talks them through and lays out options without rewriting anything.
- **Resolve conflicts** — any card carrying a "merge conflicts" signal; runs a
  direct git conflict-resolution flow.
- **Evaluate CI failures** — any card carrying a "CI failing" signal; self-routes to
  the repo's CI-diagnosis skill (`/ci-test-failures`) or diagnoses manually, then
  reports the failing checks, likely root cause, and a suggested fix.
- **Resolve** — any card with unresolved review threads, matched off every form of the
  pill: the "N unresolved" attention signal, the "Unresolved feedback" bucket / *Your PRs
  outside* reason label, and the "resolve feedback" action pill. Works each thread, makes
  the requested change, and resolves it.

![Card action split buttons](media/card-actions.png)

## What it does

- **Grid or rows** — use the **Dashboard layout** selector in the toolbar to switch
  between the existing card grid and full-width rows in Review, Issues, Ship, and
  Health. This changes presentation only: each card becomes one row with the
  same information, links, signals, and actions, in the same order. Section
  counts, display limits, and collapsed sections are unchanged; no items are
  added, removed, or re-sorted. Content wraps on narrow screens. The choice
  follows you across canvas reloads and sessions.
- **Review mode** — buckets every open PR across your watched repos into lanes:
  Needs your review, Ready to merge, CI failing, Unresolved feedback, and Your PRs.
- **Issues mode** — Assigned to you, Your issues, Needs triage, Recently active,
  with linked pull requests shown directly on issue cards and opened in in-app
  browser tabs.
- **Ship mode** — PRs in the current release milestone grouped into Ready to ship,
  In progress, and Blocked.
- **Health mode** — default-branch CI state, failure streak, last successful
  validation, days since success, failed checks/stages, and evidence-backed
  explanations for watched GitHub repositories and Azure DevOps delivery pipelines.
  Related provider sources are grouped by repository and each group can be dragged
  into a preferred order that persists across reloads.
- **Signal pills** — Draft, CI failing, Merge conflicts, Changes requested,
  N unresolved, Approved, Ready to merge, Needs review, Quick win, Stalled.
  Hover or keyboard-focus explanatory pills to see their meaning and expected next
  action; open-age and CI status tags stay plain. **Review debt** means the PR is
  not currently approved and its relevant activity is at least 14 days old,
  not that nobody has reviewed it. Raw GitHub
  labels are identified separately from computed dashboard signals.
- **Notifications** — review requested, your PR ready to merge, changes requested,
  CI failing, with per-category preferences. Live updates over SSE.
- **Stable background refresh** — GitHub data is assembled into a complete snapshot
  while the current board remains visible, then applied in one update. The compact
  **Auto** toolbar switch can pause automatic UI updates; when paused, an **Apply
  update** pill appears only after changed data is ready. The refresh button tooltip
  counts down to the next background data check.
- **Multiple GitHub accounts** — every detected credential (gh CLI, environment,
  Copilot) appears on the Accounts screen. Activate any number of them and their
  results **interleave across every tab**, de-duplicated by PR/issue URL. Each
  account watches **its own** repositories, editable inline.
- **Enterprise aware** — accounts on a GitHub Enterprise Server host are badged and
  their API calls are routed to that host's GraphQL/REST endpoints.
- **Editable watched repos** — per account; defaults to the public Aspire team set,
  except the `msft.ghe.com` enterprise account, which defaults to the Proxima
  `coreai/aspire-1p` and `coreai/sisyphus` repos. Defaults only fill in accounts
  you haven't configured — they never overwrite an explicit repo list.

## Needs attention order

Needs attention is numbered, left-to-right across each row, in ascending order of
**first-ever non-draft readiness**. A PR opened ready uses its creation time; a PR
opened as a draft uses its earliest ready-for-review event. Complete, paginated
draft/ready transition history distinguishes these cases, including PRs opened
ready that later returned to draft. Equal timestamps use ordinal host/repository
order, then PR number, independent of fetch completion and user locale.

Later draft cycles, comments, label changes, commits, and human or Copilot reviews
do not reset this priority. Existing eligibility rules still hide blocked PRs;
when eligible again, they return at their original first-readiness priority.
This is an approximation of reviewability: a PR marked ready on Monday but
conflicted until Thursday still precedes one first marked ready on Tuesday.
The canvas only reads GitHub; it does not persist queue state or write metadata.
If transition history is missing, inconsistent, or exceeds the pagination safety
limit, the PR is omitted from Needs attention with a visible error, not assigned
a guessed timestamp. A later refresh retries.

The main queue shows the first **10** eligible PRs (or all of them when fewer than
10 qualify), with no review-debt spillover or per-user limit override.
**Later in queue** shows the remaining count and starts collapsed. Expand it to
look up a PR's position, not to pick an alternative review pool: prioritize the
first 10 in Needs attention. This compact reference list has PR links but no
review-action buttons. Positions continue at 11 without gaps or duplicates and
keep their order at every window width. Expansion follows your choice during
refreshes, but starts collapsed when the canvas reloads.

The loader preserves the full eligible queue as `attention.focus` (positions
1–10), `attention.focusRemainder` (11 onward), and `attention.focusTotal`.
Other lanes, personal watched repositories, eligibility, and refresh preferences are unchanged.
Shared PRs have the same relative order for the same GitHub snapshot, but different
repository access, scopes, and refresh times can still produce different lists.

## Health mode

Health mode checks the default branch of every watched GitHub repository. When
`microsoft/aspire` is watched, it also checks the known `dnceng/internal`
`microsoft-aspire` mirror for the curated official CodeQL, NuGet release, and CI
pipelines. This first-party discovery does not depend on the Azure CLI's configured
defaults, excludes the unofficial pipeline, and quietly skips the internal sources
when the CLI, authentication, or repository access is unavailable.

Separately, the app checks the Azure CLI's configured default organization and
project for Azure Repos whose name uniquely matches a watched github.com repository.
For each unambiguous match, it auto-discovers one enabled delivery pipeline,
preferring production, release, deployment, or publish definitions over a generic
build. Merge, mirror, cleanup, provisioning, generated, old, unofficial, and disabled
definitions are ignored. The catalog is cached for ten minutes, and discovery never
scans other Azure DevOps projects or organizations.

Paste a pipeline URL such as
`https://dev.azure.com/{org}/{project}/_build?definitionId={id}` or a build-results
URL into **Settings → Azure DevOps pipelines** to monitor additional definitions.
A branch override is optional. Legacy `{org}.visualstudio.com` URLs are also
supported. Explicit and discovered definitions are de-duplicated by canonical
pipeline ID.

Azure DevOps access reuses the installed Azure CLI and `azure-devops` extension.
Authenticate with an existing `az` session or `AZURE_DEVOPS_EXT_PAT`. The app stores
only normalized pipeline coordinates and branch preferences; it never stores tokens
or other credentials. GitHub and Azure DevOps sources are loaded independently, so
an unavailable provider does not hide results from the other provider.
GitHub CLI discovery failures appear in Health's error bar without blocking
configured Azure pipelines. GitHub-only modes show an unauthenticated dashboard
with the discovery diagnostic while keeping Health and Settings accessible.

When a configured Azure pipeline builds a watched GitHub repository directly, provider
metadata links both sources into one repository group. Azure Repos mirrors do not expose
an upstream GitHub origin, so the app discovers and groups a mirror only when its
repository name has exactly one normalized GitHub match. Curated first-party sources
are labeled **Official default**; default-project matches are labeled
**Auto-discovered**. Both use a repository-name group, and ambiguous names remain
separate.

| State | Meaning |
| --- | --- |
| **Healthy** | The latest default-branch validation or pipeline build succeeded. |
| **Running** | Validation is queued, expected, or in progress. |
| **Degraded** | Azure DevOps reported a partially successful result or another non-fatal issue. |
| **Failing** | The latest validation failed, timed out, was cancelled, or requires action. |
| **Unknown / unavailable** | No CI signal exists, authentication is missing, or the source could not be queried. |

Days since success is an indicator, not an inactivity alarm: a quiet repository does
not become unhealthy solely because it has not received commits. Explanations are
deterministic and conservative. The app names failing checks, stages, jobs, or tasks;
reports failure streaks; identifies a Dependabot auto-merge as a **likely** regression
source only when GitHub exposes both bot authorship and auto-merge metadata; and calls
a deployment **likely blocked upstream** only when the same Azure DevOps timeline has
a failed build stage and a skipped deployment stage.

Health cards do not invoke AI during refresh. **Diagnose here** asks the current
session to refetch and investigate the canonical source. **Fix in repo** opens a
mapped github.com repository session; sources without a mapped GitHub repository use
**Work fix here** in the current session. The browser sends only the source ID, and
the loopback server resolves provider coordinates from its last complete snapshot.
Actions never trigger, retry, approve, or otherwise mutate CI/CD systems.

Drag a repository group from its grip to prioritize it, or focus the grip and use the
arrow keys. Every source in the group moves together. The app saves the flattened source
order in durable preferences and broadcasts changes to other open canvas instances
without refetching provider data.

## How it works

| File | Responsibility |
| --- | --- |
| `extension.mjs` | Wiring: `joinSession` + `createCanvas`, agent-facing actions. |
| `server.mjs` | Per-instance loopback HTTP server, atomic complete-snapshot cache, background polling, SSE refresh, multi-account interleave, and health orchestration. |
| `accounts.mjs` | Credential discovery, per-account repo-access probing, host/enterprise detection. |
| `cli.mjs` | Shared CLI execution with login-shell executable discovery for restricted desktop environments. |
| `github.mjs` | GraphQL queries, lane bucketing, signals, avatars, cross-account merge. |
| `health.mjs` | GitHub default-branch health and provider-neutral health aggregation. |
| `azure-devops.mjs` | Pipeline URL validation, read-only Azure CLI queries, build timelines, Azure health inference. |
| `model.mjs` | Attention buckets, focus queue, core-team / community classification. |
| `constants.mjs` | Configuration: core-team members, release milestone, personal picks. |
| `render.mjs` | Iframe HTML / CSS / client JS, styled with Copilot theme tokens. |
| `agent.mjs` | Card-action prompt/log builders (Test, Review, Resolve conflicts, Address review, Evaluate CI failures, Discuss review, Address feedback) with untrusted-PR hardening. |
| `health-agent.mjs` | Canonical health-action routing and injection-hardened diagnosis/fix prompts. |
| `state.mjs` | Durable preferences (watched repos, active accounts, notifications, refresh behavior, Azure pipeline coordinates, and health card order). |

The canvas reads each account's token from `GH_TOKEN` / `GITHUB_TOKEN`, the
per-account Copilot credentials, or `gh auth token`, and queries the matching
GitHub GraphQL API. Azure DevOps queries use the existing Azure CLI authentication.
No credential is written to the extension preferences.

On macOS and Linux, a standalone canvas launched with a restricted `PATH` falls
back to the user's login shell to locate `gh` and `az`, then uses their absolute
executables for GitHub account/token reads and Azure DevOps queries. Shell selection
uses `SHELL`, then the OS account's configured shell, then a platform default.
POSIX-style shells and Fish use their respective argument syntax. This reuses
existing CLI logins without relying on Copilot-injected credentials. Only executable
paths are discovered; the shell's environment is not imported. If discovery fails,
the canvas reports a CLI or shell configuration error rather than asking an
already-authenticated user to log in again. Copilot-only accounts still require
credentials supplied by the app.

## Agent actions

In **Settings → Agentic actions**, **Create detached sessions** is off by default.
Enable it to launch all new-session PR and Health actions as top-level sessions
instead of children of the canvas's session. Actions targeting the current
conversation (including GHES fallbacks) are unchanged.

Detached PR sessions use `create_session` and check out the live PR using `gh`,
instead of `open_pr_session`. They have no automatic report-back relationship
with the dashboard conversation, and automatic PR/issue association is not
guaranteed. Read results and answer questions in the launched session.
The preference follows the existing machine-local settings in
`$COPILOT_HOME/extensions/aspire-team-app/artifacts/preferences.json`
(`COPILOT_HOME` defaults to `~/.copilot`); it is not committed to the repository.

- `refresh` — reload the queue and push to the open dashboard.
- `set_mode` — switch to `review` / `issues` / `ship` / `health`.
- `set_repos` — replace the watched repositories for an account (targets the first
  active account unless `account` id/login is given).
- `set_account_active` — activate or deactivate an account by id/login; active
  accounts interleave across every tab.
- `add_azure_pipeline` — validate and add an Azure DevOps pipeline/build URL, with
  an optional branch override; the result includes opaque removal keys.
- `remove_azure_pipeline` — remove a configured Azure DevOps pipeline using a
  removal key returned by `add_azure_pipeline` or the Health summary.
- `accounts` — list every detected credential, its active state, and repo access.
- `summary` — return PR/issue counts or Health status counts and reasons without
  opening the canvas.

## Install

This extension ships in the repository under `.github/extensions/aspire-team-app/`, so
the GitHub Copilot app auto-discovers it as a **project extension** whenever this repo is
opened. No manual install step is required.

It is also published to the Canvas Marketplace and can be installed standalone:

```text
install_extension url: https://github.com/IEvangelist/canvas-marketplace/tree/main/canvases/aspire-team-app
```

Once loaded, open it from chat, or via `open_canvas` with `canvasId: "aspire-team-app"`.

The standalone canvas can also run from the top-level sidebar without an Aspire
repository session. It discovers stored `gh` accounts independently of tokens
injected by the Copilot app, and an expired account on another host does not hide
healthy accounts. The `gh` executable must be on the app's `PATH`; sign in with
`gh auth login` only if no usable credentials are available.
PR and Health agent-action buttons are disabled when the host does not provide an
active session working directory, including sidebar-only canvases. Hover or focus
the disabled action group to see why and how to enable it: open the canvas from a
session in the relevant repository. This applies to **new session** actions too:
the extension requests those through the owning conversation's agent, not a
direct project/session-creation API. Browsing, refresh, and settings remain available.

To preview this behavior from a normal repository session, open a fresh canvas
instance with the following input:

```json
{
  "previewSidebar": true
}
```

The override disables agent actions only for that instance, including server-side
dispatch. It is preserved when the canvas is rehydrated, does not change saved
preferences, and cannot enable actions in a sidebar-only canvas.

Older GitHub CLI versions without JSON authentication status use a text-status
fallback that retains accounts reported on either output stream.

On Windows, sidebar providers can omit `APPDATA`, causing `gh` to miss its saved
logins. If discovery finds no accounts and no explicit configuration location is
set, the canvas resolves the Windows application-data known folder and retries
using that location for both account discovery and token retrieval. Existing
`APPDATA`, `GH_CONFIG_DIR`, and `XDG_CONFIG_HOME` settings are preserved.
If that lookup fails, environment and Copilot credentials are still tried; the
lookup error is surfaced if none authenticate. Windows token variable names are
handled case-insensitively. `GH_ENTERPRISE_TOKEN` and `GITHUB_ENTERPRISE_TOKEN`
are also supported when `GH_HOST` names an enterprise host.

## Notes

- Notifications are **in-canvas + live in-session** (SSE). A canvas iframe is not a
  PWA with a service worker, so OS-level push while the app is closed is out of
  scope for v1.
- `copilot-extension.json` lets the folder install as a gist as well as a repo
  folder. `extension.mjs` must keep that exact filename.
