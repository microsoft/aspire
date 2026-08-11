---
name: reviewing-aspire-architecture
description: "Triggers deep architectural review across 15 Aspire-specific dimensions. Activated by requests for deep review, architectural review, pattern review, or PRs touching hosting core, Azure integrations, dashboard, CLI, or components."
---

# Aspire Architectural Review

Aspire-specific architectural review via the `reviewing-aspire-architecture` agent. Catches domain patterns that generic `code-review` cannot.

## When to Use

**Use**: "deep review," "architectural review," "pattern review," PRs touching hosting core, Azure integrations, dashboard, CLI, components, new resource types, app model changes, deployment behavior.

**Don't use**: doc/config-only PRs → `code-review` · generic PR review → `code-review` · CI failures → `ci-test-failures` · flaky tests → `fix-flaky-test` · API surface → `api-review`

## Relationship to code-review

`code-review` covers generic bugs, security, perf, concurrency, and error handling. This skill covers only checks requiring Aspire domain knowledge. Some topic areas (Security, Performance, Error Handling) exist in both, but the checks are disjoint: generic patterns belong to `code-review`, Aspire-specific patterns belong here. Run `code-review` first for breadth, then this for domain depth.

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
