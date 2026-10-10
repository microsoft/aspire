---
name: migrate-apphost-sdk
description: Migrates C# Aspire AppHosts from Aspire.AppHost.Sdk and original ProjectReference resources to Microsoft.NET.Sdk, explicit AppHost packages, and AddDotnetProject while preserving application behavior.
---

# Migrate an AppHost off Aspire.AppHost.Sdk

Read `docs/migrate-from-apphost-sdk.md` and the affected AppHost, resource projects,
central package configuration, run configuration and CI build inputs before editing.
Respect repository instructions and unrelated user changes.

1. Identify the selected Aspire release and matching CLI bundle. Use real compatible
   package versions, not the guide's placeholders. Preserve central package management.
2. Change the AppHost SDK to unversioned `Microsoft.NET.Sdk`. Add explicit
   `Aspire.Hosting.AppHost` and `Aspire.Hosting.Dotnet` references. For a file-based
   AppHost, replace the Aspire SDK directive with corresponding package directives.
3. Map each orchestrated C# `ProjectReference` to its `AddProject` call. Replace it with
   `AddDotnetProject` using the same resource name and a project-relative path, then
   remove only that resource reference. Preserve genuine library references.
4. Preserve endpoints, launch-profile choices, references, waits, environment variables,
   secrets and chained integration configuration. Inspect overload-specific settings;
   stop for a decision if an original behavior has no verified equivalent.
5. Install the matching CLI bundle, which is required by default and cannot be disabled.
   Remove any `AspireUseCliBundle=false` setting. Do not use warning suppression
   as a substitute for migration. Do not promise SDK removal: it remains available
   for staged migration and original project resources.
6. Restore/build and run the migrated AppHost. Verify dashboard availability,
   resource names, service responses, references/waits and Restart/Rebuild. Check
   solution and CI build/publish inputs because resource projects are now built
   when run rather than by an AppHost project reference.
7. Exercise `aspire update` and an invalid bundle path. Report exact versions,
   commands, results and limitations; compilation alone is not migration validation.

Do not convert test-project references to the AppHost into resource declarations,
delete user code or secrets, hide failures, or claim successful validation when the
required CLI/packages are unavailable.
