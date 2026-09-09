# Polyglot integration restore

## Purpose

Polyglot AppHosts restore Aspire hosting integrations through two execution paths:

- Package-only integration closures use the bundled managed NuGet implementation.
- Closures containing project references use an SDK-generated restore project.

Both paths must apply the same Aspire-selected package source policy while preserving NuGet's native configuration behavior for credentials, trusted signers, fallback folders, audit settings, relative paths, and other user-owned settings.

## Configuration boundary

The generated integration restore is rooted under the AppHost-specific Aspire integration cache. NuGet configuration discovery starts from that directory.

This boundary intentionally:

- Includes configuration inherited from directories above the integration cache.
- Excludes configuration beneath a nested AppHost directory.
- Keeps referenced projects responsible for their own directory-scoped configuration.

The generated restore project resolves the Aspire server and integration closure. It does not combine the NuGet configuration of every project in the referenced MSBuild graph.

## Effective source policy

`IntegrationRestoreSourceResolver` resolves channel and source customization before the package-only and SDK paths diverge.

The policy accounts for:

- The requested or identity-selected channel.
- Explicit source overrides.
- Local package hives.
- Staging feed overrides.
- The NuGet service-index override.
- Global-packages-folder isolation explicitly requested by the selected channel.

The result contains the effective source locations, package patterns, and cache-isolation requirements. Downstream restore paths consume this result directly and do not reconstruct built-in feed URLs.

Relative local sources are resolved against the AppHost directory before they are used from the integration cache.

Cache isolation is an explicit channel policy rather than something inferred from a local source path. Staging feeds opt into source-specific isolation because distinct feeds can publish different packages under the same stable-shaped version.

Local and PR package hives use [NuGet's normal global-packages behavior](https://learn.microsoft.com/nuget/consume-packages/managing-the-global-packages-and-cache-folders): an existing package with the requested ID and version is reused without consulting the selected source. Replacing package contents under an existing version therefore requires publishing a new version, removing the cached package, or selecting a fresh global packages folder through standard NuGet configuration.

## Native NuGet settings bridge

`Aspire.Managed` owns the narrow operation that requires `NuGet.Configuration`.

For a requested discovery directory, the operation:

1. Loads the normal NuGet hierarchy with `Settings.LoadDefaultSettings`.
2. Returns configuration paths in highest-to-lowest precedence order.
3. Returns non-secret source descriptors containing the source name, resolved location, and enabled state.

The operation does not return credentials, passwords, client certificates, trusted signers, or serialized configuration sections.

The CLI matches effective Aspire source locations to these descriptors using NuGet-compatible source identity rules. Every matching descriptor supplies a source key used by the policy overlay, preserving NuGet's association between each alias and its ambient authentication or transport settings.

## Aspire policy overlay

When the effective policy includes package-source mappings, the CLI writes a small `NuGet.Config` overlay.

The overlay can contain:

- Definitions for effective sources not present in ambient settings.
- A complete `packageSourceMapping` policy with `<clear />`.
- Mapping entries for both the NuGet source key and the effective source location when they differ.
- A controlled global packages folder.
- A `disabledPackageSources` override when every ambient alias for an explicitly selected source is disabled. The overlay clears inherited disabled state, enables one selected alias, and re-emits the other disabled ambient aliases.

Mapping both identities supports the generated root regardless of how NuGet identifies a source:

- The ambient source key is used where one exists.
- The effective source location remains valid when the policy introduces a source not present in ambient settings.

The overlay never copies arbitrary user settings. Authentication, trusted signers, fallback folders, audit settings, and unknown sections continue to come from NuGet's native hierarchy loading.

## Package-only restore

The package-only path:

1. Resolves native settings from the integration cache root.
2. Creates a temporary policy overlay at highest precedence.
3. Passes the ordered overlay and ambient config paths to `Aspire.Managed`.
4. Adds effective source locations only when NuGet has not already resolved the matching configured source.
5. Injects the resulting `ISettings` into `DependencyGraphSpecRequestProvider`.

Configured source descriptors are reused so source names, credentials, protocol settings, and credential-provider behavior remain NuGet-owned.

The temporary overlay is deleted after the restore invocation.

## SDK restore root

The SDK path writes a persistent policy overlay beside `IntegrationRestore.csproj`. Normal SDK discovery loads this file together with the intended ancestor hierarchy.

The generated root project receives `RestoreAdditionalProjectSources` only for effective sources that are not already represented by ambient settings. An empty property is emitted when all effective sources are ambient so an inherited environment value does not replace the root's authenticated source identity.

The generated project does not set `RestoreConfigFile`. Using normal discovery preserves NuGet settings that cannot be represented as source arguments or project properties.

## Referenced-project version hint

The SDK process exposes the invocation-scoped `AspireIntegrationHostingVersion` MSBuild property. It contains the `Aspire.Hosting` version selected by the CLI.

The property is a hint for integration authors. It is visible to every project evaluated in the SDK process, including transitive project references, and an integration can explicitly consume it through central package management.

For example, an integration that intentionally aligns its `Aspire.Hosting` dependency with the invoking CLI can use:

```xml
<Project>
  <ItemGroup>
    <PackageVersion Include="Aspire.Hosting" Version="$(AspireIntegrationHostingVersion)"
                    Condition="'$(AspireIntegrationHostingVersion)' != ''" />
  </ItemGroup>
</Project>
```

Referenced projects remain responsible for their own restore policy. A project that explicitly replaces `RestoreSources` must configure every source needed by the version it selects.

When the source policy requires an isolated global packages folder, the SDK process also receives that folder through `NUGET_PACKAGES`. The generated root and every referenced project therefore use the same source-specific package cache.

## Credentials

Credentials remain in NuGet-owned mechanisms:

- `packageSourceCredentials`
- Environment-based credentials
- Credential providers
- Client certificates
- Authenticated ambient source descriptors

The Aspire overlay contains no copied credential material.

Credential-bearing source URLs are rejected for SDK project-reference restores because the generated project and persistent overlay must remain non-secret. Package-only diagnostics redact credential-bearing source values.

NuGet-generated restore artifacts are not scrubbed or separately isolated by Aspire. Files such as `project.assets.json`, dependency graph specifications, and `.nupkg.metadata` can retain configured source URLs, including inline URL credentials. Authentication should therefore use NuGet credential mechanisms rather than embedding credentials in source URLs.

## Cache identity

Package-only cache identity includes:

- Package identities and versions.
- Target framework and runtime identifier.
- Effective source locations.
- An exact, normalized source-policy identity for isolated global package caches.
- Ordered configuration paths and file bytes.
- Stable content identity for invocation-scoped policy overlays.
- Values of environment variables referenced by NuGet config files.
- Global and fallback package folder inputs.
- The managed restore implementation identity.

SDK restore fingerprints include:

- Generated project content.
- Ordered configuration paths and file bytes.
- Integration hosting-version hint value.
- Global and fallback package folder inputs.
- Referenced project files and their directory-scoped imports.

SDK restore skipping is disabled when a config file references an environment variable, a project uses a floating or property-driven package version, or explicit MSBuild imports and property-driven project references require evaluation. Running restore again is preferred when an unchanged closure cannot be proven.

## Expected scenarios

| Scenario | Generated root | Referenced projects |
|---|---|---|
| Default channel | Uses the effective channel policy and ambient hierarchy | Can opt into the selected version |
| Internal proxy override | Uses only the proxy selected by the source policy | Retains its own restore policy |
| Ambient authenticated source | Uses the ambient source key and NuGet-owned credentials | Retains its own configuration and credentials |
| Explicitly selected disabled source | Clears inherited disabled-source state under the complete mapping policy | Retains its own restore policy |
| Local or PR package hive | Uses an absolute local source and standard NuGet global-packages behavior | Can opt into the selected version |
| Nested AppHost config | Excluded by the integration-cache discovery boundary | Remains available to projects whose own hierarchy includes it |
| Referenced project outside the AppHost tree | Uses the generated root hierarchy | Retains its own config and can explicitly consume the version hint |
