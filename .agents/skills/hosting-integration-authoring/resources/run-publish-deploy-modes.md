# Run, publish, and deploy modes

Mode behavior is one of the most important Aspire hosting integration design points.

## Definitions

| Mode | Meaning |
| --- | --- |
| Run mode | Local orchestration by the AppHost and DCP. Runtime values such as allocated host ports and local container endpoints exist here. |
| Publish mode | Asset generation for `aspire publish`. Runtime values may not exist. Emit reviewable artifacts, references, placeholders, or deployment model customizations instead of reading local runtime state or mutating provider resources. |
| Deploy mode | Applying generated assets for `aspire deploy`. Deployment resolves safe late-bound values, performs preflight checks, builds/pushes required images when that is part of the pipeline, mutates provider resources, verifies target state, and records deployment outputs. |

## Core rules

DO:

- Branch callbacks that read runtime-only values on `context.ExecutionContext.IsPublishMode`.
- Emit references, placeholders, or deployment target metadata in publish mode instead of resolving local endpoints.
- Make run-only helpers no-op or hidden in publish mode.
- Make publish-only APIs no-op outside publish mode.
- Keep deployment environment resources out of the run model unless they have a real local runtime role.
- For run-mode controllers/reconcilers, keep user-facing control resources visible only when they can act locally; in publish mode, either omit them or keep hidden/excluded marker resources only when later publish/deploy stages need to discover model metadata.
- Validate publish-only preconditions in publish/build pipeline steps, not during `aspire start`.
- Treat deploy as a separate lifecycle from publish: deploy resolves late-bound values, applies artifacts idempotently, records target outputs, and should have a clear update and cleanup/destroy story.
- Keep `aspire publish` assets useful without live target credentials when possible: generated manifests, parameter placeholders, image references/build metadata, prerequisite notes, and target-specific files should be inspectable and source-control/review friendly.
- Keep `aspire deploy` responsible for target-specific mutation: credential/context checks, registry push, provider apply, IAM changes, URL/status verification, and deployment state updates.

DON'T:

- Don't read allocated host ports, runtime endpoint URLs, container hostnames, or local process state during publish.
- Don't add deployment target environment resources to the local dashboard/run model.
- Don't let dev tools, setup siblings, or admin UIs appear in manifests.
- Don't mutate run-mode resources just to satisfy deployment output customization.
- Don't serialize controller runtime state, queued operations, command state, or drift probe results into publish artifacts.
- Don't assume publish success means deploy success; target auth, provider validation, registry access, and IAM can still fail after artifacts are generated.
- Don't make `aspire publish` require cloud credentials merely to inspect generated assets unless the target cannot generate meaningful assets without provider reads; if it must, document and test that requirement.
- Don't let `aspire deploy` regenerate a materially different desired model from `aspire publish` without writing the deploy-time assets separately and making the difference explicit.

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
