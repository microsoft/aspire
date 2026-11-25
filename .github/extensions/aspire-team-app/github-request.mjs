// Mode changes queue behind the current refresh. Bound the entire GitHub request, including
// response-body reads, so a stalled provider cannot hold navigation indefinitely.
//
// A stalled connection is usually transient: a live walk of microsoft/aspire saw one GraphQL
// page hang for over 30 minutes while the requests around it finished in seconds. Every caller
// sends read-only queries, so retry once on a fresh request before surfacing the failure.
const timeoutMs = 30_000;
const attempts = 2;

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
