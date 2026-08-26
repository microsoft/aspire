# TypeScript fluent promise code-reuse experiment

## Summary

The TypeScript ATS generator previously emitted a complete forwarding implementation for every method on every generated `*PromiseImpl` class. Provisioning assemblies magnify this duplication because they expose hundreds of compatible public types.

This experiment replaces those implementations with:

- one generic `FluentPromise<T>` runtime dispatcher in `base.mts`;
- one generated transition table per public promise interface;
- lazy constructor providers for cross-type fluent returns;
- `null` transition entries for methods that return native promises.

Public TypeScript interfaces, method/property names, signatures, JSDoc, argument marshalling, capability IDs, and RPC implementation methods are unchanged.

## Repeated output quantified

The following counts are from the unmodified parent branch output. Promise implementation bytes include only complete generated `class *PromiseImpl` blocks. RPC counts show the other major repeated pattern, which remains in the concrete proxy implementations because those bodies contain method-specific marshalling and protocol behavior.

| Fixture | Promise classes | Promise implementation bytes | RPC invocations | RPC argument blocks |
| --- | ---: | ---: | ---: | ---: |
| Key Vault | 151 | 533,143 | 1,881 | 1,350 |
| Storage | 223 | 814,035 | 3,084 | 1,964 |
| SQL | 272 | 1,308,143 | 5,123 | 3,087 |

The replacement transition tables are 134,212, 201,890, and 310,494 bytes respectively, removing 74.8%-76.3% of the promise implementation bytes.

## Generated SDK sizes

The fixtures were regenerated from the same tracked AppHost configurations before and after the change. `Total SDK` includes `aspire.mts`, `base.mts`, and `transport.mts`. The parent branch runtime files totaled 67,235 bytes; the optimized runtime files total 71,799 bytes.

| Fixture | `aspire.mts` before | `aspire.mts` after | Reduction | Total SDK before | Total SDK after | Reduction |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Key Vault | 4,031,671 | 3,619,736 | 411,935 (10.22%) | 4,098,906 | 3,691,535 | 407,371 (9.94%) |
| Storage | 6,085,611 | 5,454,441 | 631,170 (10.37%) | 6,152,846 | 5,526,240 | 626,606 (10.18%) |
| SQL | 9,843,771 | 8,822,429 | 1,021,342 (10.38%) | 9,911,006 | 8,894,228 | 1,016,778 (10.26%) |

The core-only TypeScript SDK (`Aspire.Hosting` with no provisioning package) changed from the standardized 2,317,381-byte baseline to 2,099,865 bytes, a reduction of 217,516 bytes (9.39%). Relative to those core baselines, provisioning marginal sizes changed as follows:

| Provisioning package | Marginal before | Marginal after | Reduction |
| --- | ---: | ---: | ---: |
| Key Vault | 1,781,525 | 1,591,670 | 189,855 (10.66%) |
| Storage | 3,835,465 | 3,426,375 | 409,090 (10.67%) |
| SQL | 7,593,625 | 6,794,363 | 799,262 (10.53%) |

The previously recorded standardized SQL marginal was 7,593,720 bytes, 95 bytes higher than the regenerated parent fixture used for the paired comparison.

Generated line counts fell from 68,942 to 62,100 for Key Vault, 101,636 to 91,471 for Storage, and 158,420 to 143,229 for SQL.

## TypeScript compile timings

Generation and restore were completed before timing. Each trial removed `tsconfig.tsbuildinfo` and measured `npm run build --silent` with `/usr/bin/time -p`. Before and after SDKs were alternated in the same fixture directory to reduce host-load and filesystem-cache bias.

| Fixture | Before trials (seconds) | After trials (seconds) | Median change |
| --- | --- | --- | ---: |
| Key Vault | 1.10, 1.09, 1.06 | 0.96, 0.97, 0.96 | 1.09 to 0.96 (-11.9%) |
| Storage | 1.34, 1.35, 1.39 | 1.14, 1.15, 1.17 | 1.35 to 1.15 (-14.8%) |
| SQL | 1.86, 1.83, 1.87 | 1.56, 1.53, 1.54 | 1.86 to 1.54 (-17.2%) |

## Validation

- Key Vault, Storage, and SQL fixtures regenerated successfully.
- All three fixture AppHosts pass `npm run build --silent`.
- `Aspire.Hosting.CodeGeneration.TypeScript.Tests`: 110 passed.
- TypeScript runtime tests: 152 passed.
- Runtime coverage verifies fluent chaining, native promise returns, tracking overrides, unknown-member behavior, and Object prototype name collisions.

## Tradeoffs and recommendation

- **Runtime overhead:** Each call through an unresolved fluent chain adds a `Proxy` property lookup and transition-table lookup. Direct calls on resolved generated proxy objects are unchanged. Transition tables are lazily created once per generated promise constructor.
- **Stack traces and debugging:** Forwarding now passes through `FluentPromise` instead of a generated named forwarding method. Stack traces contain less type-specific forwarding code, but capability invocation frames and concrete proxy method names remain unchanged.
- **Source readability:** Public declarations and documentation remain explicit. Internal generated implementation is substantially shorter, while transition tables make cross-type fluent returns visible.
- **Protocol compatibility:** The dispatcher invokes the same generated concrete methods. Capability IDs, argument resolution, callback registration, DTO marshalling, handle wrapping, cancellation, and RPC calls are unchanged.
- **Non-provisioning SDKs:** Every SDK pays 4,564 additional runtime bytes, but the measured core-only SDK is 217,516 bytes smaller because it also reuses fluent promise implementations.

The optimization is production-worthy if the small proxy-dispatch cost is acceptable. It provides a consistent 9.9%-10.3% raw SDK reduction and 11.9%-17.2% lower median TypeScript compile time on the measured provisioning fixtures without weakening the public API or protocol.
