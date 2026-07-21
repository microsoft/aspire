// Aspire Team App: a GitHub Copilot App canvas extension.
//
// Recreates the davidfowl/pr-dashboard cross-repo PR review queue for the
// logged-in GitHub user: Review / Issues / Ship modes, review lanes, signal
// pills, and notifications. The dashboard UI is served from a per-instance
// loopback server (see server.mjs); GitHub data and lane logic live in
// github.mjs; durable preferences in state.mjs.

import { joinSession, createCanvas, CanvasError } from "@github/copilot-sdk/extension";
import { startInstance, stopInstance, forceRefresh, getDashboard, rescanAccounts, toggleAccount, setReposFor, setAgentSend } from "./server.mjs";
import { loadPrefs, savePrefs } from "./state.mjs";
import { accountId } from "./accounts.mjs";

function resolveAccountId(ref) {
  if (!ref) return null;
  const value = String(ref).toLowerCase();
  if (value.startsWith("acct:")) {
    const accountRef = value.slice("acct:".length);
    return accountRef.includes("/") ? value : accountId(accountRef);
  }
  return accountId(value);
}

const session = await joinSession({
  canvases: [
    createCanvas({
      id: "aspire-team-app",
      displayName: "Aspire Team App",
      description:
        "Cross-repo PR review queue for the logged-in GitHub user: Review, Issues, and Ship modes with signal pills and notifications.",
      actions: [
        {
          name: "refresh",
          description: "Reload the review queue from GitHub and push the update to the open dashboard.",
          handler: async () => {
            const { dashboard } = await forceRefresh();
            return {
              mode: dashboard.mode,
              counts: dashboard.counts ?? null,
              authenticated: dashboard.authenticated,
            };
          },
        },
        {
          name: "set_mode",
          description: "Switch the dashboard mode.",
          inputSchema: {
            type: "object",
            properties: { mode: { type: "string", enum: ["review", "issues", "ship"] } },
            required: ["mode"],
          },
          handler: async (ctx) => {
            const mode = ctx.input?.mode;
            if (!["review", "issues", "ship"].includes(mode)) {
              throw new CanvasError("invalid_mode", "mode must be review, issues, or ship");
            }
            const prefs = await loadPrefs();
            prefs.mode = mode;
            await savePrefs(prefs);
            const { dashboard } = await forceRefresh();
            return { mode: dashboard.mode, counts: dashboard.counts ?? null };
          },
        },
        {
          name: "set_repos",
          description: "Replace the watched repositories for one account (comma or space separated, e.g. 'microsoft/aspire, CommunityToolkit/Aspire'). Targets the first active account unless 'account' (id or login) is given.",
          inputSchema: {
            type: "object",
            properties: { repos: { type: "string" }, account: { type: "string" } },
            required: ["repos"],
          },
          handler: async (ctx) => {
            let id = resolveAccountId(ctx.input?.account);
            if (!id) {
              const { dashboard } = await getDashboard(false);
              const first = (dashboard.activeAccounts ?? [])[0] ?? (dashboard.accounts ?? [])[0];
              id = first ? first.id : null;
            }
            if (!id) throw new CanvasError("no_account", "No account available to set repos for");
            const { dashboard } = await setReposFor(id, ctx.input?.repos ?? "");
            const acct = (dashboard.activeAccounts ?? []).find((a) => a.id === id)
              ?? (dashboard.accounts ?? []).find((a) => a.id === id);
            return { account: id, repos: acct ? acct.repos : [], counts: dashboard.counts ?? null };
          },
        },
        {
          name: "summary",
          description: "Return a text summary of the current review queue without opening the canvas.",
          handler: async () => {
            const { dashboard } = await getDashboard(false);
            if (!dashboard.authenticated) {
              return {
                authenticated: false,
                message: dashboard.message,
                accounts: (dashboard.accounts ?? []).map((a) => ({
                  id: a.id, login: a.login, sources: a.sourceKinds, status: a.status,
                  enterprise: !!a.enterprise, host: a.host ?? null,
                })),
              };
            }
            const c = dashboard.counts;
            return {
              authenticated: true,
              viewer: dashboard.viewer,
              viewers: dashboard.viewers ?? [dashboard.viewer],
              activeAccounts: (dashboard.activeAccounts ?? []).map((a) => ({
                id: a.id, login: a.login, sources: a.sourceKinds, status: a.status,
                enterprise: !!a.enterprise, host: a.host ?? null, repos: a.repos ?? [],
              })),
              mode: dashboard.mode,
              repos: dashboard.repos,
              counts: c,
              notifications: (dashboard.notifications ?? []).length,
            };
          },
        },
        {
          name: "accounts",
          description: "List every detected GitHub credential, whether it is active, and whether it can read its watched repos. Re-probes all accounts.",
          handler: async () => {
            const { accounts, activeAccounts } = await rescanAccounts();
            return {
              active: (activeAccounts ?? []).map((a) => a.id),
              accounts: accounts.map((a) => ({
                id: a.id, login: a.login, sources: a.sourceKinds, status: a.status,
                active: !!a.active, enterprise: !!a.enterprise, host: a.host ?? null,
                accessible: a.accessible, total: a.total, hasReadOrg: a.hasReadOrg,
                reason: a.reason ?? null,
              })),
            };
          },
        },
        {
          name: "set_account_active",
          description: "Activate or deactivate a GitHub account by its id (from the accounts action) or login. Active accounts are interleaved across every tab.",
          inputSchema: {
            type: "object",
            properties: { id: { type: "string" }, login: { type: "string" }, active: { type: "boolean" } },
            required: ["active"],
          },
          handler: async (ctx) => {
            const id = resolveAccountId(ctx.input?.id ?? ctx.input?.login);
            if (!id) throw new CanvasError("invalid_account", "id or login is required");
            const active = !!ctx.input?.active;
            const { dashboard } = await toggleAccount(id, active);
            return {
              authenticated: dashboard.authenticated,
              active: (dashboard.activeAccounts ?? []).map((a) => a.id),
              counts: dashboard.counts ?? null,
            };
          },
        },
      ],
      open: async (ctx) => {
        const entry = await startInstance(ctx.instanceId, (m) => session.log(m, { level: "debug" }));
        return { title: "Aspire Team App", url: entry.url, status: "Review queue" };
      },
      onClose: async (ctx) => {
        await stopInstance(ctx.instanceId);
      },
    }),
  ],
});

// Bridge card action buttons (Test / Review / Resolve conflicts / Address review) to
// the main session.
//
// Track whether the main session is mid-turn so a click can tell the user, truthfully,
// whether their request starts now or queues behind the current task. The agent goes
// busy at the start of an assistant turn and idle when the session settles. This is the
// only honest signal available: an extension cannot spawn an independent sub-session
// (that is an agent tool), so a queued prompt genuinely waits for the current turn.
let agentBusy = false;
// Sends that have entered setAgentSend but not yet reached session.send(). Incremented
// synchronously before the first await so two card clicks that arrive close together can't
// both read agentBusy===false while suspended in session.log() and each claim "starting now".
let sendsInFlight = 0;
session.on("assistant.turn_start", () => { agentBusy = true; });
session.on("session.idle", () => { agentBusy = false; });

// The loopback server builds the prompt plus a short log line and calls this to
// (1) drop a visible breadcrumb on the session timeline and (2) post the prompt as a
// user turn, so the agent opens the PR sub-session or does the interactive work. We
// snapshot the busy flag *before* sending — session.send queues behind an in-flight
// turn rather than interrupting it — and return it so the button labels itself
// "Queued …" vs "Sent" instead of always claiming success.
setAgentSend(async ({ prompt, log }) => {
  // Decide queued/starting *synchronously* here, before any await. agentBusy alone races:
  // when two actions fire nearly together, both suspend in the session.log() await below
  // before either calls session.send(), so both would read agentBusy===false. Also treat a
  // send already in flight as outstanding work the next click queues behind.
  const queued = agentBusy || sendsInFlight > 0;
  sendsInFlight++;
  try {
    if (log) {
      await session.log(`Aspire Team App \u2014 ${log} (${queued ? "queued; starts after the current task" : "starting now"})`);
    }
    const messageId = await session.send({ prompt });
    return { messageId, queued };
  } finally {
    sendsInFlight--;
  }
});
