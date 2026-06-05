# Aspire.Azure.Security.KeyVault

Retrieves secrets from Azure Key Vault to use in your application. Registers a [SecretClient](https://learn.microsoft.com/dotnet/api/azure.security.keyvault.secrets.secretclient) in the DI container for connecting to Azure Key Vault. Enables corresponding health checks, logging and telemetry.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)
- Azure Key Vault - [create one](https://learn.microsoft.com/azure/key-vault/general/quick-create-portal).

### Install the package

Install the Aspire Azure Key Vault library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Azure.Security.KeyVault
```

## Usage examples

### Add secrets to configuration

In the _AppHost.cs_ file of your project, call the `builder.Configuration.AddAzureKeyVaultSecrets` extension method to add the secrets in the Azure Key Vault to the application's Configuration. The method takes a connection name parameter.

```csharp
builder.Configuration.AddAzureKeyVaultSecrets("secrets");
```

You can then retrieve a secret through normal `IConfiguration` APIs. For example, to retrieve a secret from a Web API controller:

```csharp
public ProductsController(IConfiguration configuration)
{
    string secretValue = configuration["secretKey"];
}
```

### Use SecretClient

Alternatively, you can use a `SecretClient` to retrieve the secrets on demand. In the _AppHost.cs_ file of your project, call the `AddAzureKeyVaultClient` extension method to register a `SecretClient` for use via the dependency injection container. The method takes a connection name parameter.

```csharp
builder.AddAzureKeyVaultClient("secrets");
```

You can then retrieve the `SecretClient` instance using dependency injection. For example, to retrieve the client from a Web API controller:

```csharp
private readonly SecretClient _client;

public ProductsController(SecretClient client)
{
    _client = client;
}
```

See the [Azure.Security.KeyVault.Secrets documentation](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/keyvault/Azure.Security.KeyVault.Secrets/README.md) for examples on using the `SecretClient`.

### Optionally include KeyClient and CertificateClient

You can also dependency inject a `KeyClient` and/or `CertificateClient` too:

```csharp
builder.AddAzureKeyVaultKeyClient("keys");
builder.AddAzureKeyVaultCertificateClient("certificates");
```

Which can then be retrieved in the same way the `SecretClient` is. For example , to retrieve a `KeyClient` from a Web API controller:

```csharp
private readonly KeyClient _client;

public ProductsController(KeyClient client)
{
    _client = client;
}
```

Or to retrieve a `CertificateClient` from a Web API controller:

```csharp
private readonly CertificateClient _client;

public ProductsController(CertificateClient client)
{
    _client = client;
}
```

See the [Azure.Security.KeyVault.Keys documentation](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/keyvault/Azure.Security.KeyVault.Keys/README.md) for examples on using the `KeyClient`.

See the [Azure.Security.KeyVault.Certificates documentation](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/keyvault/Azure.Security.KeyVault.Certificates/README.md) for examples on using the `CertificateClient`.

## Configuration

The Aspire Azure Key Vault library provides multiple options to configure the Azure Key Vault connection based on the requirements and conventions of your project. Note that the `VaultUri` is required to be supplied.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddAzureKeyVaultClient()`:

```csharp
builder.AddAzureKeyVaultClient("secretConnectionName");
```

And then the vault URI will be retrieved from the `ConnectionStrings` configuration section. The vault URI which works with the `AzureSecurityKeyVaultSettings.Credential` property to establish a connection. If no credential is configured, a [default TokenCredential is created based on the current environment](https://aka.ms/aspire/default-azure-credential).

```json
{
  "ConnectionStrings": {
    "secretConnectionName": "https://{account_name}.vault.azure.net/"
  }
}
```

### Use configuration providers

The Aspire Azure Key Vault library supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `AzureSecurityKeyVaultSettings` and `SecretClientOptions` from configuration by using the `Aspire:Azure:Security:KeyVault` key. Example `appsettings.json` that configures some of the options:

```json
{
  "Aspire": {
    "Azure": {
      "Security": {
        "KeyVault": {
          "DisableHealthChecks": true,
          "DisableTracing": false,
          "ClientOptions": {
            "Diagnostics": {
              "ApplicationId": "myapp"
            }
          }
        }
      }
    }
  }
}
```

### Use inline delegates

You can also pass the `Action<AzureSecurityKeyVaultSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

```csharp
builder.AddAzureKeyVaultClient("secrets", settings => settings.DisableHealthChecks = true);
```

You can also setup the [SecretClientOptions](https://learn.microsoft.com/dotnet/api/azure.security.keyvault.secrets.secretclientoptions) using the optional `Action<IAzureClientBuilder<SecretClient, SecretClientOptions>> configureClientBuilder` parameter of the `AddAzureKeyVaultClient` method. For example, to set the first part of "User-Agent" headers for all requests issues by this client:

```csharp
builder.AddAzureKeyVaultClient("secrets", configureClientBuilder: clientBuilder => clientBuilder.ConfigureOptions(options => options.Diagnostics.ApplicationId = "myapp"));
```

## AppHost extensions

In your AppHost project, install the Aspire Azure KeyVault Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.KeyVault
```

Then, in the _AppHost.cs_ file of `AppHost`, add a Key Vault connection and consume the connection using the following methods:

```csharp
// Service registration
var keyVault = builder.ExecutionContext.IsPublishMode
    ? builder.AddAzureKeyVault("secrets")
    : builder.AddConnectionString("secrets");

// Service consumption
var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(keyVault);
```

The `AddAzureKeyVault` method adds an Azure Key Vault resource to the builder. Or `AddConnectionString` can be used to read connection information from the AppHost's configuration (for example, from "user secrets") under the `ConnectionStrings:secrets` config key. The `WithReference` method passes that connection information into a connection string named `secrets` in the `MyService` project. In the _Program.cs_ file of `MyService`, the connection can be consumed using:

```csharp
builder.AddAzureKeyVaultClient("secrets");
```

## Additional documentation

* https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/keyvault/Azure.Security.KeyVault.Secrets/README.md
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
