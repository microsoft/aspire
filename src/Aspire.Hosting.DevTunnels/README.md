# Dev tunnels hosting integration

Provides extension methods and resource definitions for an Aspire AppHost to expose local application endpoints publicly via a secure [dev tunnel](https://learn.microsoft.com/azure/developer/dev-tunnels/overview).  
Dev tunnels are useful for:
* Sharing a running local service (e.g., a Web API) with teammates, mobile devices, or webhooks.
* Testing incoming callbacks from external SaaS systems (GitHub / Stripe / etc.) without deploying.
* Quickly publishing a temporary, TLS‑terminated endpoint during development.

> By default tunnels require authentication and are available only to the user who created them. You can selectively enable anonymous (public) access per tunnel or per individual port.

---

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.DevTunnels` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.DevTunnels
```

### Install the devtunnel CLI

Before you create a dev tunnel, you first need to download and install the devtunnel CLI (Command Line Interface) tool that corresponds to your operating system. See the [devtunnel CLI installation documentation](https://learn.microsoft.com/azure/developer/dev-tunnels/get-started#install) for more details.

---

## Basic usage

### Expose all endpoints on a project

**C#**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var web = builder.AddProject<Projects.WebApp>("web");

var tunnel = builder.AddDevTunnel("mytunnel")
                    .WithReference(web);

builder.Build().Run();
```

**TypeScript**

```typescript
import { createBuilder } from "./.aspire/modules/aspire.mjs";

const builder = await createBuilder();

const web = await builder.addNodeApp("web", "../web", "server.js");

const tunnel = await builder.addDevTunnel("mytunnel")
                    .withReference(web);

await builder.build().run();
```

### Enable anonymous (public) access

```csharp
var tunnel = builder.AddDevTunnel("publicapi")
                    .WithReference(web)
                    .WithAnonymousAccess();   // Entire tunnel (all ports) can be accessed anonymously
```

### Expose only specific endpoint(s)

```csharp
var web = builder.AddProject<Projects.WebApp>("web");

var tunnel = builder.AddDevTunnel("apitunnel")
                    .WithReference(web.GetEndpoint("api"));  // Only expose the "api" endpoint
```

### Per‑port anonymous access

You can control anonymous access at the port (endpoint) level using the overload of `WithReference` that accepts a `bool allowAnonymous` parameter:

```csharp
var api = builder.AddProject<Projects.Api>("api");

var tunnel = builder.AddDevTunnel("mixedaccess")
                    .WithReference(api.GetEndpoint("public"), allowAnonymous: true)
                    .WithReference(api.GetEndpoint("admin"));  // This endpoint requires authentication
```

### Custom tunnel ID, description, and labels

```csharp
var options = new DevTunnelOptions
{
    Description = "Shared QA validation tunnel",
    Labels = { "qa", "validation" },
    AllowAnonymous = false
};

var tunnel = builder.AddDevTunnel(
                 name: "qa",
                 tunnelId: "qa-shared",
                 options: options)
             .WithReference(api);
```

### Setting devtunnel region

When creating a dev tunnel, you can optionally specify the Azure region where the tunnel will be hosted.
If not set, when attempting to connect to an existing dev tunnel, it is possible based on ping a different region is chosen. This will create a new dev tunnel in that region,
which may be undesired if testing registered webhooks or a similar scenario.

To prevent this behaviour, it is recommended to explicitly set the desired region.

```csharp
var options = new DevTunnelOptions
{
    Region = DevTunnelRegion.NorthEurope
};

var tunnel = builder.AddDevTunnel(name: "devtunnel", options: options)            
                    .WithReference(api);
             
```

### Configure idle expiration

Set an idle expiration period to clean up tunnels that are no longer used:

**C#**

```csharp
var tunnel = builder.AddDevTunnel("mytunnel")
                    .WithExpiration(24)
                    .WithReference(web);
```

Alternatively, set `ExpirationHours` on `DevTunnelOptions` when calling `AddDevTunnel`.

**TypeScript**

```typescript
const tunnel = await builder.addDevTunnel("mytunnel")
                    .withExpiration(24)
                    .withTunnelReferenceAll(web, false);
```

Expiration is expressed as a whole number of hours, from one hour through 30 days, inclusive.
The setting applies to both new tunnels and existing tunnels reused by the AppHost.
If omitted, new tunnels use the service default and existing tunnels keep their configured expiration period.

This is the time a tunnel can remain unused or unmodified before it expires, not a limit on hosting
duration or an access-token lifetime. See the [devtunnel CLI expiration documentation](https://learn.microsoft.com/azure/developer/dev-tunnels/cli-commands#advanced-manage-dev-tunnels).

### Multiple tunnels for different audiences

```csharp
var web = builder.AddProject<Projects.WebApp>("web");

var publicTunnel = builder.AddDevTunnel("public")
                          .WithReference(web)
                          .WithAnonymousAccess();

var privateTunnel = builder.AddDevTunnel("private")
                           .WithReference(web);  // Requires authentication
```

---

## Service discovery integration

When another resource references a dev tunnel via:

```csharp
builder.AddProject<Projects.ClientApp>("client")
       .WithReference(web, publicTunnel);  // Use the tunneled address for 'web'
```

Environment variables are injected after the tunnel port is allocated using the [Aspire service discovery](https://aspire.dev/fundamentals/service-discovery/) configuration format:

```env
services__{ResourceName}__{EndpointName}__0 = https://{public-host}/
```

Example:

```env
services__web__https__0 = https://myweb-1234.westeurope.devtunnels.ms/
```

This lets downstream resources use the tunneled address exactly like any other Aspire service discovery entry. Note that dev tunnels are a development time concern only and are not included when publishing or deploying an Aspire AppHost, including any service discovery information.

> Referencing a tunnel delays the consumer resource's start until the tunnel has started and its endpoint is fully allocated.

---

## Anonymous access options

| Scope            | How to enable                                  | Notes |
|------------------|-------------------------------------------------|-------|
| Entire tunnel    | `tunnel.WithAnonymousAccess()`                  | Affects all ports unless overridden at port level. |
| Specific port(s) | `WithReference(endpoint, allowAnonymous: true)` | Fine-grained control per exposed endpoint. |

If neither is set, the tunnel is private and authentication as the tunnel creator is required.

---

## Protocol handling

`DevTunnelPortOptions.Protocol` supports:  
* `http`  
* `https`  
* `auto` (let the service decide)  
* `null` (default = use the referenced endpoint's scheme)

Unsupported schemes (e.g., non-HTTP(S)) will throw an exception.

---

## Tunnel logging and diagnostics

When dev tunnel ports are successfully allocated, they log detailed information about their forwarding configuration and access level. This helps you understand which URLs are available and their security settings.

### Connection status and compatibility

Aspire observes the `devtunnel host` console output to make public endpoints available as soon as the local host reports that it is ready. Connection loss updates the tunnel and port resources without waiting for the next health check. Links remain inactive if delayed URL callbacks complete after the tunnel stops. A running CLI process does not necessarily mean its tunnel connection is active.

Health checks also reconcile tunnel ports and access settings with the dev tunnels service. If port or readiness output is unrecognized, Aspire logs a warning and uses service-based reconciliation to discover the endpoints once this local host has reported a connection. A remote host count alone cannot establish local readiness: another machine might be hosting the same tunnel. If no local connection message is recognized, the tunnel remains unready rather than exposing another host's endpoints. Minor whitespace, line wrapping, and separator differences are supported. The warning is limited to once per resource start; the original console output remains available in the tunnel resource's logs.

Starting a tunnel checks its remote configuration and reuses matching tunnels and ports. Unchanged access policies are left intact; changed policies are reconciled before hosting. Ports are recreated only when their protocol, description, or labels differ from the application model. Remote drift is checked on each start rather than relying on a cached successful setup.

For ports configured with `allowAnonymous: false`, an existing permanent, non-inverse anonymous connection deny is retained even if additional access rules are present. An expiring deny is not a permanent restriction, and inverse anonymous rules apply to authenticated users rather than anonymous callers. If a permanent deny is missing, it is added without first clearing the other rules. This avoids temporarily removing an existing restriction during in-place reconciliation.

If tunnel status is incorrect after such a warning, include the output of `devtunnel --version` and the relevant tunnel console logs in an Aspire issue, after removing sensitive information. A service response indicating an active host will not override a disconnect reported by the local CLI or reactivate a connection whose loss or tunnel deletion was established by reconciliation, because that connection may belong to another host. Initial service-status propagation is tracked separately from the loss of a previously confirmed connection.

### Port forwarding logs

Each port resource logs its forwarding configuration when it becomes available:

```text
Forwarding from https://37tql9l1-7023.usw2.devtunnels.ms to https://localhost:7023/ (webfrontend/https)
```

### Anonymous access logging

Port resources also log their effective anonymous access policy when first known or when it changes, showing both the current access level and the configuration that led to it. The display ignores inverse rules and expired entries. A failed policy query clears that port's access property instead of retaining a stale value; successful policy queries for other ports are still applied. Access metadata refreshes do not block subsequent tunnel/port status checks.

**When anonymous access is allowed:**
```text
!! Anonymous access is allowed (port explicitly allows it) !!
```

```text
!! Anonymous access is allowed (inherited from tunnel) !!
```

**When anonymous access is denied:**
```text
Anonymous access is not allowed (tunnel does not allow it and port does not explicitly allow or deny it)
```

```text
Anonymous access is not allowed (tunnel allows it but port explicitly denies it)
```

The logging helps you verify that your tunnel configuration is working as expected and troubleshoot access issues.

---

## Security considerations

* Prefer authenticated tunnels during normal development.
* Only enable anonymous access for endpoints that are safe to expose publicly.
* Treat public tunnel URLs as temporary & untrusted (rate limit / validate input server-side).

---

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/devtools/dev-tunnels/
* [Dev tunnels service](https://learn.microsoft.com/azure/developer/dev-tunnels/overview)
* [Dev tunnels FAQ](https://learn.microsoft.com/azure/developer/dev-tunnels/faq)

---

## Feedback & contributing

https://github.com/microsoft/aspire

Contributions (improvements, clarifications, samples) are welcome.
