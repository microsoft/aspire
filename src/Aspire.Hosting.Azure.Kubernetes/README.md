# Aspire.Hosting.Azure.Kubernetes library

Provides extension methods and resource definitions for an Aspire AppHost to configure an Azure Kubernetes Service (AKS) environment.

## Getting started

### Prerequisites

- An Azure subscription - [create one for free](https://azure.microsoft.com/free/)
- [Helm](https://helm.sh/docs/intro/install/) **v4.2.0 or later** on your `PATH`.

Aspire shells out to `helm upgrade --install` to deploy the generated chart and any `AddHelmChart(...)` releases, and validates the Helm version up front so missing or older installs produce a clear actionable error instead of cryptic flag failures.

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
