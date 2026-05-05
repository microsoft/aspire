# Design comparison: Hybrid vs Unified rules

## Scope

This comparison is intentionally design-focused.

It does not go deep on implementation details. The goal is to decide which design direction is the better planning target for the Aspire repo’s next iteration of selective test execution.

## Short summary

- **Unified rules** is now the stronger candidate, especially when paired with additive `dotnet-affected` resolver behavior.
- **Hybrid** remains the pragmatic fallback if unified rules cannot express repo patterns cleanly enough.

Given the added context that the conditional-selection work on this branch has not merged, the design can optimize for a clean contract rather than backward compatibility.

## Comparison table

| Dimension | Hybrid | Unified rules |
|---|---|---|
| Mental model | Better than today, but still mixed | Cleanest single model |
| Rollout risk | Lower | Moderate |
| Fit with current repo patterns | Strong | Strong once direct mappings and additive `dotnet-affected` are both allowed |
| Explainability | Good if provenance is added | Best |
| Audit mode | Strong | Strongest |
| Migration cost | Moderate | Highest |
| Support for job-only triggers | Strong | Strong |
| Support for future growth | Good | Best |

## Where Hybrid wins

### 1. It respects current repo strengths

The repo already has direct source-to-test mapping patterns that are useful and readable. Hybrid preserves those.

### 2. It is easier to stage

You can add first-class tags and provenance without forcing a full config rewrite at once.

### 3. It is safer with current workflow consumers

The path from current outputs to compatibility outputs is simpler.

## Where Unified rules wins

### 1. One primitive is easier to explain

A single rules model gives the best answer to:

- why did this job run?
- why did this project get selected?
- what exact rule caused it?

### 2. It likely ages better

As the repo adds more non-project triggers and more special cases, one rules model may prevent further fragmentation.

### 3. It can absorb current exceptions cleanly

Things like default coverage projects fit more naturally into a unified contract than into workflow-level special cases.

## Key repo-specific considerations

### Placeholder mapping matters

This is one of the strongest arguments in Hybrid’s favor, but it is no longer automatically disqualifying for Unified rules.

The better framing is:

- if a unified rules model stays readable with explicit mappings, keep it clean
- if repeated repo patterns become awkward, allow parameterized mapping as an option inside the unified model

That means this concern should be treated as a **design branch inside Unified rules**, not only as an argument for Hybrid.

### `dotnet-affected` does not require hybrid config

One of the earlier reasons to keep Hybrid was preserving first-class generic affected-project discovery.

That no longer needs to be true at the config level.

The cleaner position is:

- keep the config unified
- let the selector run `dotnet-affected` additively during evaluation
- preserve provenance in the output so rule-selected and `dotnet-affected`-selected projects remain distinguishable

This keeps the operational benefit that Hybrid was trying to protect without reintroducing a second top-level matching concept.

### Job-only behavior is non-negotiable

Any chosen design must make room for tags that do not select any `.csproj`.

Today that clearly includes tags such as `extension` and `polyglot`, and the model should also leave room for future tag-only cases such as deployment-related workflow gates.

### Conservative fallback should remain

Both designs should preserve the repo’s current safety bias.

### Audit needs better provenance

Whichever design wins, audit should compare more than final booleans:

- selected tags
- selected projects
- unmatched files
- explanation/rule hits

The preferred design target is structured machine-comparable output first. Human-readable summaries should be derived from those artifacts later.

## Recommended planning position

Use **Unified rules** as the design to develop first.

Why:

- it gives the cleanest contract while the work is still unmerged
- it gives the best explanation and audit story
- it avoids introducing compatibility layers that would only need later removal
- it works naturally with additive-only semantics, including unioning direct rule-selected projects with `dotnet-affected` test projects

Use **Hybrid** as the fallback if, during deeper design work, the unified model becomes awkward for repeated repo patterns such as placeholder-style project mapping or if downstream consumers truly require a first-class config split between rule-selected and generic-discovery behavior.

## Suggested next design questions

1. Should tags be purely additive?
2. Should default coverage projects become first-class outputs?
3. What provenance artifact is required for audit to be useful?
4. Which current workflow outputs must remain stable through rollout?

## Bottom line

Unified rules is now the best next planning target.

Hybrid is the best fallback if design pressure from repo-specific mapping patterns or downstream compatibility requirements outweigh the benefits of a single unified rule model.

Assumed rule style for ongoing design:

- additive by default
- explicit safety constructs for run-all / ignore / non-applying behavior
