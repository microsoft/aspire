'use strict';

const DEFAULT_ATTEMPTS = 3;
const DEFAULT_RETRY_DELAY_MS = 5000;
// ExTester's root `before all` hook allows 180 s for session creation plus the 30 s workbench wait.
// ChromeDriver can spend 60 s before reporting `DevToolsActivePort file doesn't exist`, so only
// start another attempt while one more slow failure still fits in that hook timeout.
const DEFAULT_RETRY_BUDGET_MS = 90000;

/**
 * Creates the ExTester WebDriver session, retrying transient ChromeDriver startup failures.
 *
 * ChromeDriver launches VS Code and then attaches to its first renderer. On busy CI runners that
 * intermittently fails with `SessionNotCreatedError` (`unable to connect to renderer` or
 * `DevToolsActivePort file doesn't exist`), or with `ECONNREFUSED` (Selenium prefixes the
 * socket error code to the message) when ChromeDriver is not listening yet. ExTester builds the session once inside the root `before all` hook, so a single
 * failure fails the whole shard before any test runs. Selenium stops the ChromeDriver service when
 * session creation fails, and the delay gives the VS Code it launched time to exit before the next
 * attempt reuses the same user data directory.
 */
async function createSessionWithRetry(createSession, options = {}) {
  const attempts = options.attempts ?? DEFAULT_ATTEMPTS;
  const retryDelayMs = options.retryDelayMs ?? DEFAULT_RETRY_DELAY_MS;
  const retryBudgetMs = options.retryBudgetMs ?? DEFAULT_RETRY_BUDGET_MS;
  const now = options.now ?? Date.now;
  const log = options.log ?? (message => console.warn(message));
  const startedAt = now();

  for (let attempt = 1; ; attempt++) {
    try {
      return await createSession();
    }
    catch (error) {
      if (attempt >= attempts || !isTransientSessionStartupError(error) || now() - startedAt + retryDelayMs > retryBudgetMs) {
        throw error;
      }

      log(`WebDriver session creation failed on attempt ${attempt}/${attempts}; retrying in ${retryDelayMs} ms: ${error.message}`);
      await new Promise(resolve => setTimeout(resolve, retryDelayMs));
    }
  }
}

function isTransientSessionStartupError(error) {
  return error instanceof Error && (error.name === 'SessionNotCreatedError' || /^ECONNREFUSED\b/.test(error.message));
}

module.exports = {
  createSessionWithRetry,
  isTransientSessionStartupError,
};
