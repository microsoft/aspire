# Aspire.Hosting.Integration.Analyzers package

Provides Roslyn analyzers for Aspire hosting integration authors building ATS and polyglot-friendly extensions outside the Aspire repo.

## Getting started

### Install the package

In your integration project, install the analyzer package with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Integration.Analyzers --prerelease
```

If you edit the project file directly, keep the analyzer private and use the same package version as the Aspire packages referenced by the integration:

```xml
<ItemGroup>
  <PackageReference Include="Aspire.Hosting.Integration.Analyzers" Version="<Aspire package version>" PrivateAssets="all" />
</ItemGroup>
```

## Usage

The package is applied automatically when it is referenced. No additional MSBuild property is required.

The analyzers validate common ATS export patterns used by polyglot integrations, such as:

- exported builder methods that directly invoke synchronous callbacks
- unsupported signatures or exported shapes for ATS/code-generated hosting surfaces
- other authoring patterns that can break polyglot consumers

For example, this pattern produces analyzer feedback because the exported API executes a synchronous callback directly:

```csharp
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace MyIntegration;

public static class MyIntegrationExtensions
{
    [AspireExport("addMyContainer")]
    public static IResourceBuilder<ContainerResource> AddMyContainer(
        this IDistributedApplicationBuilder builder,
        string name,
        Action<IResourceBuilder<ContainerResource>> configure)
    {
        var resource = builder.AddContainer(name, "my-image");
        configure(resource);
        return resource;
    }
}
```

## Additional documentation

- https://aspire.dev/extensibility/multi-language-integration-authoring/#install-the-analyzer
- https://github.com/microsoft/aspire/blob/main/src/Aspire.Hosting/Ats/ThirdPartyAtsAttributes.md
- https://github.com/microsoft/aspire/blob/main/docs/specs/polyglot-apphost.md

## Feedback & contributing

https://github.com/microsoft/aspire
