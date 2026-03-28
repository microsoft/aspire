# Aspire.Hosting.Azure.Functions library

Provides methods to the Aspire hosting model for Azure functions.

## Getting started

### Prerequisites

* An Aspire project based on the starter template.
* A .NET-based Azure Functions worker project.

### Install the package

In your AppHost project, install the Aspire Azure Functions Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.Functions
```

## Usage example

Add a reference to the .NET-based Azure Functions project in your `AppHost` project.

```dotnetcli
dotnet add reference ..\Company.FunctionApp\Company.FunctionApp.csproj
```

In the _AppHost.cs_ file of `AppHost`, use the `AddAzureFunctionsProject` to configure the Functions project resource.

```csharp
using Aspire.Hosting;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Functions;

var builder = new DistributedApplicationBuilder();

var storage = builder.AddAzureStorage("storage").RunAsEmulator();
var queue = storage.AddQueues("queue");
var blob = storage.AddBlobs("blob");

builder.AddAzureFunctionsProject<Projects.Company_FunctionApp>("my-functions-project")
    .WithReference(queue)
    .WithReference(blob);

var app = builder.Build();

app.Run();
```

## Feedback & contributing

https://github.com/microsoft/aspire
