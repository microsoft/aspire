# Migrating off Aspire.AppHost.Sdk

`Aspire.AppHost.Sdk` is obsolete (see diagnostic [`ASPIRE012`](list-of-diagnostics.md)) and will be
removed in a future major version. It historically did two things for a C# AppHost project:

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

## 2. Add an explicit PackageReference to Aspire.Hosting.AppHost

Add a `PackageReference` for the same version of `Aspire.Hosting.AppHost` that matches (or is
compatible with) the `Aspire.AppHost.Sdk` version you were using:

```xml
<ItemGroup>
  <PackageReference Include="Aspire.Hosting.AppHost" Version="9.x.x" />
</ItemGroup>
```

Everything else the SDK previously wired up for you (`IsAspireHost`, code generation for referenced
projects, the `dotnet run`/CLI integration, etc.) is provided by the `Aspire.Hosting.AppHost`
package's own build targets - no other SDK-only behavior is required for a working AppHost.

## 3. Opt in to the Aspire CLI bundle

```xml
<PropertyGroup>
  <AspireUseCliBundle>true</AspireUseCliBundle>
</PropertyGroup>
```

With the CLI bundle enabled, the dashboard and DCP are resolved from your Aspire CLI installation
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

## Suppressing the warning temporarily

If you need more time to migrate, you can suppress `ASPIRE012` while you plan the migration:

```xml
<PropertyGroup>
  <SuppressAspireAppHostSdkObsoleteWarning>true</SuppressAspireAppHostSdkObsoleteWarning>
  <!-- or: -->
  <NoWarn>$(NoWarn);ASPIRE012</NoWarn>
</PropertyGroup>
```

This is only intended as a temporary migration aid - `Aspire.AppHost.Sdk` will be removed in a
future major version.
