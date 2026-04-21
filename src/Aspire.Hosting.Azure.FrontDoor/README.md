# Aspire.Hosting.Azure.FrontDoor library

Provides extension methods and resource definitions for an Aspire AppHost to configure an Azure Front Door resource.

## Getting started

### Prerequisites

- An Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Install the package

In your AppHost project, install the `Aspire.Hosting.Azure.FrontDoor` library via NuGet:

```dotnetcli
dotnet add package Aspire.Hosting.Azure.FrontDoor
```

Or using the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.FrontDoor
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add an Azure Front Door resource and configure origins using the following methods:

```csharp
var api = builder.AddProject<Projects.Api>("api")
    .WithExternalHttpEndpoints();

var frontDoor = builder.AddAzureFrontDoor("frontdoor")
    .WithOrigin(api);
```

## Additional documentation

* https://learn.microsoft.com/azure/frontdoor/

## Feedback & contributing

https://github.com/microsoft/aspire
