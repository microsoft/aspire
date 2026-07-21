import assert from "node:assert/strict";
import test from "node:test";

import {
  AGENT_ACTION_KINDS,
  buildAgentActionLog,
  buildAgentActionPrompt,
  isValidActionPr,
  normalizeActionPr,
} from "./agent.mjs";

const validPr = {
  repository: "microsoft/aspire",
  number: 123,
  title: "Add widget",
  author: "octocat",
  url: "https://github.com/microsoft/aspire/pull/123",
};

test("normalizeActionPr coerces every field to a trimmed string / integer", () => {
  const pr = normalizeActionPr({ repository: "  microsoft/aspire ", number: "123", title: " x ", author: " a ", url: " u " });
  assert.deepEqual(pr, { repository: "microsoft/aspire", number: 123, title: "x", author: "a", url: "u" });
});

test("isValidActionPr rejects malformed repository or non-positive number", () => {
  assert.equal(isValidActionPr(normalizeActionPr(validPr)), true);
  assert.equal(isValidActionPr(normalizeActionPr({ repository: "aspire", number: 1 })), false);
  assert.equal(isValidActionPr(normalizeActionPr({ repository: "a/b/c", number: 1 })), false);
  assert.equal(isValidActionPr(normalizeActionPr({ repository: "microsoft/aspire", number: 0 })), false);
});

test("buildAgentActionPrompt opens a sub-session with the right skill for test and review", () => {
  const testPrompt = buildAgentActionPrompt("test", validPr);
  assert.match(testPrompt, /open_pr_session/);
  assert.match(testPrompt, /\/pr-testing/);
  assert.match(testPrompt, /microsoft\/aspire#123/);
  assert.match(testPrompt, /pr_number: 123/);

  const reviewPrompt = buildAgentActionPrompt("review", validPr);
  assert.match(reviewPrompt, /open_pr_session/);
  assert.match(reviewPrompt, /\/code-review/);
});

test("buildAgentActionPrompt keeps resolve-conflicts and review-debt in the current session", () => {
  const conflicts = buildAgentActionPrompt("resolve-conflicts", validPr);
  assert.match(conflicts, /resolve the merge conflicts/i);
  assert.match(conflicts, /do not open a separate sub-session/i);

  const debt = buildAgentActionPrompt("review-debt", validPr);
  assert.match(debt, /review debt/i);
  assert.match(debt, /do not open a separate sub-session/i);
});

test("buildAgentActionPrompt reconstructs a github.com url when the descriptor url is untrustworthy", () => {
  const tampered = buildAgentActionPrompt("test", { ...validPr, url: "javascript:alert(1)" });
  assert.match(tampered, /https:\/\/github\.com\/microsoft\/aspire\/pull\/123/);
  assert.doesNotMatch(tampered, /javascript:/);
});

test("buildAgentActionPrompt throws on an unknown kind or an invalid PR", () => {
  assert.throws(() => buildAgentActionPrompt("nope", validPr), /Unknown card action/);
  assert.throws(() => buildAgentActionPrompt("test", { repository: "aspire", number: 1 }), /valid pull request/);
});

test("buildAgentActionLog produces a concise per-kind breadcrumb", () => {
  assert.equal(buildAgentActionLog("test", validPr), 'Test PR microsoft/aspire#123 \u2014 "Add widget"');
  assert.equal(buildAgentActionLog("review", validPr), 'Review PR microsoft/aspire#123 \u2014 "Add widget"');
  assert.equal(buildAgentActionLog("resolve-conflicts", validPr), 'Resolve merge conflicts on PR microsoft/aspire#123 \u2014 "Add widget"');
  assert.equal(buildAgentActionLog("review-debt", validPr), 'Address review on PR microsoft/aspire#123 \u2014 "Add widget"');
});

test("buildAgentActionLog omits the title when absent and stays valid for every known kind", () => {
  assert.equal(buildAgentActionLog("test", { ...validPr, title: "" }), "Test PR microsoft/aspire#123");
  for (const kind of AGENT_ACTION_KINDS) {
    assert.ok(buildAgentActionLog(kind, validPr).includes("microsoft/aspire#123"));
  }
});

test("buildAgentActionLog degrades gracefully for an invalid PR descriptor", () => {
  assert.equal(buildAgentActionLog("test", { repository: "", number: "x" }), "Test PR the pull request");
  assert.equal(buildAgentActionLog("review", { repository: "aspire", number: 0 }), "Review PR aspire");
});
