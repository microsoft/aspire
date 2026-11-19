import assert from "node:assert/strict";
import test from "node:test";

import { describeSafeError, genericFailureMessage } from "./safe-error.mjs";

test("describeSafeError never echoes the caught error text or stack", () => {
  const error = new Error("Command failed: gh api repos/microsoft/aspire/issues/1 C:\\Users\\secret\\token.txt");
  error.stderr = "internal stderr with /home/secret/path and a token ghp_example";

  const message = describeSafeError(error);

  assert.equal(message, genericFailureMessage);
  assert.doesNotMatch(message, /secret|token|ghp_example|Command failed/);
  assert.equal(describeSafeError(error, "custom fallback"), "custom fallback");
});

test("describeSafeError classifies known GitHub CLI failures into fixed messages", () => {
  const cases = [
    [Object.assign(new Error("spawn gh ENOENT"), { code: "ENOENT", syscall: "spawn gh" }), "The GitHub CLI (gh) was not found. Install it and try again."],
    [Object.assign(new Error("failed"), { stderr: "To get started with GitHub CLI, please run:  gh auth login" }), "The GitHub CLI is not authenticated. Run `gh auth login` and try again."],
    [Object.assign(new Error("failed"), { stderr: "gh: Bad credentials (HTTP 401)" }), "The GitHub CLI is not authenticated. Run `gh auth login` and try again."],
    [Object.assign(new Error("failed"), { stderr: "gh: API rate limit exceeded for user ID 1 (HTTP 403)" }), "GitHub rate limit reached. Try again later."],
    [Object.assign(new Error("failed"), { stderr: "gh: Must have admin rights to Repository. (HTTP 403)" }), "GitHub denied the request. Check your permissions for this repository."],
    [Object.assign(new Error("failed"), { stderr: "gh: Not Found (HTTP 404)" }), "GitHub could not find the requested issue or repository."],
  ];

  for (const [error, expected] of cases) {
    assert.equal(describeSafeError(error), expected);
  }
});

test("describeSafeError tolerates non-error values", () => {
  assert.equal(describeSafeError(undefined), genericFailureMessage);
  assert.equal(describeSafeError(null), genericFailureMessage);
  assert.equal(describeSafeError("a thrown string"), genericFailureMessage);
});
