# Aspire.Hosting.AppHost library

Provides the core APIs and MSBuild logic for Aspire AppHost projects.

## Getting started

### Install the package

In your project, install the Aspire AppHost library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.AppHost
dotnet add package Aspire.Hosting.Dotnet
```

## Usage example

`Aspire.Hosting.AppHost` supplies AppHost APIs and targets. `Aspire.Hosting.Dotnet` supplies
`AddDotnetProject`; the AppHost package alone does not include that integration.

Then, in the _AppHost.cs_ file of `AppHost`, add a .NET project resource by its actual path:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var app = builder.AddDotnetProject("myapp", "../MyApp/MyApp.csproj");

builder.Build().Run();
```

The path is relative to the AppHost directory. Ordinary path-based services do not need a
resource `ProjectReference`; V2 coordinates their builds when running. An AppHost-only build
does not build removed service references. See the [AddProject migration guide](../../docs/migrate-from-addproject.md)
for typed resources, launch profiles, and retained legacy exceptions, and the
[SDK migration guide](../../docs/migrate-from-apphost-sdk.md) for project configuration.

## Additional documentation
https://aspire.dev/

## Feedback & contributing

https://github.com/microsoft/aspire
