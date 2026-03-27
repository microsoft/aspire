# Aspire.Hosting.JavaScript library

Provides extension methods and resource definitions for an Aspire AppHost to configure JavaScript projects.

## Getting started

### Install the package

In your AppHost project, install the Aspire JavaScript library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.JavaScript
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a Or resource and consume the connection using the following methods:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddJavaScriptApp("frontend", "../frontend", "app.js");

builder.Build().Run();
```

## Additional documentation
https://github.com/microsoft/aspire-samples/tree/main/samples/aspire-with-javascript
https://github.com/microsoft/aspire-samples/tree/main/samples/aspire-with-node

## Feedback & contributing

https://github.com/microsoft/aspire
