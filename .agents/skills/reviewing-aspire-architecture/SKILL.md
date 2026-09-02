---
name: reviewing-aspire-architecture
description: "Use only when the user explicitly requests a deep Aspire architectural or pattern review of an existing PR or diff, or when a generic reviewer escalates a named Aspire-domain question it cannot resolve. Do not use for ordinary review, implementation, debugging, explanation, or design discussion—even when a PR is referenced—and never select it from changed file paths alone."
---

# Aspire Architectural Review

Aspire-specific architectural review via the `reviewing-aspire-architecture` agent. Catches domain patterns that generic `code-review` cannot.

## When to Use

**Use only when** at least one of these conditions is true:

- The user explicitly asks for a deep architectural review, Aspire pattern review, or equivalent domain-focused review of an existing PR or diff.
- A generic review has already found a concrete question that cannot be resolved without Aspire-specific architectural knowledge. The reviewer must state the unresolved question and why its normal review cannot answer it; invoke this skill with only that focused question. Do not escalate merely for additional confidence or a second opinion.

Changed file location is routing information after this skill is selected, not a reason to select it. A PR touching hosting core, Azure integrations, dashboard, CLI, components, resource types, the app model, or deployment behavior does not by itself qualify.

The user's primary requested action must be architectural review. Asking to understand, explain, compare, or design something does not qualify, even when the request references a PR or diff.

**Don't use** this skill for implementation, debugging, testing, routine code changes, post-change validation, design questions or brainstorming, explanatory investigation, generic PR review, doc/config-only PRs, CI failures, flaky tests, or API surface review. Use `code-review`, `ci-test-failures`, `fix-flaky-test`, or `api-review` where appropriate.

## Invocation Guard

- A parent may load this skill once and launch one `reviewing-aspire-architecture` agent for a user review request and change-set revision.
- If this skill context is already loaded, do not invoke the skill again.
- If the current agent is `reviewing-aspire-architecture`, do not invoke this skill or launch another `reviewing-aspire-architecture` agent.
- Continue follow-up work in the existing agent conversation. Do not launch a replacement agent for refinements or narrower follow-up questions.
- Completing or modifying code does not automatically trigger another architectural review. Re-run only when the user explicitly requests it for a materially changed diff.

## Relationship to code-review

`code-review` covers generic bugs, security, perf, concurrency, and error handling and is sufficient for ordinary PR reviews. This skill covers only checks requiring Aspire domain knowledge. Some topic areas (Security, Performance, Error Handling) exist in both, but the checks are disjoint: generic patterns belong to `code-review`, Aspire-specific patterns belong here.

Do not run this skill automatically after `code-review`. Escalate only a concrete unresolved Aspire-domain question, or run a full architectural pass when the user explicitly requests one.

## Review Scope

For an explicit full architectural review, inspect the PR context and only the dimensions selected by the changed-file routing below. For an escalation from another review, inspect only the supplied question, relevant diff, surrounding code, and existing comments needed to avoid duplication. Do not expand a focused escalation into a full PR review or repository-wide inventory.

## Folder → Dimension Routing

| Folder | Dimensions |
|---|---|
| `src/Aspire.Hosting/**` | Resource Model, API Design, Pattern Conformance, Containers |
| `src/Aspire.Hosting.Azure*/**` | Azure Provisioning, Resource Model, API Design, Security |
| `src/Aspire.Dashboard/**` | Dashboard UI/UX, Security, Performance |
| `src/Aspire.Cli/**` | CLI Behavior, Error Handling, Platform Compatibility |
| `src/Components/**` | Pattern Conformance, API Design, Build & Contributor Workflow |
| `tests/**` | Test Quality + mirror dimensions of code under test |
| `eng/**`, `.github/**` | Build & Contributor Workflow, Documentation & Naming |

Full review rules in the `reviewing-aspire-architecture` agent.
