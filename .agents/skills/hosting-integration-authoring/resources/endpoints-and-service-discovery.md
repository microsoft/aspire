# Endpoints and service discovery

Endpoints are part of the app model contract. They drive connection properties, service discovery, generated URLs, container networking, and deployment output.

## Endpoint naming

DO:

- Use stable endpoint names.
- Use `"tcp"` for the primary non-HTTP protocol endpoint when there is only one protocol endpoint.
- Use `"http"` and `"https"` for HTTP endpoints.
- Use role-specific names when multiple endpoints exist, for example `"internal"`, `"management"`, `"emulator"`, or `"metrics"`.
- Expose common endpoint references as properties such as `PrimaryEndpoint`, `InternalEndpoint`, or `HttpEndpoint`.
- Use separate host-facing and container-internal endpoints when clients connect differently from host processes and containers.

DON'T:

- Don't rename endpoint names casually; consumers and connection properties may depend on them.
- Don't create multiple endpoints with ambiguous generic names.
- Don't hardcode host ports unless the integration explicitly models a fixed-port requirement.

## Host, target, and internal ports

The host port is the port exposed on the developer machine. The target port is the port inside the container or process. The target port is often fixed by the service. The host port should usually be nullable so Aspire can allocate it.

DO:

- Accept `int? port = null` for user-selected host ports.
- Set the service's known target port explicitly.
- Use `WithHostPort(int? port)` for companion/admin UI host-port customization.
- Use `EndpointProperty.Host`, `EndpointProperty.Port`, `EndpointProperty.HostAndPort`, or `EndpointProperty.Url` in reference expressions.

DON'T:

- Don't confuse host `Port` with container `TargetPort`.
- Don't use allocated host ports in publish-mode callbacks.
- Don't expose internal-only endpoints as reference endpoints unless consumers should use them.

## Service discovery vs connection properties

Use service discovery when a workload needs to call another HTTP/gRPC-style workload by logical service name and endpoint.

Use connection properties when a consumer needs protocol-specific data such as database host, port, username, password, URI, JDBC string, queue name, model name, or API key.

Some resources need both. For example, an app workload may expose HTTP endpoints for service discovery and also expose connection properties for protocol clients.

## Reference endpoints

DO:

- Mark health-only, management-only, or internal-only endpoints with `ExcludeReferenceEndpoint` when they should not be used by `WithReference`.
- Prefer HTTPS reference endpoints when both HTTP and HTTPS are available and that matches existing resource conventions.
- Keep endpoint schemes accurate; URI expressions use the scheme.

DON'T:

- Don't make readiness/health probe endpoints the default reference endpoint.
- Don't expose admin UI endpoints as service dependencies unless that is the intended API.

## URL display

Dashboard URL display is part of developer experience.

DO:

- Use `WithUrlForEndpoint` to customize display names, locations, or URLs when defaults are confusing.
- Put secondary diagnostic URLs in details-only display when they are not primary user entry points.
- Show admin companion URLs only for resources users are expected to open.

DON'T:

- Don't flood the dashboard summary with internal, health, metrics, or implementation URLs.

## Endpoint environment variables

For language apps and frameworks that expect a port environment variable, use endpoint APIs that set env vars such as `PORT` instead of manually duplicating endpoint state.

Branch mode-specific endpoint args carefully. Development servers may bind localhost or add reload flags in run mode, but published containers should bind `0.0.0.0` and use the deployment-provided port.

## Mediated and externally allocated endpoints

Some integrations expose an endpoint through a mediator such as a tunnel, proxy, or external CLI. The target endpoint and public endpoint are different resources.

DO:

- Preserve the original target endpoint as an `EndpointReference`.
- Create a separate facade endpoint for the mediated/public URL when consumers need to reference it.
- Allocate the facade endpoint only when the external endpoint is known at run time.
- Inject the facade endpoint into consumers through normal `WithReference`, service discovery, or environment flows.
- Account for host/container differences when deciding whether to forward a target port or an allocated host port.

DON'T:

- Don't overwrite the target resource endpoint with the mediated endpoint.
- Don't make users parse logs or dashboard URLs to get a mediated endpoint.
- Don't serialize mediated run-mode URLs into publish output.
