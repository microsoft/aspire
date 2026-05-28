# Aspire.Hosting.Kubernetes library

Provides publishing extensions to Aspire for Kubernetes.

## Getting started

### Prerequisites

- [Helm](https://helm.sh/docs/intro/install/) **v4.2.0 or later** on your `PATH`.

Aspire shells out to `helm upgrade --install` to deploy the generated chart and validates the Helm version up front, so missing or older installs produce a clear actionable error instead of cryptic flag failures.

### Install the package

In your AppHost project, install the Aspire Kubernetes Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Kubernetes
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add the environment:

```csharp
builder.AddKubernetesEnvironment("k8s");
```

```shell
aspire publish -o k8s-artifacts
```

## Feedback & contributing

https://github.com/microsoft/aspire
