# TypeScript API compatibility

The TypeScript API compatibility check prevents pull requests from introducing undeclared breaking changes in the ATS surface used to generate TypeScript polyglot AppHost SDKs.

## Baseline

The checked-in `src/Aspire.Hosting*/api/*.ats.txt` files are the release compatibility baseline. The scheduled `.github/workflows/generate-ats-diffs.yml` workflow remains the review and release mechanism for updating the checked-in ATS files after API changes are accepted.

In pull request CI, `.github/workflows/typescript-api-compat.yml` generates ATS output from the pull request target branch and compares it with fresh `aspire sdk dump --format ci` output generated from the pull request. This mergeable baseline avoids failing unrelated pull requests when the target branch contains accepted source changes that have not yet been rolled into the release baseline.

This intentionally differs from a plain git diff. A pull request cannot hide a breaking change by editing the checked-in ATS files in the same PR; the pull request check compares generated target-branch output with generated pull request output.

After a new version ships, reset the compatibility baseline by updating the checked-in ATS files to the shipped surface. Suppressions for breaks that are now part of that new baseline should be deleted in the same change; keep only suppressions that still describe intentional breaks relative to the release baseline. The compatibility checker fails on unused suppressions added by a pull request, but suppressions already present in the target branch are allowed to become unused against the generated target-branch baseline so merged intentional breaks do not block unrelated pull requests before the next release reset.

## Breaking changes

The checker treats these ATS changes as breaking:

- Removed packages, handle types, DTO types, enum types, exported values, or capabilities.
- Removed handle flags such as `ExposeProperties` or `ExposeMethods`.
- Removed DTO properties, optional-to-required DTO property changes, DTO property type changes, or newly added required DTO properties.
- Removed enum values.
- Exported value type or literal value changes.
- Removed capability parameters, optional-to-required parameter changes, required parameter additions, narrowed parameter types, parameter order changes, optional parameter insertions before existing parameters, or return type changes.

Additive changes such as new capabilities, optional capability parameters appended after all existing parameters, widened capability parameter union types, new optional DTO properties, new enum values, and new exported values do not require suppressions.

## Suppressions

Intentional breaks must be declared in a suppression file. Per-package suppressions should live next to the ATS file:

```text
src/Aspire.Hosting.Redis/api/Aspire.Hosting.Redis.tscompat.suppression.txt
```

Cross-cutting suppressions may use:

```text
eng/TypeScriptApiCompat/global.suppression.txt
```

The format is:

```text
BREAK <kind> <package> <symbol> -- <issue-or-pr-url> -- <reason>
```

The `<issue-or-pr-url>` field is required so every intentional breaking change is traceable to the issue or pull request where the API break was reviewed and approved. The `<reason>` should briefly explain why the break is intentional.

Supported `<kind>` values are:

- `package-removed`
- `handle-removed`
- `handle-flag-removed`
- `dto-removed`
- `dto-property-removed`
- `dto-property-type-changed`
- `dto-property-required`
- `dto-property-added-required`
- `enum-removed`
- `enum-value-removed`
- `exported-value-removed`
- `exported-value-type-changed`
- `exported-value-changed`
- `capability-removed`
- `capability-return-type-changed`
- `capability-parameter-removed`
- `capability-parameter-type-changed`
- `capability-parameter-required`
- `capability-parameter-added-required`
- `capability-parameter-order-changed`

Example:

```text
BREAK capability-removed Aspire.Hosting.Redis Aspire.Hosting.Redis/withRedisCommander -- https://github.com/microsoft/aspire/issues/16961 -- Removed unsupported API before GA
```

Suppression matching is exact. Unused suppressions fail the check so stale entries are removed when the API surface changes again.

## Generated TypeScript declarations

This check currently compares the ATS source surface that feeds TypeScript generation. Generator implementation changes are still covered by TypeScript code generation tests and `tests/PolyglotAppHosts/*/TypeScript` validation, but they are not yet classified against a generated `.d.ts` baseline.

If generator-only API shape changes need the same breaking-change treatment, extend `tools/TypeScriptApiCompat` to generate declaration-only output from the checked-in TypeScript validation AppHosts and compare those declarations with the same suppression format.
