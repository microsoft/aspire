# YARP hosting integration

Use this integration to model, configure, and orchestrate a YARP reverse proxy instance in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Yarp` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Yarp
```

## Usage examples

### Programmatic configuration

The modern approach uses programmatic configuration with the `WithConfiguration` method:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var backendService = builder.AddProject<Projects.Backend>("backend");
var frontendService = builder.AddProject<Projects.Frontend>("frontend");

var gateway = builder.AddYarp("gateway")
                     .WithConfiguration(yarp =>
                     {
                         // Add a catch-all route for the frontend
                         yarp.AddRoute(frontendService);
                         
                         // Add a route with path prefix for the backend API
                         yarp.AddRoute("/api/{**catch-all}", backendService)
                             .WithTransformPathRemovePrefix("/api");
                     });

var app = builder.Build();
await app.RunAsync();
```

### Configuration with external services

You can also route to external services:

```csharp
var externalApi = builder.AddExternalService("external-api", "https://api.example.com");

var gateway = builder.AddYarp("gateway")
                     .WithConfiguration(yarp =>
                     {
                         yarp.AddRoute("/external/{**catch-all}", externalApi)
                             .WithTransformPathRemovePrefix("/external");
                     });
```

### Static file serving

YARP can serve static files alongside proxied routes. There are two approaches:

#### Copy files locally

```C#
builder.AddYarp("static")
       .WithStaticFiles("../static");
```
This bind mounts files in run mode and copies them into the generated container image in publish mode.

#### Copy files via Docker

You can also use a Dockerfile to copy static assets into the YARP container. Pass the YARP base image used by your deployment as `YARP_BASE_IMAGE` when building the image:
```C#
var yarpBaseImage = builder.AddParameter("yarp-base-image");

builder.AddYarp("frontend")
       .WithStaticFiles()
       .WithDockerfile("../npmapp")
       .WithBuildArg("YARP_BASE_IMAGE", yarpBaseImage);
```

```Dockerfile
# Stage 1: Build React app
FROM node:20 AS builder
WORKDIR /app
COPY . .
RUN npm install
RUN npm run build

# Stage 2: Copy static files into the YARP base image used by your deployment
ARG YARP_BASE_IMAGE
FROM ${YARP_BASE_IMAGE} AS yarp
WORKDIR /app
COPY --from=builder /app/dist ./wwwroot
```
## Configuration API

### Route configuration

The `IYarpConfigurationBuilder` provides methods to configure routes and clusters:

```csharp
// Add routes with different targets
yarp.AddRoute(resource);                                    // Catch-all route
yarp.AddRoute("/path/{**catch-all}", resource);            // Specific path route
yarp.AddRoute("/path/{**catch-all}", endpoint);            // Route to specific endpoint
yarp.AddRoute("/path/{**catch-all}", externalService);     // Route to external service

// Add clusters directly
var cluster = yarp.AddCluster(resource);
var route = yarp.AddRoute("/path/{**catch-all}", cluster);
```

### Route matching options

Routes can be configured with various matching criteria:

```csharp
yarp.AddRoute("/api/{**catch-all}", backendService)
    .WithMatchMethods("GET", "POST")                        // HTTP methods
    .WithMatchHeaders(new RouteHeader("Content-Type", "application/json"))  // Headers
    .WithMatchHosts("api.example.com")                      // Host header
    .WithOrder(1);                                          // Route priority
```

## Transform extensions

YARP provides various transform extensions to modify requests and responses:

### Path transforms

```csharp
route.WithTransformPathSet("/new/path")                     // Set path
     .WithTransformPathPrefix("/prefix")                    // Add prefix
     .WithTransformPathRemovePrefix("/api")                 // Remove prefix
     .WithTransformPathRouteValues("/users/{id}/posts");    // Use route values
```

### Request header transforms

```csharp
route.WithTransformRequestHeader("X-Forwarded-For", "value")           // Add/set header
     .WithTransformRequestHeaderRouteValue("X-User-Id", "id")          // From route value
     .WithTransformUseOriginalHostHeader(true)                         // Preserve host
     .WithTransformCopyRequestHeaders(false);                          // Copy headers
```

### Response transforms

```csharp
route.WithTransformResponseHeader("X-Powered-By", "Aspire")            // Add response header
     .WithTransformResponseHeaderRemove("Server")                      // Remove header
     .WithTransformCopyResponseHeaders(true);                          // Copy headers
```

### Query parameter transforms

```csharp
route.WithTransformQueryValue("version", "1.0")                       // Add query param
     .WithTransformQueryRouteValue("userId", "id")                     // From route value
     .WithTransformQueryRemoveKey("debug");                            // Remove query param
```

## Advanced configuration

### Multiple routes to the same service

```csharp
builder.AddYarp("gateway")
       .WithConfiguration(yarp =>
       {
           // Different routes to the same backend
           yarp.AddRoute("/api/v1/{**catch-all}", backendService)
               .WithTransformPathRemovePrefix("/api/v1");
               
           yarp.AddRoute("/api/v2/{**catch-all}", backendService)
               .WithTransformPathRemovePrefix("/api/v2")
               .WithTransformPathPrefix("/v2");
       });
```

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/integrations/reverse-proxies/yarp/
* [YARP documentation](https://microsoft.github.io/reverse-proxy/)
* [Aspire documentation](https://aspire.dev/)
* [Service Discovery in Aspire](https://aspire.dev/fundamentals/service-discovery/)

## Feedback & contributing

https://github.com/microsoft/aspire
