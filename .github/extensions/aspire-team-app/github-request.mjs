// Mode changes queue behind the current refresh. Bound the entire GitHub request, including
// response-body reads, so a stalled provider cannot hold navigation indefinitely.
export async function withGitHubTimeout(request) {
  const signal = AbortSignal.timeout(30_000);
  try {
    return await request(signal);
  } catch (error) {
    if (signal.aborted) {
      throw new Error("GitHub request timed out after 30 seconds. Try refreshing again.", { cause: error });
    }
    if (error.cause?.code === "UND_ERR_CONNECT_TIMEOUT") {
      throw new Error("Connection to GitHub timed out. Check network connectivity and try refreshing again.", { cause: error });
    }
    throw error;
  }
}
