# Aspire.Hosting.Azure.Kubernetes library

Provides extension methods and resource definitions for an Aspire AppHost to configure an Azure Kubernetes Service (AKS) environment.

## Getting started

### Prerequisites

- An Azure subscription - [create one for free](https://azure.microsoft.com/free/)

### Install the package

In your AppHost project, install the Aspire Azure Kubernetes Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.Kubernetes
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add an AKS environment and deploy services to it:

```csharp
var aks = builder.AddAzureKubernetesEnvironment("aks");

var myService = builder.AddProject<Projects.MyService>()
    .WithComputeEnvironment(aks);
```

## Additional documentation

* https://learn.microsoft.com/azure/aks/

## Feedback & contributing

https://github.com/microsoft/aspire
