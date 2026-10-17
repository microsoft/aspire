---
applyTo: "tests/Infrastructure.Tests/**"
---

# Infrastructure.Tests authoring and review guidance

## Core rule

Do not assert YAML source text. Assert durable workflow semantics from parsed YAML. Extract non-trivial imperative logic into executable files and test its behavior, while retaining focused YAML tests for the wiring contract.

Each assertion should fail for the exact regression it protects. When a brittle source-pinning test breaks, convert it to a semantic or behavior test, or delete it if it protects no durable contract.

## YAML contracts

- A parsed-YAML assertion is useful when valid but wrong YAML could silently skip work, weaken a gate or security boundary, route the wrong artifact or RID, or leave independently maintained workflow and configuration sources inconsistent.
- Prioritize relationships: producer-consumer wiring, selection-to-job mapping, final-result aggregation, reusable-workflow interfaces, permissions, conditions, artifact flow, and ordering only when order affects execution.
- Prefer deriving expectations from another source of truth or checking relationships across nodes or files. Avoid restating one field's exact value or list unless it is a named, high-consequence policy contract.
- Test immutable action pins and security-sensitive literals from their parsed YAML nodes when the literal is the contract. Name the invariant so the reason for exactness is clear.
- Use exact lexical source checks only when parsing or execution erases the hazard, such as forbidden shell-version constructs or dangerous Azure template-expression literals. State why the lexical form itself is the contract.
- Do not assert formatting, comments, presentation-only display names, full step inventories, incidental command strings or quoting, implementation prose, or negative strings for renamed symbols. Retain exact parsed names when they are externally consumed identities, such as required status checks.
- GitHub Actions workflow syntax, expressions, and schema belong to actionlint. Other configuration formats require their own validators. Parsed YAML cannot prove platform-owned runtime behavior.

## Imperative logic

- Extract inline JavaScript, PowerShell, or shell when it contains meaningful branches, loops, parsing or transformation, retry or error handling, security-sensitive decisions, or reusable logic.
- Keep trivial orchestration commands inline when locality is clearer. Extra files and quoting or path boundaries can make simple wiring harder to maintain.
- After extraction, test both layers:
  - Execute the script with controlled fixtures and assert exit codes, outputs, side effects, generated files, and external-tool invocations. Reuse `NodeCommand`, `PowerShellCommand`, temporary workspaces or Git repositories, fake executables through `PATH`, and generated fixtures.
  - Retain a focused parsed-YAML test proving that the workflow invokes the correct file with its required inputs, environment, permissions, dependencies, and condition.

## Fixtures and generated output

- Use a temporary workspace for ordinary file, script, or MSBuild fixtures. Initialize a minimal Git repository only when behavior depends on commits, branches, merge bases, rename records, staging, or `git diff`; assert the resulting behavior rather than Git command text.
- Use Verify for a stable, reviewable, complete generated artifact when fragment assertions would duplicate one output contract. Keep focused assertions for calculations, filtering, security or escaping, and edge behavior. Do not snapshot workflow or configuration source as a substitute for semantic tests, and avoid large approval blobs.
- Test selector and trigger-map mechanisms with small synthetic maps and project graphs. Keep production-map coverage thin: derived cross-file invariants and representative positive and negative regressions for consequential curated routing boundaries. Do not duplicate the production map or job inventory as expected data.

## Verification hierarchy

1. Use actionlint for GitHub Actions workflow syntax, expression, and schema validation.
2. Use parsed YAML tests for orchestration and security contracts.
3. Use executable behavior tests for imperative logic.
4. Use live or dry-run validation only for integration behavior owned by the platform.

Azure Pipelines changes that depend on platform behavior still require validation in the official pipeline.
