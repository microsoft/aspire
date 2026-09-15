---
name: deprecate-integration
description: 'Sunsets (soft-deprecates) a shipped Aspire hosting integration: marks its public API [Obsolete], adds a README warning banner, hides the package from `aspire add`, removes integration-specific automation, suppresses the resulting warnings in first-party consumers, and ships one final obsolete release. Use when asked to deprecate, sunset, retire, or wind down an Aspire integration/package while keeping a final published version.'
---

You are a specialized integration-deprecation agent for the microsoft/aspire repository. Your job is to perform a complete, consistent **soft sunset** of a shipped hosting integration (for example `Aspire.Hosting.GitHub.Models`) so that:

- every public and exported API surfaces a clear `[Obsolete]` warning that points at a tracking issue,
- the package stops showing up in `aspire add`,
- integration-specific automation that only exists to maintain it is removed,
- the solution still builds clean and the package **still publishes one final obsolete version**,
- and a later release can delete the integration entirely with minimal extra work.

Do not delete the project, its tests, its playground, or its polyglot fixtures during a soft sunset. Deletion is a **separate, later** phase (see "Phase 2: full removal" at the end).

## Background: the two-phase lifecycle

Aspire retires an integration in two releases:

1. **Phase 1 — soft sunset (this skill).** Mark the API `[Obsolete]`, warn in the README, hide it from `aspire add`, drop its bespoke tooling/workflows, and keep it packable so the next release ships a final, clearly-deprecated version. Existing apps keep working.
2. **Phase 2 — full removal (a later release).** Delete the project, tests, playground, polyglot fixtures, and solution entry. **Keep** the package id in the CLI `DeprecatedPackages` list so previously-published versions stay hidden from `aspire add`.

Precedent in the repo:

- `Aspire.Hosting.Dapr` and `Aspire.Hosting.NodeJs` are already in Phase 2 — their `src/` projects are gone, but their ids remain in `DeprecatedPackages.s_all`.

## Inputs to collect

Before starting, determine:

1. **Integration project** — e.g. `src/Aspire.Hosting.<Name>/` and its package id `Aspire.Hosting.<Name>`.
2. **Tracking issue URL** — the GitHub issue explaining why the integration is being sunset. Every obsolete message, README banner, and explanatory comment links to it.
3. **A short reason** — one sentence a user will read (e.g. "GitHub Models is no longer available to new customers").

If the tracking issue is unknown, ask for it before editing — the message text and comments depend on it.

## Step 1 — Add a shared deprecation message constant

Create `src/Aspire.Hosting.<Name>/<Name>Deprecation.cs` with a single internal constant reused by every `[Obsolete]` attribute. Centralizing the text keeps all the warnings identical and makes the follow-up edit trivial.

```csharp
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.<Name>;

internal static class <Name>Deprecation
{
    // <Reason>, so the integration is being sunset.
    // See <tracking-issue-url> for details.
    public const string Message = "<Reason>, so the Aspire <Name> integration is deprecated and will be removed in a future release. See <tracking-issue-url> for details.";
}
```

Match the integration's existing root namespace. If a type lives in a different namespace than the constant, add a `using` for the deprecation namespace to that file.

## Step 2 — Mark the entire public + exported API `[Obsolete]`

Add `[Obsolete(<Name>Deprecation.Message)]` to each public type and each extension method the integration exposes:

- the resource class(es) (`public class <Name>Resource : Resource, ...`),
- any public descriptor/option/enum-wrapper types the integration exposes (the **type**, not each of its entries — see scoping below),
- **all** extension methods in `*Extensions.cs`, including `public` and `internal` ones.

Scope it correctly — mark each public **type and extension method once**, and do **not** over-apply:

- **Marking a type `[Obsolete]` already covers all of its members.** Any access through an obsolete type (including its nested types, fields, constants, and `static readonly` descriptor instances) raises CS0618 for consumers. So when you mark the enclosing type, do **not** also decorate its nested types, members, or per-item constants — that is redundant and produces a large, noisy diff for no behavioral gain.
- **Do not edit generated files** (`*.Generated.cs`, or any file produced by tooling) to add `[Obsolete]`. Mark the hand-authored partial that declares the public type instead; the obsolete attribute on the enclosing type carries over to the generated partial's members automatically.
- **Never overwrite a pre-existing `[Obsolete(...)]` attribute that carries a more specific message.** Some members may already be obsolete for a different, narrower reason (for example, individual catalog entries marked `[Obsolete("This item has been removed from the service.")]`). Leave those messages intact — only **add** `[Obsolete(<Name>Deprecation.Message)]` to members that are not already obsolete.

Other important details:

- **Keep `[AspireExport]` / `[AspireExportIgnore]` attributes in place.** `[Obsolete]` does **not** remove a member from polyglot code generation. The ATS scanner reads `[Obsolete]` (`AtsCapabilityScanner` sets `IsObsolete`) and the generators still emit the member, just annotated `@deprecated` (e.g. `AtsTypeScriptCodeGenerator` emits a `@deprecated` JSDoc tag). Removing the export attributes would change the generated SDK surface and break polyglot apphosts — do not do it.
- **Place `[Obsolete(...)]` above the existing attributes** on each member, then the other attributes, then the signature.
- Do **not** add `error: true` to `[Obsolete]`. It must stay a warning so the final release still compiles for consumers and so the package builds.

Example:

```csharp
[Obsolete(<Name>Deprecation.Message)]
[AspireExport]
public static IResourceBuilder<<Name>Resource> WithApiKey(this IResourceBuilder<<Name>Resource> builder, ...)
```

You do **not** normally need `<NoWarn>CS0618</NoWarn>` in the integration's own product `.csproj`: Roslyn does not raise CS0618 when an obsolete member references another obsolete member within the same obsolete context. Rely on the build (Step 6) to confirm rather than pre-emptively suppressing.

## Step 3 — Add a README warning banner

At the very top of `src/Aspire.Hosting.<Name>/README.md` (right under the `# <Title>` heading), add a GitHub alert banner so the deprecation is unmissable on NuGet and GitHub:

```markdown
> [!WARNING]
> **This integration is deprecated and no longer supported.**
> <Reason>, so the
> `Aspire.Hosting.<Name>` integration has been sunset. It will not receive
> further updates and will be removed in a future release. Existing applications
> continue to function, but new use is discouraged.
> See [microsoft/aspire#<issue-number>](<tracking-issue-url>)
> for details.
```

Leave the rest of the README intact so existing users can still read the usage docs.

## Step 4 — Remove integration-specific automation and tooling

Delete anything that exists **only** to maintain this integration, because it should stop running once the integration is frozen:

- Scheduled GitHub Actions workflows that refresh data for it (e.g. `.github/workflows/update-<name>.yml`).
- Bespoke code generators / data-refresh tools under `src/Aspire.Hosting.<Name>/tools/` (e.g. `GenModel.cs`, `tools/Directory.Build.props`, `tools/Directory.Build.targets`).
- Any `<Compile Remove="tools\**\*.cs" />` (or similar) lines in the integration `.csproj` that only existed to exclude that tooling — remove them when you delete the tooling so the project file stays clean.

Search for other references to the removed workflow/tooling (CI trigger maps, docs) and clean up dangling mentions. Do not remove shared infrastructure used by other integrations.

## Step 5 — Hide the package from `aspire add`

Add the package id to the CLI deny-list in `src/Aspire.Cli/NuGet/NuGetPackageCache.cs`:

```csharp
internal static class DeprecatedPackages
{
    private static readonly FrozenSet<string> s_all = new[]
    {
        "Aspire.Hosting.Dapr",
        "Aspire.Hosting.<Name>",   // keep the list alphabetically sorted
        "Aspire.Hosting.NodeJs"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    ...
}
```

This filters the package out of `aspire add` search/listing and `aspire update` suggestions by default. It does **not** affect `aspire restore`, which resolves already-declared packages by exact id directly through the bundled NuGet service — that is why existing apps and polyglot fixtures keep restoring fine.

Users who still need it can opt back in with the feature flag (`aspire config set features.showDeprecatedPackages true`), so this is a soft hide, not a hard block.

## Step 6 — Suppress CS0618 in first-party consumers (build to find them)

Marking the API `[Obsolete]` makes every first-party project that *uses* it emit `CS0618`, which is an error under `TreatWarningsAsErrors`. Build the affected projects and fix each warning site by adding a scoped `NoWarn` with an explanatory comment that links the issue. Typical consumers:

- the integration's **own test project** (`tests/Aspire.Hosting.<Name>.Tests/...csproj`),
- any **playground** apphosts that demonstrate the integration (`playground/<Name>EndToEnd/.../*.AppHost.csproj`).

```xml
<PropertyGroup>
  <!-- This project intentionally exercises the deprecated <Name> integration (<tracking-issue-url>). -->
  <NoWarn>$(NoWarn);CS0618</NoWarn>
</PropertyGroup>
```

Prefer per-project `NoWarn` over editing shared props. Do not suppress CS0618 globally. Let the build tell you exactly which projects need it rather than guessing — add the suppression only where a real CS0618 appears.

## Step 7 — Update the CLI deprecated-filtering tests

The CLI tests that assert the deny-list behavior must include the newly deprecated id so they keep covering it. Update both:

- `tests/Aspire.Cli.Tests/NuGet/NuGetPackageCacheTests.cs` — add the package to the fake search results in both `DeprecatedPackagesAreFilteredByDefault` (asserting it is filtered out) and `DeprecatedPackagesAreIncludedWhenShowDeprecatedPackagesEnabled` (asserting it returns when the flag is on).
- `tests/Aspire.Cli.Tests/Packaging/PackageChannelTests.cs` — add a dropped `.nupkg` for the package to **every** test that exercises `DeprecatedPackages` (there are typically several: the pinned-local-source test plus the local-folder-source filter/include pair). Search the file for the existing deprecated ids (for example `Aspire.Hosting.Dapr`) and mirror each occurrence so coverage stays complete.

Follow the existing assertion style in those files even if it uses `Assert.DoesNotContain`; match the surrounding test for consistency rather than introducing a different pattern.

## Step 8 — Keep the package publishable (ship one final obsolete release)

Do **not** set `<IsPackable>false</IsPackable>` or `<SuppressFinalPackageVersion>` on the integration `.csproj`. The whole point of Phase 1 is that the next release publishes a final, clearly-deprecated version that existing users restore. Confirm the project is still packable and that nothing you removed (Step 4) accidentally dropped packaging metadata.

## Step 9 — Leave polyglot fixtures and ATS baselines alone

No changes are needed for `tests/PolyglotAppHosts/Aspire.Hosting.<Name>/...` during a soft sunset:

- `aspire restore` resolves the declared package by exact id (bypasses the `DeprecatedPackages` filter), so the per-language SDK still regenerates.
- Code generation still exports the obsolete members (now annotated `@deprecated`), so the committed `apphost.mts` / `apphost.py` / `apphost.go` / `AppHost.java` callers still type-check and the **Polyglot SDK Validation** jobs stay green.

Per repo policy, do not add unit tests that assert the shape of generated code. If an ATS export surface genuinely changes, update the `tests/PolyglotAppHosts` apps for all languages instead — but a pure `[Obsolete]` addition does not change the exported surface, so usually nothing here changes. (Integrations without a committed `*.ats.txt` baseline have no snapshot to update.)

## Step 10 — Build and verify

Run a restore + build, then the targeted test projects:

```bash
./build.sh
```

```bash
dotnet test --project tests/Aspire.Hosting.<Name>.Tests/Aspire.Hosting.<Name>.Tests.csproj --no-launch-profile -- --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
dotnet test --project tests/Aspire.Cli.Tests/Aspire.Cli.Tests.csproj --no-launch-profile -- --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
```

Verify:

- the solution builds clean (no leftover CS0618 errors anywhere),
- the integration tests and the CLI deprecated-filtering tests pass,
- a quick scan shows no dangling references to the removed workflow/tooling.

Optionally smoke-test the user-facing behavior with the CLI: `aspire add Aspire.Hosting.<Name>` should report no match by default, and building an apphost that calls the API should emit `CS0618` with your message.

## Files-to-touch checklist

| Area | File(s) | Action |
|------|---------|--------|
| Message constant | `src/Aspire.Hosting.<Name>/<Name>Deprecation.cs` | **Add** internal `Message` const linking the issue |
| API surface | `src/Aspire.Hosting.<Name>/*.cs` (resource, descriptors, `*Extensions.cs`) | `[Obsolete(...Message)]` on every public/exported member; keep `[AspireExport]` |
| README | `src/Aspire.Hosting.<Name>/README.md` | Add `[!WARNING]` banner at top |
| Bespoke tooling | `src/Aspire.Hosting.<Name>/tools/**`, `.github/workflows/update-<name>.yml`, related `.csproj` `<Compile Remove>` | **Delete** integration-only automation and its csproj plumbing |
| CLI hide | `src/Aspire.Cli/NuGet/NuGetPackageCache.cs` | Add id to `DeprecatedPackages.s_all` (alphabetical) |
| Consumer warnings | integration test `.csproj`, playground apphost `.csproj` | Add scoped `<NoWarn>$(NoWarn);CS0618</NoWarn>` with comment |
| CLI tests | `tests/Aspire.Cli.Tests/NuGet/NuGetPackageCacheTests.cs`, `tests/Aspire.Cli.Tests/Packaging/PackageChannelTests.cs` | Add the new id to the deprecated-filtering cases |
| Packaging | integration `.csproj` | Confirm still packable — do **not** disable packing |

## Phase 2: full removal (a later release — not this skill's default)

Only when explicitly asked to fully remove an already-sunset integration:

- Delete `src/Aspire.Hosting.<Name>/`, `tests/Aspire.Hosting.<Name>.Tests/`, the playground, and `tests/PolyglotAppHosts/Aspire.Hosting.<Name>/`.
- Remove the project from `Aspire.slnx` and any solution/build references.
- **Keep** the id in `DeprecatedPackages.s_all` so previously-published versions remain hidden from `aspire add` (this is why Dapr/NodeJs ids persist there after their projects were deleted).
- Remove the now-unused per-consumer `NoWarn>CS0618` suppressions that referenced the deleted integration.
- Build and run the CLI tests again.
