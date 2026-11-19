// Maps a caught error to a fixed message that is safe to return to the canvas browser page.
//
// Raw error text must never reach the HTTP response: `gh` stderr, Node error messages, and stack
// traces can include local paths, command lines, and API response bodies (CodeQL js/stack-trace-exposure).
// Every return value below is a string literal chosen by classifying the error, so no caught
// value flows into the response. Callers log the real error server-side before calling this.

export const genericFailureMessage = "The operation failed. See the Copilot session log for details.";

const classifiers = [
    {
        matches: (error) => error?.code === "ENOENT" && String(error?.syscall ?? "").startsWith("spawn"),
        message: "The GitHub CLI (gh) was not found. Install it and try again.",
    },
    {
        // Examples of `gh` output: "To get started with GitHub CLI, please run:  gh auth login",
        // "HTTP 401: Bad credentials (https://api.github.com/...)".
        matches: (error, text) => /gh auth login|not logged in|bad credentials|http 401/.test(text),
        message: "The GitHub CLI is not authenticated. Run `gh auth login` and try again.",
    },
    {
        // Example: "gh: API rate limit exceeded for user ID 1234 (HTTP 403)". Checked before the
        // generic 403 case because GitHub reports rate limiting with a 403 status as well.
        matches: (error, text) => /rate limit|http 429/.test(text),
        message: "GitHub rate limit reached. Try again later.",
    },
    {
        matches: (error, text) => /http 403|forbidden|saml|resource not accessible/.test(text),
        message: "GitHub denied the request. Check your permissions for this repository.",
    },
    {
        matches: (error, text) => /http 404/.test(text),
        message: "GitHub could not find the requested issue or repository.",
    },
];

export function describeSafeError(error, fallback = genericFailureMessage) {
    const text = `${error?.stderr ?? ""}\n${error?.message ?? ""}`.toLowerCase();

    for (const classifier of classifiers) {
        if (classifier.matches(error, text)) {
            return classifier.message;
        }
    }

    return fallback;
}
