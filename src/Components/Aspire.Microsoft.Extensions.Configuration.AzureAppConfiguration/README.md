# Aspire.Microsoft.Extensions.Configuration.AzureAppConfiguration

Retrieves configuration settings from Azure App Configuration to use in your application. Registers Azure App Configuration service as a configuration source. Enables corresponding logging and telemetry.

## Getting started

### Prerequisites

- Azure subscription - [create one for free](https://azure.microsoft.com/free/)
- Azure App Configuration - [create one](https://learn.microsoft.com/azure/azure-app-configuration/quickstart-azure-app-configuration-create).

### Install the package

Install the Aspire Azure App Configuration library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Microsoft.Extensions.Configuration.AzureAppConfiguration
```

## Usage examples

### Add App Configuration to configuration

In the _Program.cs_ file of your project, call the `builder.Configuration.AddAzureAppConfiguration` extension method to add key-values from Azure App Configuration to the application's Configuration. The method takes a connection name parameter.

```csharp
builder.AddAzureAppConfiguration("appConfig");
```

You can then retrieve a key-value through normal `IConfiguration` APIS. For example, to retrieve a key-value from a Web API controller:

```csharp
public MyController(IConfiguration configuration)
{
    string someValue = configuration["someKey"];
}
```

#### Use feature flags

To use feature flags, install the Feature Management library:

```dotnetcli
dotnet add package Microsoft.FeatureManagement
```

App Configuration will not load feature flags by default. To load feature flags, you can pass the `Action<AzureAppConfigurationOptions> configureOptions` delegate when calling `builder.AddAzureAppConfiguration`.

```csharp
builder.AddAzureAppConfiguration(
    "appConfig",
    configureOptions: options => options.UseFeatureFlags());

// Register feature management services
builder.Services.AddFeatureManagement();
```

You can then use `IVariantFeatureManager` to evaluate feature flags in your application:

```csharp
private readonly IVariantFeatureManager _featureManager;

public MyController(IVariantFeatureManager featureManager)
{
    _featureManager = featureManager;
}

[HttpGet]
public async Task<IActionResult> Get()
{
    if (await _featureManager.IsEnabledAsync("NewFeature"))
    {
        return Ok("New feature is enabled!");
    }

    return Ok("Using standard implementation.");
}
```

For information about using the Feature Management library, please go to the [documentation](https://learn.microsoft.com/azure/azure-app-configuration/feature-management-dotnet-reference).

## Configuration

The Aspire Azure App Configuration library provides multiple options to configure the Azure App Configuration connection based on the requirements and conventions of your project. Note that the App Config endpoint is required to be supplied, either in `AzureAppConfigurationSettings.Endpoint` or using a connection string.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddAzureAppConfiguration()`:

```csharp
builder.AddAzureAppConfiguration("appConfigConnectionName");
```

And then the App Configuration endpoint will be retrieved from the `ConnectionStrings` configuration section. The App Configuration store URI works with the `AzureAppConfigurationSettings.Credential` property to establish a connection. If no credential is configured, a [default TokenCredential is created based on the current environment](https://aka.ms/aspire/default-azure-credential).

```json
{
  "ConnectionStrings": {
    "appConfigConnectionName": "https://{store_name}.azconfig.io"
  }
}
```

### Use configuration providers

The Aspire Azure App Configuration library supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `AzureAppConfigurationSettings` from configuration by using the `Aspire:Microsoft:Extensions:Configuration:AzureAppConfiguration` key. Example `appsettings.json` that configures some of the options:

```json
{
  Aspire.Microsoft.Extensions.Configuration.AzureAppConfiguration
  "Aspire": {
    "Microsoft": {
      "Extensions": {
        "Configuration": {
          "AzureAppConfiguration": {
            "Endpoint": "YOUR_APPCONFIGURATION_ENDPOINT_URI"
          }
        }
      }
    }
  }
}
```

### Use inline delegates

You can also pass the `Action<AzureAppConfigurationSettings> configureSettings` delegate to set up some or all the options inline, for example to set App Configuration endpoint from code:

```csharp
builder.AddAzureAppConfiguration("appConfig", configureSettings: settings => settings.Endpoint = "http://YOUR_URI");
```

## AppHost extensions

In your AppHost project, install the Aspire Azure App Configuration Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.AppConfiguration
```

Then, in the _Program.cs_ file of `AppHost`, add a App Configuration connection and consume the connection using the following methods:

```csharp
// Service registration
var appConfig = builder.AddAzureAppConfiguration("appConfig");

// Service consumption
var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(appConfig);
```

The `AddAzureAppConfiguration` method adds an Azure App Configuration resource to the builder. Or `AddConnectionString` can be used to read connection information from the AppHost's configuration under the `ConnectionStrings:appConfig` config key. The `WithReference` method passes that connection information into a connection string named `appConfig` in the `MyService` project. In the _Program.cs_ file of `MyService`, the connection can be consumed using:

```csharp
builder.AddAzureAppConfiguration("appConfig");
```

## Additional documentation

* https://learn.microsoft.com/azure/azure-app-configuration/
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
