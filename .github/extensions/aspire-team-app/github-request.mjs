// Bound the entire GitHub request, including response-body reads, so a stalled provider
// cannot keep a dashboard load pending indefinitely.
//
// A stalled connection is usually transient: a live walk of microsoft/aspire saw one GraphQL
// page hang for over 30 minutes while the requests around it finished in seconds. Every caller
// sends read-only queries, so retry once on a fresh request before surfacing the failure.
const timeoutMs = 30_000;
const attempts = 2;

export async function throwGitHubResponseError(response, signal) {
  let payload;
  try {
    payload = await response.json();
  } catch (error) {
    if (signal.aborted) throw error;
  }
  const details = [];
  if (typeof payload?.message === "string" && payload.message.trim()) {
    details.push(payload.message.trim().slice(0, 500));
  }
  const retryAfter = response.headers?.get?.("retry-after");
  const remaining = response.headers?.get?.("x-ratelimit-remaining");
  const reset = response.headers?.get?.("x-ratelimit-reset");
  if (retryAfter) details.push(`Retry-After: ${retryAfter}`);
  if (remaining === "0") details.push("rate limit remaining: 0");
  if (reset && /^\d+$/.test(reset)) {
    const resetAt = new Date(Number(reset) * 1000);
    if (!Number.isNaN(resetAt.getTime())) details.push(`rate limit resets: ${resetAt.toISOString()}`);
  }
  const suffix = details.length ? ` - ${details.join("; ")}` : "";
  const error = new Error(`GitHub API ${response.status} ${response.statusText}${suffix}`);
  error.status = response.status;
  throw error;
}

export async function withGitHubTimeout(request) {
  for (let attempt = 1; ; attempt++) {
    const signal = AbortSignal.timeout(timeoutMs);
    try {
      return await request(signal);
    } catch (error) {
      const timedOut = signal.aborted;
      const connectTimedOut = error?.cause?.code === "UND_ERR_CONNECT_TIMEOUT";
      if ((timedOut || connectTimedOut) && attempt < attempts) continue;
      if (timedOut) {
        throw new Error("GitHub request timed out after 30 seconds. Try refreshing again.", { cause: error });
      }
      if (connectTimedOut) {
        throw new Error("Connection to GitHub timed out. Check network connectivity and try refreshing again.", { cause: error });
      }
      throw error;
    }
  }
}
