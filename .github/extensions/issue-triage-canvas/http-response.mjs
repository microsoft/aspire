// Response helpers for the issue triage canvas server. They live outside extension.mjs because that
// entrypoint calls joinSession at module scope and cannot be imported by tests, and the error paths
// below are the ones that must be verified against leaking raw error text to the browser.

import { describeSafeError } from "./safe-error.mjs";

export function jsonResponse(res, statusCode, body) {
    res.writeHead(statusCode, {
        "Content-Type": "application/json; charset=utf-8",
        "Cache-Control": "no-store",
    });
    res.end(JSON.stringify(body));
}

// Logs the full error server-side, then answers with a fixed message only. `log` receives the
// message and options of copilotSession.log; it is injected so tests can observe it.
export async function respondWithError(res, error, log) {
    await log(`Issue triage canvas error: ${String(error?.stderr || error?.message || error)}`, { level: "error", ephemeral: true });

    if (!res.headersSent) {
        jsonResponse(res, 500, { error: describeSafeError(error) });
        return;
    }

    if (!res.writableEnded) {
        res.end();
    }
}

export async function toCloseFailure(number, error, log) {
    await log(`Issue triage failed to close #${number}: ${String(error?.stderr || error?.message || error)}`, { level: "warning", ephemeral: true });

    return {
        number,
        error: describeSafeError(error, "The issue could not be closed. See the Copilot session log for details."),
    };
}
