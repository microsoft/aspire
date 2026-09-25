# Archetype: external/cloud-reference service

Use this archetype for services that Aspire references but does not run locally or provision directly, such as external APIs, SaaS services, OpenAI-style endpoints, or GitHub Models-style services.

## Resource shape

These resources are usually plain `Resource` types that implement `IResourceWithConnectionString` or another reference interface. They are not `ContainerResource` and not `AzureProvisioningResource`.

They often contain:

- Endpoint URI or base address.
- API key or token `ParameterResource`.
- Optional child model/deployment resources.
- Optional health check that calls a live external endpoint.

## DO

- Use `Add{Service}` or `Add{Provider}` for the parent external reference.
- Store secrets in `ParameterResource`s.
- Mark secret parameters as secret.
- Validate user-supplied credential parameters are marked secret before accepting them.
- Validate endpoint/base-address inputs at construction time, including absolute URI and allowed schemes such as `https`.
- Build connection strings and URIs with `ReferenceExpression`.
- Expose connection properties for endpoint, API key, model/deployment name, and URI as applicable.
- Put child methods like `AddModel` or `AddDeployment` on the parent builder.
- Make live health checks explicit, side-effect-free, and easy to disable if the service charges or rate-limits.
- Document prerequisites clearly, including required API keys and external accounts.

## DON'T

- Don't invent a local container or emulator unless the ecosystem provides a real one.
- Don't use Azure provisioning just because the service is cloud-hosted.
- Don't call live APIs during resource construction.
- Don't defer basic endpoint validation until connection property evaluation.
- Don't hide network calls in connection property generation.
- Don't write API keys to generated files or Dockerfile layers.
- Don't assume all external services can be validated in CI.

## Testing

Unit tests should verify resource shape, parameter wiring, connection expressions, child resource behavior, and health-check registration without calling the real service.

Use fake HTTP servers only when behavior needs protocol validation.
