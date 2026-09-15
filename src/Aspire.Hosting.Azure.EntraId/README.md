# Aspire.Hosting.Azure.EntraId library

Provides extension methods and resource definitions for an Aspire AppHost to configure Microsoft Entra ID application registrations for authentication and authorization.

## Getting started

### Prerequisites

- Microsoft Entra ID tenant
- App registrations created in the [Azure Portal](https://portal.azure.com) or via the Entra ID provisioning skill

### Install the package

In your AppHost project, install the Aspire Entra ID Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.EntraId
```

In your service projects, install Microsoft.Identity.Web:

```dotnetcli
dotnet add package Microsoft.Identity.Web
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add an Entra ID application resource and inject the authentication configuration into consuming services:

```csharp
var tenantId = builder.AddParameter("EntraTenantId");
var apiClientId = builder.AddParameter("EntraApiClientId");

var entraApi = builder.AddEntraIdApplication("entra-api")
    .AsExisting(
        tenantId: tenantId,
        clientId: apiClientId);

builder.AddProject<Projects.Api>("api")
    .WithReference(entraApi);
```

In the API project's _Program.cs_, use Microsoft.Identity.Web directly — the configuration is automatically available via the `AzureAd` section:

```csharp
builder.Services.AddAuthentication()
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
```

For web applications that use OpenID Connect sign-in and call protected APIs:

```csharp
var webClientId = builder.AddParameter("EntraWebClientId");
var webSecret = builder.AddParameter("EntraWebClientSecret", secret: true);

var entraWeb = builder.AddEntraIdApplication("entra-web")
    .AsExisting(
        tenantId: tenantId,
        clientId: webClientId)
    .WithClientSecret(webSecret);

builder.AddProject<Projects.Web>("web")
    .WithReference(entraWeb);
```

In the web project's _Program.cs_:

```csharp
builder.Services.AddAuthentication()
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
    .EnableTokenAcquisitionToCallDownstreamApi()
    .AddInMemoryTokenCaches();
```

### Federated identity credential with managed identity

For production deployments, use FIC+MSI to avoid storing secrets:

```csharp
var entra = builder.AddEntraIdApplication("entra-web")
    .AsExisting(
        tenantId: tenantId,
        clientId: webClientId)
    .WithFicMsi();
```

### Managed certificate (Microsoft-internal)

For first-party Microsoft services, use managed certificates — zero secrets, automatic rotation:

```csharp
var entra = builder.AddEntraIdApplication("entra-web")
    .AsExisting(
        tenantId: tenantId,
        clientId: webClientId)
    .WithManagedCertificate();
```

### Certificate from Key Vault

```csharp
var entra = builder.AddEntraIdApplication("entra-web")
    .AsExisting(
        tenantId: tenantId,
        clientId: webClientId)
    .WithCertificateFromKeyVault("https://myvault.vault.azure.net", "MyCert");
```

### Certificate from store

```csharp
var entra = builder.AddEntraIdApplication("entra-web")
    .AsExisting(
        tenantId: tenantId,
        clientId: webClientId)
    .WithCertificateThumbprint("CurrentUser/My", "ABC123...");
```

### Advanced: custom credential

For credential types not covered by convenience methods, use `WithCredential` directly:

```csharp
var entra = builder.AddEntraIdApplication("entra-web")
    .AsExisting(
        tenantId: tenantId,
        clientId: webClientId)
    .WithCredential(new EntraIdSignedAssertionFileCredential());
```

### Sovereign clouds

To use a sovereign cloud instance (e.g., Azure Government):

```csharp
var entra = builder.AddEntraIdApplication("entra-api")
    .WithInstance("https://login.microsoftonline.us/")
    .AsExisting(
        tenantId: tenantId,
        clientId: clientId);
```

## How it works

The `WithReference` method injects environment variables like `AzureAd__TenantId`, `AzureAd__ClientId`, etc. into the consuming service. .NET's configuration system automatically maps these to the `AzureAd` configuration section that Microsoft.Identity.Web reads natively — no custom parsing or glue code needed.

## Additional documentation

* https://learn.microsoft.com/entra/identity-platform/
* https://aspire.dev/
* https://devblogs.microsoft.com/aspire/securing-dotnet-aspire-apps-with-microsoft-entra-id/

## Feedback & contributing

https://github.com/dotnet/aspire
