# Run, publish, and deploy modes

Mode behavior is one of the most important Aspire hosting integration design points.

## Definitions

| Mode | Meaning |
| --- | --- |
| Run mode | Local orchestration by the AppHost and DCP. Runtime values such as allocated host ports and local container endpoints exist here. |
| Publish mode | Manifest or infrastructure generation. Runtime values may not exist. Emit references, placeholders, or deployment model customizations instead of reading local runtime state. |
| Deploy mode | Applying generated deployment artifacts or executing deployment pipelines. Deployment targets, publish-time validation, and generated container images matter here. |

## Core rules

DO:

- Branch callbacks that read runtime-only values on `context.ExecutionContext.IsPublishMode`.
- Emit references, placeholders, or deployment target metadata in publish mode instead of resolving local endpoints.
- Make run-only helpers no-op or hidden in publish mode.
- Make publish-only APIs no-op outside publish mode.
- Keep deployment environment resources out of the run model unless they have a real local runtime role.
- Validate publish-only preconditions in publish/build pipeline steps, not during `aspire start`.

DON'T:

- Don't read allocated host ports, runtime endpoint URLs, container hostnames, or local process state during publish.
- Don't add deployment target environment resources to the local dashboard/run model.
- Don't let dev tools, setup siblings, or admin UIs appear in manifests.
- Don't mutate run-mode resources just to satisfy deployment output customization.

## API mode contracts

| API shape | Expected behavior |
| --- | --- |
| `RunAs{Mode}` | Affects run mode only. Return unchanged builder in publish if the setup has no publish meaning. |
| `PublishAs{Target}` | Affects publish/deploy output only. Return unchanged builder outside publish. |
| `AsExisting` | Existing-resource semantics apply in both run and publish. |
| `RunAsExisting` | Existing-resource semantics apply when running. |
| `PublishAsExisting` | Existing-resource semantics apply when publishing/deploying. |
| `Add{DeploymentTarget}Environment` | Usually returns a non-added builder in run mode and adds the environment resource in publish. |

## Common patterns

Run-only companion:

```csharp
if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
{
    return builder;
}
```

Publish-only customization:

```csharp
if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
{
    return builder;
}

builder.ApplicationBuilder.AddTargetInfrastructureCore();
return builder.WithAnnotation(new TargetCustomizationAnnotation(configure));
```

Deployment environment hidden in run mode:

```csharp
return builder.ExecutionContext.IsRunMode
    ? builder.CreateResourceBuilder(environmentResource)
    : builder.AddResource(environmentResource);
```
