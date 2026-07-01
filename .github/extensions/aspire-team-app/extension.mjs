// Aspire Team App: a GitHub Copilot App canvas extension.
//
// Recreates the davidfowl/pr-dashboard cross-repo PR review queue for the
// logged-in GitHub user: Review / Issues / Ship modes, review lanes, signal
// pills, and notifications. The dashboard UI is served from a per-instance
// loopback server (see server.mjs); GitHub data and lane logic live in
// github.mjs; durable preferences in state.mjs.

import { joinSession, createCanvas, CanvasError } from "@github/copilot-sdk/extension";
import { startInstance, stopInstance, forceRefresh, getDashboard, rescanAccounts, toggleAccount, setReposFor } from "./server.mjs";
import { loadPrefs, savePrefs } from "./state.mjs";
import { accountId } from "./accounts.mjs";

function resolveAccountId(ref) {
  if (!ref) return null;
  return String(ref).startsWith("acct:") ? String(ref).toLowerCase() : accountId(ref);
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
