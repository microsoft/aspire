# Aspire.Hosting.DevTunnels library

Provides extension methods and resource definitions for an Aspire AppHost to expose local application endpoints publicly via a secure [dev tunnel](https://learn.microsoft.com/azure/developer/dev-tunnels/overview).  
Dev tunnels are useful for:
* Sharing a running local service (e.g., a Web API) with teammates, mobile devices, or webhooks.
* Testing incoming callbacks from external SaaS systems (GitHub / Stripe / etc.) without deploying.
* Quickly publishing a temporary, TLS‑terminated endpoint during development.

> By default tunnels require authentication and are available only to the user who created them. You can selectively enable anonymous (public) access per tunnel or per individual port.

---

## Getting started

### Install the package

In your AppHost project, install the `Aspire.Hosting.DevTunnels` library via NuGet:

```dotnetcli
dotnet add package Aspire.Hosting.DevTunnels
```

Or using the Aspire CLI:

```bash
aspire add devtunnels
```

### Install the devtunnel CLI

Before you create a dev tunnel, you first need to download and install the devtunnel CLI (Command Line Interface) tool that corresponds to your operating system. See the [devtunnel CLI installation documentation](https://learn.microsoft.com/azure/developer/dev-tunnels/get-started#install) for more details.

---

## Basic usage

### Expose all endpoints on a project

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var web = builder.AddProject<Projects.WebApp>("web");

var tunnel = builder.AddDevTunnel("mytunnel")
                    .WithReference(web);

builder.Build().Run();
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
var api = builder.AddProject<Projects.ApiService>("api");

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

To prevent this behaviour, it is recommended to explicity set the desired region. 

```csharp
var options = new DevTunnelOptions
{
    Region = DevTunnelRegion.NorthEurope
};

var tunnel = builder.AddDevTunnel(name: "devtunnel", options: options)            
                    .WithReference(api);
             
```

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

### Port forwarding logs

Each port resource logs its forwarding configuration when it becomes available:

```text
Forwarding from https://37tql9l1-7023.usw2.devtunnels.ms to https://localhost:7023/ (webfrontend/https)
```

### Anonymous access logging

Port resources also log their effective anonymous access policy, showing both the current access level and the configuration that led to it:

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

* [Aspire documentation](https://aspire.dev/integrations/devtools/dev-tunnels/)
* [Dev tunnels service](https://learn.microsoft.com/azure/developer/dev-tunnels/overview)
* [Dev tunnels FAQ](https://learn.microsoft.com/azure/developer/dev-tunnels/faq)

---

## Feedback & contributing

https://github.com/dotnet/aspire

Contributions (improvements, clarifications, samples) are welcome.
