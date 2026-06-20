# Kubernetes hosting integration

Provides publishing extensions to Aspire for Kubernetes.

## Getting started

### Prerequisites

- [Helm](https://helm.sh/docs/intro/install/) **v4.2.0 or later** on your `PATH`.

Aspire shells out to `helm upgrade --install` to deploy the generated chart and validates the Helm version up front, so missing or older installs produce a clear actionable error instead of cryptic flag failures.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Kubernetes` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Kubernetes
```

## Usage example

In the AppHost, add the environment:

**C#**

```csharp
builder.AddKubernetesEnvironment("k8s");
```

**TypeScript**

```typescript
await builder.addKubernetesEnvironment("k8s");
```

```shell
aspire publish -o k8s-artifacts
```

## Additional documentation

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/compute/kubernetes/

## Feedback & contributing

https://github.com/microsoft/aspire
