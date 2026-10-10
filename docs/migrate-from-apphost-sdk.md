# Migrating off Aspire.AppHost.Sdk

`Aspire.AppHost.Sdk` is obsolete (see diagnostic [`ASPIRE012`](list-of-diagnostics.md)) but remains
functional for staged migration, including original `ProjectReference`-based C# resources.
No removal date has been agreed. It historically did two things for a C# AppHost project:

1. Made every `ProjectReference` item in the AppHost project become a C# resource automatically
   (`IsAspireProjectResource=true` by default), orchestrated by the AppHost.
2. Added implicit `PackageReference` items for the dashboard and DCP packages for the current RID.

Both behaviors are now obsolete:

* **Project Resource v2** (the [`AddDotnetProject`](../src/Aspire.Hosting.Dotnet/DotnetProjectHostingExtensions.cs)
  API) replaces (1) with an explicit, discoverable call in `Program.cs`.
* The **Aspire CLI bundle** (`AspireUseCliBundle=true`) replaces (2) by resolving the dashboard and
  DCP from the Aspire CLI installation instead of adding RID-specific package references.

This guide walks through migrating a C# AppHost project off `Aspire.AppHost.Sdk`.

## 1. Switch the project SDK

Before:

```xml
<Project Sdk="Aspire.AppHost.Sdk/9.x.x">
```

After:

```xml
<Project Sdk="Microsoft.NET.Sdk">
```

## 2. Add explicit AppHost and .NET project integration packages

Choose a release that supports `AddDotnetProject`. Add explicit references to
`Aspire.Hosting.AppHost` and `Aspire.Hosting.Dotnet` at compatible versions from that release;
`AddDotnetProject` is provided by the latter package, not by `Aspire.Hosting.AppHost`.
Replace `17.x.x` below with an available version; it is not a literal package version.

```xml
<ItemGroup>
  <PackageReference Include="Aspire.Hosting.AppHost" Version="17.x.x" />
  <PackageReference Include="Aspire.Hosting.Dotnet" Version="17.x.x" />
</ItemGroup>
```

The `Aspire.Hosting.AppHost` package establishes `IsAspireHost` and supplies the AppHost targets,
including `dotnet run` integration. It does not automatically promote ordinary project
references into resources.

## 3. Install the required Aspire CLI bundle

```xml
<PropertyGroup>
  <AspireUseCliBundle>true</AspireUseCliBundle>
</PropertyGroup>
```

The bundle is the default and cannot be disabled. The explicit property above can be omitted.
Setting it to `false` produces the unsuppressible `ASPIRE010` error.
The dashboard and DCP are resolved from your Aspire CLI installation
(see [the CLI bundle spec](specs/bundle.md)) instead of being added as RID-specific
`PackageReference` items. Make sure the [Aspire CLI](https://get.aspire.dev) is installed.

## 4. Convert ProjectReference-based C# resources to AddDotnetProject

`Aspire.AppHost.Sdk` defaulted every `ProjectReference` to `IsAspireProjectResource=true`, which
generated a `Projects.<Name>` metadata class and an implicit `builder.AddProject<Projects.Name>(...)`
style resource. With Project Resource v2, add each resource explicitly with `AddDotnetProject` and a
path to the project file, and remove the `ProjectReference` item (it's no longer needed to get a
resource - `AddDotnetProject` resolves the project directly):

Before:

```xml
<ItemGroup>
  <ProjectReference Include="..\MyService\MyService.csproj" />
</ItemGroup>
```

```csharp
var myService = builder.AddProject<Projects.MyService>("myservice");
```

After:

```csharp
var myService = builder.AddDotnetProject("myservice", "../MyService/MyService.csproj");
```

If a referenced project should remain a plain `ProjectReference` (for example, a shared library that
isn't itself an orchestrated resource), keep the `ProjectReference` item and set
`IsAspireProjectResource="false"` on it, or simply leave it as-is once the SDK's automatic
defaulting is gone - under plain `Microsoft.NET.Sdk`, `ProjectReference` items are never
auto-promoted to Aspire resources.

Resource names, endpoint configuration, references, waits and other chained configuration should
remain unchanged. Review overloads and metadata such as launch profiles and excluded endpoints
individually rather than mechanically replacing every `AddProject` call.

`AddDotnetProject` builds its resource project when running it. Removing the resource
`ProjectReference` means `dotnet build` of the AppHost alone no longer builds that service;
keep resource projects in the solution and in CI build/publish inputs.

## File-based AppHosts

Replace the obsolete SDK directive with explicit package directives:

```csharp
#:package Aspire.Hosting.AppHost@17.x.x
#:package Aspire.Hosting.Dotnet@17.x.x
#:property AspireUseCliBundle=true
```

Use actual compatible package versions, retain run configuration and user secrets, and migrate
resource declarations as above. The AppHost package disables Native AOT for file-based AppHosts,
matching the old SDK behavior.

## Verify the migration

Restore and build the AppHost, then run it with the matching Aspire CLI installation.
Verify the dashboard starts, resource names and endpoints are unchanged, services return
expected responses, and Restart/Rebuild work. Check service-to-service references and waits,
not just whether the AppHost compiles. Build the solution separately to check genuine library
references and resource-project build inputs.

An unresolved CLI bundle produces `ASPIRE009` with installation/setup guidance. Fix the
installation or explicit bundle path rather than suppressing the diagnostic.
Pinned DNX selection uses the CLI version paired with `Aspire.Hosting.AppHost`, independently
of an older SDK retained during staged migration.

## Suppressing the warning temporarily

If you need more time to migrate, you can suppress `ASPIRE012` while you plan the migration:

```xml
<PropertyGroup>
  <SuppressAspireAppHostSdkObsoleteWarning>true</SuppressAspireAppHostSdkObsoleteWarning>
  <!-- or: -->
  <NoWarn>$(NoWarn);ASPIRE012</NoWarn>
</PropertyGroup>
```

Suppression does not migrate resources or remove the need for a working CLI bundle.
The obsolete SDK remains functional for users who have not migrated.
