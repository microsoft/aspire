# Aspire.Hosting.Azure.AppService library

Provides extension methods and resource definitions for an Aspire AppHost to configure Azure App Service for the compute resources (like project).

## Getting started

### Prerequisites

- Azure subscription (requires Owner access to the target subscription for role assignments)

### Install the package

In your AppHost project, install the Aspire Azure App Service Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.Azure.AppService
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add an Azure App Service Environment and publish your project as an Azure App Service Web App:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var appServiceEnvironment = builder.AddAzureAppServiceEnvironment("env");

builder.AddProject<Projects.MyWebApp>("webapp")
    .WithExternalHttpEndpoints()
    .PublishAsAzureAppServiceWebsite((infrastructure, website) =>
    {
        // Customize the App Service health check path and appsettings
        website.SiteConfig.HealthCheckPath = "/health";
        website.SiteConfig.AppSettings.Add(new AppServiceNameValuePair()
        {
            Name = "Environment",
            Value = "Production"
        });
    });
```

## Azure App Service constraints

When deploying to Azure App Service, the following constraints apply:

- **External endpoints only**: App Service only supports external endpoints. All endpoints must be configured using `WithExternalHttpEndpoints()`.
- **HTTP/HTTPS only**: Only HTTP and HTTPS endpoints are supported. Other protocols are not supported.
- **Single endpoint**: App Service supports only a single target port. Resources with multiple external endpoints with different target ports are not supported. The default target port is 8000, which can be overridden using the `WithHttpEndpoint` extension method.

### Publishing compute resources to Azure App Service

The `PublishAsAzureAppServiceWebsite` extension method is used to configure a compute resource (such as a project) to be published as an Azure App Service Web App when deploying to Azure. This method allows you to customize the App Service Web App configuration using the Azure Provisioning SDK.

```csharp
builder.AddProject<Projects.MyApi>("api")
    .WithHttpEndpoint(targetPort: 8080)
    .WithExternalHttpEndpoints()
    .WithHealthProbe(ProbeType.Liveness, "/health")
    .WithArgs("--environment", "Production")
    .PublishAsAzureAppServiceWebsite((infrastructure, website) =>
    {
        // Customize the App Service Web App appsettings
        website.SiteConfig.IsWebSocketsEnabled = true;
        website.SiteConfig.MinTlsVersion = SupportedTlsVersions.Tls1_2;
    });
```

### Adding an Azure App Service Environment

The Azure App Service Environment resource creates the underlying infrastructure needed to host your applications, including:

- An Azure App Service Plan (default SKU: P0v3)
- An Azure Container Registry for storing container images
- A managed identity for accessing the container registry
- Optionally, the Aspire Dashboard as an Azure App Service Web App
- Optionally, Application Insights for monitoring and telemetry

```csharp
var appServiceEnvironment = builder.AddAzureAppServiceEnvironment("env");
```

By default, the Aspire Dashboard is included in the App Service Environment. To disable the dashboard, use the `WithDashboard` extension method:

```csharp
var appServiceEnvironment = builder.AddAzureAppServiceEnvironment("env")
    .WithDashboard(enable: false);
```

### Enabling Application Insights

Application Insights can be enabled for the App Service Environment using the `WithAzureApplicationInsights` extension method. A different location can be specified for Application Insights using the optional location parameter:

```csharp
var appServiceEnvironment = builder.AddAzureAppServiceEnvironment("env")
    .WithAzureApplicationInsights();
```

### Customizing the App Service Plan

The App Service Plan can be customized using the `ConfigureInfrastructure` extension method.

The default SKU for the App Service Plan is P0V3 and can be changed using this extension method:

```csharp
var appServiceEnvironment = builder.AddAzureAppServiceEnvironment("env")
    .ConfigureInfrastructure((infra) =>
    {
        var plan = infra.GetProvisionableResources().OfType<AppServicePlan>().Single();
        plan.Sku = new AppServiceSkuDescription
        {
            Name = "P2V3",
            Tier = "Premium"
        };
    });
```

## Additional documentation

* https://learn.microsoft.com/azure/app-service/

## Feedback & contributing

https://github.com/microsoft/aspire
