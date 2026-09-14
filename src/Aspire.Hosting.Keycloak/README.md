# Keycloak hosting integration

Use this integration to model, configure, and orchestrate a Keycloak resource in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Keycloak` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Keycloak
```

## Usage example

In the AppHost, add a Keycloak resource and enable service discovery with either C# or TypeScript:

**C#**

```csharp
var keycloak = builder.AddKeycloak("keycloak", 8080);

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(keycloak);
```

**TypeScript**

```typescript
const keycloak = await builder.addKeycloak("keycloak", 8080);

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
                       .withReference(keycloak);
```

**Recommendation:** For local development use a stable port for the Keycloak resource (8080 in the example above). It can be any port, but it should be stable to avoid issues with browser cookies that will persist OIDC tokens (which include the authority URL, with port) beyond the lifetime of the AppHost.

## Additional documentation

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/security/keycloak/

## Feedback & contributing

https://github.com/microsoft/aspire
