# Aspire.Hosting.Keycloak library

Provides extension methods and resource definitions for an Aspire AppHost to configure a Keycloak resource.

## Getting started

### Install the package

In your AppHost project, install the Aspire Keycloak Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Keycloak
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a Keycloak resource and enable service discovery using the following methods:

```csharp
var keycloak = builder.AddKeycloak("keycloak", 8080);

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(keycloak);
```

**Recommendation:** For local development use a stable port for the Keycloak resource (8080 in the example above). It can be any port, but it should be stable to avoid issues with browser cookies that will persist OIDC tokens (which include the authority URL, with port) beyond the lifetime of the AppHost.

## Feedback & contributing

https://github.com/microsoft/aspire
