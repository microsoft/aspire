# Aspire.Hosting.Orleans library

Provides extension methods and resource definitions for an Aspire AppHost to configure an Orleans cluster.

## Getting started

### Install the package

In your AppHost project, install the Aspire Orleans library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Orleans
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a Or resource and consume the connection using the following methods:

```csharp
var storage = builder.AddAzureStorage("storage").RunAsEmulator();
var clusteringTable = storage.AddTables("clustering");
var grainStorage = storage.AddBlobs("grainstate");

var orleans = builder.AddOrleans("my-app")
                     .WithClustering(clusteringTable)
                     .WithGrainStorage("Default", grainStorage);

builder.AddProject<Projects.OrleansServer>("silo")
       .WithReference(orleans);

builder.AddProject<Projects.OrleansClient>("frontend")
       .WithReference(orleans.AsClient());
```

## Additional documentation
https://learn.microsoft.com/dotnet/orleans/

## Feedback & contributing

https://github.com/microsoft/aspire
