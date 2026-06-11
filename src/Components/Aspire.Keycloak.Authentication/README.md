# Aspire.Keycloak.Authentication library

Adds JwtBearer and OpenId Connect authentication to the project via a [Keycloak](https://www.keycloak.org).

## Getting started

### Prerequisites

- A Keycloak server instance
- A Keycloak realm
- For JwtBearer authentication, a configured audience in the Keycloak realm
- For OpenId Connect authentication, the ID of a client configured in the Keycloak realm

### Install the package

Install the Aspire Keycloak library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Keycloak.Authentication
```

## Jwt bearer authentication usage example

In the _Program.cs_ file of your ASP.NET Core API project, call the `AddKeycloakJwtBearer` extension method to add JwtBearer authentication, using a connection name, realm and any required JWT Bearer options:

```csharp
builder.Services.AddAuthentication()
                .AddKeycloakJwtBearer("keycloak", realm: "WeatherShop", options =>
                {
                    options.Audience = "weather.api";
                });
```

You can set many other options via the `Action<JwtBearerOptions> configureOptions` delegate.

## OpenId Connect authentication usage example

In the _Program.cs_ file of your Blazor project, call the `AddKeycloakOpenIdConnect` extension method to add OpenId Connect authentication, using a connection name, realm and any required OpenId Connect options:

```csharp
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
                .AddKeycloakOpenIdConnect(
                    "keycloak", 
                    realm: "WeatherShop", 
                    options =>
                    {
                        options.ClientId = "WeatherWeb";
                        options.ResponseType = OpenIdConnectResponseType.Code;
                        options.Scope.Add("weather:all");
                    });
```

You can set many other options via the `Action<OpenIdConnectOptions>? configureOptions` delegate.

## AppHost extensions

In your AppHost project, install the `Aspire.Hosting.Keycloak` library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Keycloak
```

Then, in the _AppHost.cs_ file of `AppHost`, register a Keycloak server and consume the connection using the following methods:

```csharp
var keycloak = builder.AddKeycloak("keycloak", 8080);

var apiService = builder.AddProject<Projects.Keycloak_ApiService>("apiservice")
                        .WithReference(keycloak);

builder.AddProject<Projects.Keycloak_Web>("webfrontend")
       .WithExternalHttpEndpoints()
       .WithReference(keycloak)
       .WithReference(apiService);
```

**Recommendation:** For local development use a stable port for the Keycloak resource (8080 in the example above). It can be any port, but it should be stable to avoid issues with browser cookies that will persist OIDC tokens (which include the authority URL, with port) beyond the lifetime of the AppHost.

The `WithReference` method configures a connection in the `Keycloak.ApiService` and `Keycloak.Web` projects named `keycloak`.

In the _Program.cs_ file of `Keycloak.ApiService`, the Keycloak connection can be consumed using:

```csharp
builder.Services.AddAuthentication()
                .AddKeycloakJwtBearer("keycloak", realm: "WeatherShop");
```

And in the _Program.cs_ file of `Keycloak.Web`, the Keycloak connection can be consumed using:

```csharp
var oidcScheme = OpenIdConnectDefaults.AuthenticationScheme;

builder.Services.AddAuthentication(oidcScheme)
                .AddKeycloakOpenIdConnect(
                    "keycloak", 
                    realm: "WeatherShop", 
                    oidcScheme);
```

## Additional documentation

* https://www.keycloak.org/getting-started/getting-started-docker
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
