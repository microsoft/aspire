---
title: What's new in Aspire 9.4
description: Learn what's new in the official general availability release of Aspire 9.4.
ms.date: 07/29/2025
---

## What's new in Aspire 9.4

📢 Aspire 9.4 is the next minor version release of Aspire. It supports:

- .NET 8.0 Long Term Support (LTS)
- .NET 9.0 Standard Term Support (STS)
- .NET 10.0 Preview 6

If you have feedback, questions, or want to contribute to Aspire, collaborate with us on [:::image type="icon" source="../media/github-mark.svg" border="false"::: GitHub](https://github.com/microsoft/aspire) or join us on our new [:::image type="icon" source="../media/discord-icon.svg" border="false"::: Discord](https://aka.ms/aspire-discord) to chat with the team and other community members.

It's important to note that Aspire releases out-of-band from .NET releases. While major versions of Aspire align with major .NET versions, minor versions are released more frequently. For more information on .NET and Aspire version support, see:

- [.NET support policy](https://dotnet.microsoft.com/platform/support/policy): Definitions for LTS and STS.
- [Aspire support policy](https://dotnet.microsoft.com/platform/support/policy/aspire): Important unique product lifecycle details.

## ⬆️ Upgrade to Aspire 9.4

Moving between minor releases of Aspire is simple:

1. In your AppHost project file (that is, _MyApp.AppHost.csproj_), update the [📦 Aspire.AppHost.Sdk](https://www.nuget.org/packages/Aspire.AppHost.Sdk) NuGet package to version `9.4.0`:

    ```xml
    <Sdk Name="Aspire.AppHost.Sdk" Version="9.4.0" />
    ```

    For more information, see [Aspire SDK](xref:microsoft/aspire/sdk).

1. Check for any NuGet package updates, either using the NuGet Package Manager in Visual Studio or the **Update NuGet Package** command from C# Dev Kit in VS Code.
1. Update to the latest [Aspire templates](../fundamentals/aspire-sdk-templates.md) by running the following .NET command line:

    ```dotnetcli
    dotnet new install Aspire.ProjectTemplates
    ```

    > The `dotnet new install` command will update existing Aspire templates to the latest version if they are already installed.

If your AppHost project file doesn't have the `Aspire.AppHost.Sdk` reference, you might still be using Aspire 8. To upgrade to 9, follow [the upgrade guide](../get-started/upgrade-to-aspire-9.md).

## 🛠️ Aspire CLI is generally available

With the release of Aspire 9.4, the Aspire CLI is generally available. To install the Aspire CLI as an AOT compiled binary, use the following helper scripts:

```bash
# Bash
curl -sSL https://aspire.dev/install.sh | bash

# PowerShell
iex "& { $(irm https://aspire.dev/install.ps1) }"
```

This will install the CLI and put it on your PATH (the binaries are placed in the `$HOME/.aspire/bin` path). If you choose you can also install the CLI as a non-AOT .NET global tool using:

```dotnetcli
dotnet tool install -g Aspire.Cli
```

> [!NOTE]
> ⚠️ **The Aspire 9.4 CLI is not compatible with Aspire 9.3 projects.**
> You must upgrade your project to Aspire 9.4+ in order to use the latest CLI features.

### 🎯 CLI Commands

The Aspire CLI has the following [commands](../cli-reference/aspire.md):

- `aspire new`: Creates a new Aspire project from templates.
- `aspire run`: Finds and runs the existing apphost from anywhere in the repo.
- `aspire add`: Adds a hosting integration package to the apphost.
- `aspire config [get|set|delete|list]`: Configures Aspire settings and feature flags.
- `aspire publish` (Preview): Generates deployment artifacts based on the apphost.

In addition to these core commands, we have two beta commands behind [feature flags](../cli-reference/aspire-config.md):

- `aspire exec`: Invokes an arbitrary command in the context of an executable resource defined in the apphost (ie, inheriting its environment variables).
- `aspire deploy`: Extends the capabiltiies of `aspire publish` to actively deploy to a target environment.

#### `aspire exec`

The new `exec` command allows you to execute commands within the context of your Aspire application environment:

```bash
# Execute commands, like migrataions, with environment variables from your app model
aspire exec --resource my-api -- dotnet ef database update

# Run scripts with access to application context
aspire exec --start-resource my-worker -- npm run build

# The exec command automatically provides environment variables
# from your Aspire application resources to the executed command
```

**Key capabilities**:

- **Environment variable injection** from your app model resources
- **Resource targeting** with `--resource` or `--start-resource` options
- **Command execution** in the context of your Aspirified application

> [!IMPORTANT]
> 🧪 **Feature Flag**: The `aspire exec` command is behind a feature flag and **disabled by default** in this release. It must be explicitly enabled for use with `aspire config set features.execCommandEnabled true`.

#### `aspire deploy`

The `aspire deploy` command supports extensible deployment workflows through the new [`DeployingCallbackAnnotation`](../fundamentals/annotations-overview.md), enabling custom pre/post-deploy logic and richer integration with external systems during deployment operations.

**Key capabilities:**

- **Custom deployment hooks** using `Aspire.Hosting.Publishing.DeployingCallbackAnnotation` to execute custom logic during the `aspire deploy` command
- **Workflow activity reporting** via the <xref:Aspire.Hosting.Publishing.IPublishingActivityReporter> to support progress notifications and prompting in commmands
- **Integration with publish** - `aspire deploy` runs `Aspire.Hosting.Publishing.PublishingCallbackAnnotations` to support deploying artifacts emitted by publish steps, if applicable

The example below demonstrates using the `DeployingCallbackAnnotation` to register custom deployment behavior and showcases [CLI-based prompting](#-enhanced-publish-and-deploy-output) and progress notifications.

```csharp
#pragma warning disable ASPIREPUBLISHERS001
#pragma warning disable ASPIREINTERACTION001

using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;

var builder = DistributedApplication.CreateBuilder(args);

// Custom deployment step defined below
builder.AddDataSeedJob("SeedInitialData", seedDataPath: "data/seeds");

builder.Build().Run();

internal class DataSeedJobResource(string name, string seedDataPath)
    : Resource(name)
{
    public string SeedDataPath { get; } = seedDataPath;
}

internal static class DataSeedJobBuilderExtensions
{
    public static IResourceBuilder<DataSeedJobResource> AddDataSeedJob(
        this IDistributedApplicationBuilder builder,
        string name,
        string seedDataPath = "data/seeds")
    {
        var job = new DataSeedJobResource(name, seedDataPath);
        var resourceBuilder = builder.AddResource(job);

        // Attach a DeployingCallbackAnnotation that will be invoked on `aspire deploy`
        job.Annotations.Add(new DeployingCallbackAnnotation(async ctx =>
        {
            CancellationToken ct = ctx.CancellationToken;

            // Prompt the user for a confirmation using the interaction service
            var interactionService = ctx.Services.GetRequiredService<IInteractionService>();

            var envResult = await interactionService.PromptInputAsync(
                "Environment Configuration",
                "Please enter the target environment name:",
                new InteractionInput
                {
                    Label = "Environment Name",
                    InputType = InputType.Text,
                    Required = true,
                    Placeholder = "dev, staging, prod"
                },
                cancellationToken: ct);


            // Use the ActivityReporter to report progress on the seeding process
            var reporter = ctx.ActivityReporter;

            var step = await reporter.CreateStepAsync("Seeding data", ct);
            var task = await step.CreateTaskAsync($"Loading seed data from {seedDataPath}", ct);

            try
            {
                // Do some work here
                await Task.Delay(3000);

                await task.SucceedAsync("Seed data loaded", ct);
                await step.SucceedAsync("Data seeding completed", ct);
            }
            catch (Exception ex)
            {
                await task.FailAsync(ex.Message, ct);
                await step.FailAsync("Data seeding failed", ct);
                throw;
            }
        }));

        return resourceBuilder;
    }
}
```

This custom deployment logic executes as follows from the `aspire deploy` command.

![aspire-deploy-whats-new](https://github.com/user-attachments/assets/15c6730d-8154-496a-be70-c67257ce5523)

Now, integration owners can create sophisticated `aspire deploy` workflows. This work also provides a foundation for advanced deployment automation scenarios.

> [!NOTE]
> While the `Aspire.Hosting.Publishing.DeployingCallbackAnnotation` API is available in Aspire 9.4, there are currently no built-in resources that natively support deployment callbacks. Built-in resource support for deployment callbacks will be added in the next version of Aspire.
>
> [!IMPORTANT]
> 🧪 **Feature Flag**: The `aspire deploy` command is behind a feature flag and **disabled by default** in this release. It must be explicitly enabled for use with `aspire config set features.deployCommandEnabled true`

### 📃 Enhanced publish and deploy output

Aspire 9.4 significantly improves the feedback and progress reporting during publish and deploy operations, providing clearer visibility into what's happening during deployment processes.

**Key improvements:**

- **Enhanced progress reporting** with detailed step-by-step feedback during publishing
- **Cleaner output formatting** that makes it easier to follow deployment progress
- **Better error messaging** with more descriptive information when deployments fail
- **Improved publishing context** that tracks and reports on resource deployment status
- **Container build logs** provide clear status updates during container operations

These improvements make it much easier to understand what's happening during `aspire deploy` and `aspire publish` operations, helping developers debug issues more effectively and gain confidence in their deployment processes.

The enhanced output is particularly valuable for:

- **CI/CD pipelines** where clear logging is essential for troubleshooting
- **Complex deployments** with multiple resources and dependencies
- **Container-based deployments** where build and push operations need clear status reporting
- **Team environments** where deployment logs need to be easily interpreted by different team members

For more information about publishing and deploying Aspire apps, see [aspire deploy](../cli-reference/aspire-deploy.md).

## 🖥️ App model enhancements

### 🎛️ Interaction service

Aspire 9.4 introduces the [interaction service](../extensibility/interaction-service.md), a general service that allows developers to build rich experiences at runtime by extending the dashboard UX and at publish and deploy time using the Aspire CLI. It allows you to build complex interactions where input is required from the user.

> [!IMPORTANT]
> 🧪 This feature is experimental and may change in future releases.

:::image type="content" source="media/dashboard-interaction-service.gif" lightbox="media/dashboard-interaction-service.gif" alt-text="Recording of using the interaction service in the dashboard.":::

The interaction system supports:

- Confirmation prompts for destructive operations
- Input collection with validation
- Multi-step workflows
- Dashboard interactions during run mode
- CLI interactions during deploy and publish operations

```csharp
// Example usage of IInteractionService APIs
public class DeploymentService
{
    private readonly IInteractionService _interactionService;

    public DeploymentService(IInteractionService interactionService)
    {
        _interactionService = interactionService;
    }

    public async Task DeployAsync()
    {
        // Prompt for confirmation before destructive operations
        var confirmResult = await _interactionService.PromptConfirmationAsync(
            "Confirm Deployment", 
            "This will overwrite the existing deployment. Continue?");

        if (confirmResult.Canceled || !confirmResult.Data)
        {
            return;
        }

        // Collect multiple inputs with validation
        var regionInput = new InteractionInput { Label = "Region", InputType = InputType.Text, Required = true };
        var instanceCountInput = new InteractionInput { Label = "Instance Count", InputType = InputType.Number, Required = true };
        var enableMonitoringInput =  new InteractionInput { Label = "Enable Monitoring", InputType = InputType.Boolean };

        var multiInputResult = await _interactionService.PromptInputsAsync(
            "Advanced Configuration",
            "Configure deployment settings:",
            [regionInput, instanceCountInput, enableMonitoringInput],
            new InputsDialogInteractionOptions
            {
                ValidationCallback = async context =>
                {
                    if (!IsValidRegion(regionInput.Value))
                    {
                        context.AddValidationError(regionInput, "Invalid region specified");
                    }
                }
            });

        if (multiInputResult.Canceled)
        {
            return;
        }

        await RunDeploymentAsync(
            region: regionInput.Value,
            instanceCount: instanceCountInput.Value,
            enableMonitoring: enableMonitoringInput.Value);

        // Show progress notifications
        await _interactionService.PromptNotificationAsync(
            "Deployment Status",
            "Deployment completed successfully!",
            new NotificationInteractionOptions
            {
                Intent = MessageIntent.Success,
                LinkText = "View Dashboard",
                LinkUrl = "https://portal.azure.com"
            });
    }

    private bool IsValidRegion(string? region) 
    {
        // Validation logic here
        return !string.IsNullOrEmpty(region);
    }
}
```

**Input types supported:**

- `Text` - Standard text input
- `SecretText` - Password/secret input (masked)
- `Choice` - Dropdown selection
- `Boolean` - Checkbox input
- `Number` - Numeric input

**Advanced features:**

- **Validation callbacks** for complex input validation
- **Markdown support** for rich text descriptions
- **Custom button text** and dialog options
- **Intent-based styling** for different message types
- **Link support** in notifications

These interactions work seamlessly whether you're running your application through the [Aspire dashboard](#-dashboard-improvements) or deploying via the CLI with `aspire deploy` and `aspire publish` commands.

### 🔄 Interactive parameter prompting during run mode

Aspire 9.4 introduces interactive parameter prompting, automatically collecting missing parameter values in the dashboard during application startup through the new [interaction service](#️-interaction-service).

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Parameters without default values will trigger prompts
var apiKey = builder.AddParameter("api-key", secret: true);
var dbPassword = builder.AddParameter("db-password", secret: true);

// This also works for values that could be defined in appsettings.json
var environment = builder.AddParameterFromConfiguration("environment", "ENVIRONMENT_VARIABLE");

// Application will prompt for these values if not provided
var database = builder.AddPostgres("postgres", password: dbPassword);
var api = builder.AddProject<Projects.Api>("api")
    .WithEnvironment("API_KEY", apiKey)
    .WithEnvironment("ENVIRONMENT", environment)
    .WithReference(database);

builder.Build().Run();
```

**Interactive experience:**

- **Automatically detects parameters** that are missing so there aren't startup failures
- **Dashboard prompts** with interactive forms and Markdown-enabled parameter descriptions
- **Validation support** for enforcing rules (required, length, casing, etc)
- **Secret masking** so sensitive input isn't shown while being entered
- **Save to user secrets** for persistent per-project value storage outside of source control

This feature eliminates the need to pre-configure all parameters in appsettings.json or .env files before running your Aspirified application, so you can clone, run, and be guided through what values are needed to run the full stack.

#### 📝 Enhanced parameter descriptions and custom input rendering

Building on the interactive parameter prompting capabilities and the new [interaction service](#️-interaction-service), Aspire 9.4 introduces rich parameter descriptions and custom input rendering to provide better user guidance and specialized input controls during parameter collection.

- **Aspire.Hosting.ParameterResourceBuilderExtensions.WithDescription** - Add helpful descriptions to guide users during parameter input
- **Markdown support** - Rich text descriptions with links, formatting, and lists using `enableMarkdown: true`
- **Aspire.Hosting.ParameterResourceBuilderExtensions.WithCustomInput** - Create specialized input controls for specific parameter types

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Parameters with descriptions provide better user guidance
var apiKey = builder.AddParameter("api-key", secret: true)
    .WithDescription("API key for external service authentication");

var environment = builder.AddParameter("environment")
    .WithDescription("Target deployment environment (dev, staging, prod)");

// Parameters with rich markdown descriptions
var configValue = builder.AddParameter("config-value")
    .WithDescription("""
        Configuration value with detailed instructions:
        
        - Use **development** for local testing
        - Use **staging** for pre-production validation  
        - Use **production** for live deployments
        
        See [configuration guide](https://docs.company.com/config) for details.
        """, enableMarkdown: true);

// Custom input rendering for specialized scenarios
var workerCount = builder.AddParameter("worker-count")
    .WithDescription("Number of background worker processes")
    .WithCustomInput(p => new InteractionInput
    {
        InputType = InputType.Number,
        Label = "Worker Count",
        Placeholder = "Enter number (1-10)",
        Description = p.Description
    });

var deploymentRegion = builder.AddParameter("region")
    .WithDescription("Azure region for deployment")
    .WithCustomInput(p => new InteractionInput
    {
        InputType = InputType.Choice,
        Label = "Deployment Region",
        Description = p.Description,
        Options = new[]
        {
            KeyValuePair.Create("eastus", "East US"),
            KeyValuePair.Create("westus", "West US"),
            KeyValuePair.Create("northeurope", "North Europe"),
            KeyValuePair.Create("southeastasia", "Southeast Asia")
        }
    });

var api = builder.AddProject<Projects.Api>("api")
    .WithEnvironment("API_KEY", apiKey)
    .WithEnvironment("ENVIRONMENT", environment)
    .WithEnvironment("CONFIG_VALUE", configValue)
    .WithEnvironment("WORKER_COUNT", workerCount)
    .WithEnvironment("REGION", deploymentRegion);

builder.Build().Run();
```

For more information, including supported input types, see the [Interaction Service section](#️-interaction-service) below or the full [interaction service docs](../extensibility/interaction-service.md).

### 🌐 External service modeling

Modern applications frequently need to integrate with external APIs, third-party services, or existing infrastructure that isn't managed by Aspire. Aspire 9.4 introduces first-class support for [modeling external services](../fundamentals/orchestrate-resources.md#express-external-service-resources) as resources in your application graph.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Reference an external service by URL
var externalApi = builder.AddExternalService("external-api", "https://api.company.com");

// Or use a parameter for dynamic configuration
var apiUrl = builder.AddParameter("api-url");
var externalDb = builder.AddExternalService("external-db", apiUrl)
    .WithHttpHealthCheck("/health");

var myService = builder.AddProject<Projects.MyService>("my-service")
    .WithReference(externalApi)
    .WithReference(externalDb);

builder.Build().Run();
```

External services appear in the Aspire dashboard with health status, can be referenced like any other resource, and support the same configuration patterns as internal resources.

### 🔗 Enhanced endpoint URL support

Aspire 9.4 introduces support for [non-localhost URLs](../fundamentals/networking-overview.md), making it easier to work with custom domains and network configurations. This includes support for `*.localhost` subdomains and automatic generation of multiple URL variants for endpoints listening on multiple addresses.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Endpoints targeting all addresses automatically get multiple URL variants
var api = builder.AddProject<Projects.Api>("api")
    .WithEndpoint("https", e => e.TargetHost = "0.0.0.0");

// Machine name URLs for external access  
var publicService = builder.AddProject<Projects.PublicService>("public")
    .WithEndpoint("https", e => e.TargetHost = "0.0.0.0");

builder.Build().Run();
```

**Key capabilities:**

- **Custom `*.localhost` subdomain support** that maintains localhost behavior
- **Automatic endpoint URL generation** for endpoints listening on multiple addresses, with both localhost and machine name URLs (such as Codespaces)
- **All URL variants** appear in the Aspire dashboard for easy access
- **Network flexibility** for development scenarios requiring specific network configurations
- **Launch profile configuration support** so custom URLs can also be configured via launch profiles in `launchSettings.json`:

```json
{
  "profiles": {
    "https": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": true,
      "applicationUrl": "https://*:7001;http://*:5001",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    }
  }
}
```

This simplifies development workflows where custom domains or external network access is needed while maintaining the familiar localhost development experience. A popular example includes SaaS solutions which use custom domains per-tenant.

### 🐳 Enhanced persistent container support

Aspire 9.4 improves support for [persistent containers](../app-host/persistent-containers.md) with better lifecycle management and networking capabilities, ensuring containers can persist across application restarts while maintaining proper connectivity.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Persistent containers with improved lifecycle support
var database = builder.AddPostgres("postgres")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithExplicitStart(); // Better support for explicit start with persistent containers

// Persistent containers automatically also get persistent networking
var redis = builder.AddRedis("redis")
    .WithLifetime(ContainerLifetime.Persistent);

var api = builder.AddProject<Projects.Api>("api")
    .WithReference(database)
    .WithReference(redis);

builder.Build().Run();
```

**Enhanced capabilities:**

- **Improved lifecycle coordination** between `Aspire.Hosting.ResourceBuilderExtensions.WithExplicitStart` and `ContainerLifetime.Persistent`
- **Automatic persistent networking** spun up when persistent containers are detected
- **Container delay start** for more reliable startup sequencing
- **Network isolation** between persistent and session-scoped containers, which now use separate networks for better resource management

This will greatly improve your experience while building stateful services that persist beyond individual application runs.

### 🎛️ Resource command service

Aspire 9.4 introduces `Aspire.Hosting.ApplicationModel.ResourceCommandService`, an API for executing commands against resources. You can now easily execute the commands that appear in the dashboard programmatically. For example, when writing unit tests for commands, or having other integrations in Aspire execute commands.

The example below uses `ResourceCommandService` to have a command execute other commands.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var database = builder.AddPostgres("postgres")
    .WithHttpCommand("admin-restart", "Restart Database", 
        commandName: "db-restart",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Post,
            Description = "Restart the PostgreSQL database"
        });

var cache = builder.AddRedis("cache")
    .WithHttpCommand("admin-flush", "Flush Cache",
        commandName: "cache-flush",
        commandOptions: new HttpCommandOptions
        {
            Method = HttpMethod.Delete,
            Description = "Clear all cached data"
        });

// Add a composite command that coordinates multiple operations
var api = builder.AddProject<Projects.Api>("api")
    .WithReference(database)
    .WithReference(cache)
    .WithCommand("reset-all", "Reset Everything", async (context, ct) =>
    {
        var logger = context.ServiceProvider.GetRequiredService<ILogger<Program>>();
        var commandService = context.ServiceProvider.GetRequiredService<ResourceCommandService>();
        
        logger.LogInformation("Starting full system reset...");
        
        try
        {
            var flushResult = await commandService.ExecuteCommandAsync(cache.Resource, "cache-flush", ct);
            var restartResult = await commandService.ExecuteCommandAsync(database.Resource, "db-restart", ct);
            if (!restartResult.Success || !flushResult.Success)
            {
                return CommandResults.Failure($"System reset failed");
            }
            
            logger.LogInformation("System reset completed successfully");
            return CommandResults.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "System reset failed");
            return CommandResults.Failure(ex);
        }
    },
    displayDescription: "Reset cache and restart database in coordinated sequence",
    iconName: "ArrowClockwise");

builder.Build().Run();
```

`ResourceCommandService` can also be used in unit tests:

```csharp
[Fact]
public async Task Should_ResetCache_WhenTestStarts()
{
    var builder = DistributedApplication.CreateBuilder();
    
    // Add cache with reset command for testing
    var cache = builder.AddRedis("test-cache")
        .WithHttpCommand("reset", "Reset Cache",
            commandName: "reset-cache",
            commandOptions: new HttpCommandOptions
            {
                Method = HttpMethod.Delete,
                Description = "Clear all cached test data"
            });

    var api = builder.AddProject<Projects.TestApi>("test-api")
        .WithReference(cache);

    await using var app = builder.Build();
    await app.StartAsync();
    
    // Reset cache before running test
    var result = await app.ResourceCommands.ExecuteCommandAsync(
        cache.Resource, 
        "reset-cache", 
        CancellationToken.None);
        
    Assert.True(result.Success, $"Failed to reset cache: {result.ErrorMessage}");
}
```

### 🔄 Resource lifecycle events

Aspire 9.4 introduces convenient extension methods on `Aspire.Hosting.ApplicationModel.IResourceBuilder` that make it much easier to subscribe to [lifecycle events](../app-host/eventing.md#app-host-life-cycle-events) directly on resources, providing a cleaner and more intuitive API.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var database = builder.AddPostgres("postgres")
                .AddDatabase("mydb")
                .OnConnectionStringAvailable(async (resource, evt, cancellationToken) =>
                {
                    // Log when connection strings are resolved
                    var logger = evt.Services.GetRequiredService<ILogger<Program>>();
                    logger.LogInformation("Connection string available for {Name}", resource.Name);
                });

var api = builder.AddProject<Projects.Api>("api")
                .WithReference(database)
                .OnInitializeResource(async (resource, evt, cancellationToken) =>
                {
                    // Early resource initialization
                    var logger = evt.Services.GetRequiredService<ILogger<Program>>();
                    logger.LogInformation("Initializing resource {Name}", resource.Name);
                })
                .OnBeforeResourceStarted(async (resource, evt, cancellationToken) =>
                {
                    // Pre-startup validation or configuration
                    var serviceProvider = evt.Services;
                    // Additional validation logic here
                })
                .OnResourceEndpointsAllocated(async (resource, evt, cancellationToken) =>
                {
                    // React to endpoint allocation
                    var logger = evt.Services.GetRequiredService<ILogger<Program>>();
                    logger.LogInformation("Endpoints allocated for {Name}", resource.Name);
                })
                .OnResourceReady(async (resource, evt, cancellationToken) =>
                {
                    // Resource is fully ready
                    var logger = evt.Services.GetRequiredService<ILogger<Program>>();
                    logger.LogInformation("Resource {Name} is ready", resource.Name);
                });

// Example: Database seeding using OnResourceReady
var db = builder.AddMongoDB("mongo")
    .WithMongoExpress()
    .AddDatabase("db")
    .OnResourceReady(async (db, evt, ct) =>
    {
        // Seed the database with initial data
        var connectionString = await db.ConnectionStringExpression.GetValueAsync(ct);
        using var client = new MongoClient(connectionString);
        
        var myDb = client.GetDatabase("db");
        await myDb.CreateCollectionAsync("entries", cancellationToken: ct);
        
        // Insert sample data
        for (int i = 0; i < 10; i++)
        {
            await myDb.GetCollection<Entry>("entries").InsertOneAsync(new Entry(), cancellationToken: ct);
        }
    });

builder.Build().Run();
```

**Available lifecycle events:**

- `OnInitializeResource` - Called during early resource initialization
- `OnBeforeResourceStarted` - Called before the resource starts
- `OnConnectionStringAvailable` - Called when connection strings are resolved (requires `IResourceWithConnectionString`)
- `OnResourceEndpointsAllocated` - Called when resource endpoints are allocated (requires `IResourceWithEndpoints`)
- `OnResourceReady` - Called when the resource is fully ready

The new chainable fluent API, strongly-typed callbacks, and simplified syntax make it intuitive to hook into your resource lifecycles for interactions, commands, custom scripts, and more.

**Migration from manual eventing:**

```csharp
// ❌ Before (manual eventing subscription):
builder.Eventing.Subscribe<ResourceReadyEvent>(db.Resource, async (evt, ct) =>
{
    // Manual event handling with no type safety
    var cs = await db.Resource.ConnectionStringExpression.GetValueAsync(ct);
    // Process event...
});

// ✅ After (fluent extension methods):
var db = builder.AddMongoDB("mongo")
    .AddDatabase("db")
    .OnResourceReady(async (db, evt, ct) =>
    {
        // Direct access to strongly-typed resource
        var cs = await db.ConnectionStringExpression.GetValueAsync(ct);
        // Process event...
    });
```

The new extension methods make it much easier to implement common patterns like database seeding, configuration validation, and resource health checks. Note that the old mechanism is not being deprecated, the new methods simply provide a more natural programming model when using the builder pattern.

### 📁 Enhanced container file mounting

Configuring container file systems often requires understanding complex Docker volume syntax and managing file permissions manually. Aspire 9.4 introduces enhanced file mounting APIs that handle common scenarios with sensible defaults.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Simple file copying from local source to container
var myContainer = builder.AddContainer("myapp", "myapp:latest")
    .WithContainerFiles("/app/config", "./config-files")
    .WithContainerFiles("/app/data", "./data", defaultOwner: 1000, defaultGroup: 1000)
    .WithContainerFiles("/app/scripts", "./scripts", umask: UnixFileMode.UserRead | UnixFileMode.UserWrite);

// You can also use the callback approach for dynamic file generation
var dynamicContainer = builder.AddContainer("worker", "worker:latest")
    .WithContainerFiles("/app/runtime-config", async (context, ct) =>
    {
        // Generate configuration files dynamically
        var configFile = new ContainerFileSystemItem
        {
            Name = "app.json",
            Contents = JsonSerializer.SerializeToUtf8Bytes(new { Environment = "Production" })
        };
        
        return new[] { configFile };
    });

builder.Build().Run();
```

The [enhanced APIs](../fundamentals/persist-data-volumes.md) handle file permissions, ownership, and provide both static and dynamic file mounting capabilities while maintaining the flexibility to customize when needed.

### ✨ Advanced YARP routing with transform APIs (Preview)

> [!NOTE]
> The [YARP integration](../proxies/yarp-integration.md) is currently in preview and APIs may change in future releases.

Building sophisticated reverse proxy configurations has traditionally required deep knowledge of YARP's transform system and manual JSON configuration. Aspire 9.4 introduces a comprehensive set of fluent APIs that make advanced routing transformations accessible through strongly-typed C# code.

**Breaking change in 9.4:** The `WithConfigFile()` method has been removed and replaced with a code-based configuration model. This new approach works seamlessly with deployment scenarios as the strongly-typed configuration methods translate directly into the appropriate environment variables.

You can now programmatically configure request/response transformations, header manipulation, path rewriting, and query string handling directly from your app model—no more wrestling with complex configuration files.

#### Example 1: Simple path-based routing with path prefix removal

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var apiV1 = builder.AddProject<Projects.ApiV1>("api-v1");
var apiV2 = builder.AddProject<Projects.ApiV2>("api-v2");

var yarp = builder.AddYarp("gateway")
    .WithConfiguration(yarpBuilder =>
    {
        // Route /v1/* requests to api-v1, removing the /v1 prefix
        yarpBuilder.AddRoute("/v1/{**catch-all}", apiV1)
            .WithTransformPathRemovePrefix("/v1");

        // Route /v2/* requests to api-v2, removing the /v2 prefix  
        yarpBuilder.AddRoute("/v2/{**catch-all}", apiV2)
            .WithTransformPathRemovePrefix("/v2");
    });

builder.Build().Run();
```

#### Example 2: Host-based routing

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var adminApi = builder.AddProject<Projects.AdminApi>("admin-api");
var publicApi = builder.AddProject<Projects.PublicApi>("public-api");

var yarp = builder.AddYarp("gateway")
    .WithConfiguration(yarpBuilder =>
    {
        // Route admin.example.com to admin API
        yarpBuilder.AddRoute(adminApi)
            .WithMatchHosts("admin.example.com");

        // Route api.example.com to public API  
        yarpBuilder.AddRoute(publicApi)
            .WithMatchHosts("api.example.com");

        // Default route for any other host
        yarpBuilder.AddRoute("/{**catch-all}", publicApi);
    });

builder.Build().Run();
```

#### Example 3: Advanced routing with comprehensive transforms

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var backendApi = builder.AddProject<Projects.BackendApi>("backend-api");
var identityService = builder.AddProject<Projects.Identity>("identity-service");

var yarp = builder.AddYarp("gateway")
    .WithConfiguration(yarpBuilder =>
    {
        // Configure sophisticated routing with transforms
        yarpBuilder.AddRoute("/api/v1/{**catch-all}", backendApi)
            .WithTransformPathPrefix("/v2")  // Rewrite /api/v1/* to /v2/*
            .WithTransformRequestHeader("X-API-Version", "2.0")
            .WithTransformForwarded(useHost: true, useProto: true)
            .WithTransformResponseHeader("X-Powered-By", "Aspire Gateway");

        // Advanced header and query manipulation
        yarpBuilder.AddRoute("/auth/{**catch-all}", identityService)
            .WithTransformClientCertHeader("X-Client-Cert")
            .WithTransformQueryValue("client_id", "aspire-app")
            .WithTransformRequestHeadersAllowed("Authorization", "Content-Type")
            .WithTransformUseOriginalHostHeader(false);
    });

builder.Build().Run();
```

#### Migration from YARP 9.3 to 9.4

If you were using `WithConfigFile()` in Aspire 9.3, you'll need to migrate to the new code-based configuration model shown above. The strongly-typed APIs provide better IntelliSense support and work seamlessly with deployment scenarios.

> [!NOTE]
> We are working on a more general-purpose solution for file-based configuration during deployment. File-based configuration support will return in a future version of Aspire.

This eliminates the need for complex YARP configuration files while providing complete access to YARP's powerful transformation pipeline through a fluent API.

### 🔒 Enhanced Docker Compose deployment security

Aspire 9.4 improves [Docker Compose publish](../deployment/overview.md) security with smart port mapping - only external endpoints are exposed to the host while internal services use Docker's internal networking.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var compose = builder.AddDockerComposeEnvironment("production");

// Add a service with both internal and external endpoints
var webService = builder.AddContainer("webservice", "nginx")
    .WithEndpoint(scheme: "http", port: 8080, name: "internal")       // Internal endpoint
    .WithEndpoint(scheme: "http", port: 8081, name: "api", isExternal: true); // External endpoint

builder.Build().Run();
```

**Generated Docker Compose output:**

```yaml
services:
  webservice:
    image: "nginx:latest"
    ports:
      - "8081:8001"    # Only external endpoints get port mappings (host:container)
    expose:
      - "8000"         # Internal endpoints use expose (container port only)
    networks:
      - "aspire"
```

Now, only `isExternal: true` endpoints are exposed to host, and internal endpoints use Docker's `expose` for container-to-container communication.

## 🎨 Dashboard improvements

> [!TIP]
> For a bite sized look at many of the 9.4 dashboard changes, James Newton-King has kept up his tradition of posting one new dashboard feature a day leading up to an Aspire release on his [BlueSky](https://bsky.app/profile/james.newtonking.com)!

### 🔔 Automatic upgrade check notifications

Aspire 9.4 includes an update notification system that automatically checks for newer versions and notifies developers when updates are available, making sure you stay current with the latest improvements and security updates.

When a newer version is detected, a friendly notification appears in the Aspire dashboard:

:::image type="content" source="media/dashboard-update-notification.png" lightbox="media/dashboard-update-notification.png" alt-text="Screenshot of dashboard showing an update notification.":::

Aspire only shows notifications when a newer version is available, and the checks happen in the background without impacting application startup or performance. The upgrade check system can be disable by setting the `ASPIRE_VERSION_CHECK_DISABLED` environment variable to `true`. For more information, see [App host configuration](/microsoft/aspire/app-host/configuration).

### 📋 Parameters and connection strings visible in dashboard

Aspire 9.4 makes parameters and connection strings visible in the Aspire dashboard, providing better visibility into your application's configuration and connectivity status during development.

Connection strings:

- Appear in the **resource details** panel for any resource that implements `IResourceWithConnectionString`
- Values are marked as **sensitive** and can be toggled for visibility in the dashboard
- Supports all resource types including databases, message brokers, and custom resources

:::image type="content" source="media/dashboard-connection-strings.png" lightbox="media/dashboard-connection-strings.png" alt-text="Screenshot of dashboard showing connection string.":::

External parameters are no longer hidden. The parameter state and value is visible in the dashboard.

:::image type="content" source="media/dashboard-parameters.png" lightbox="media/dashboard-parameters.png" alt-text="Screenshot of dashboard showing parameters.":::

For more information, see [external parameters](/microsoft/aspire/fundamentals/external-parameters).

### 🔗 Enhanced dashboard peer visualization for uninstrumented resources

Aspire 9.4 lets you observe connections between resources even when they aren't instrumented with telemetry.

For example, the screenshot below shows a call to a GitHub model resolving to the model resource in Aspire:

:::image type="content" source="media/dashboard-tracing-peers.png" lightbox="media/dashboard-tracing-peers.png" alt-text="Screenshot of a span linked to a GitHub model resource defined in Aspire.":::

OpenTelemetry spans can now resolve to peers that are defined by parameters, connection strings, GitHub Models, and external services:

- **Connection string parsing** supports SQL Server, PostgreSQL, MySQL, MongoDB, Redis, and many other connection string formats
- **Visualize parameters** with URLs or connection strings and how they connect to services
- **GitHub Models integration** for GitHub-hosted AI models with proper state management
- **External service mapping** between your services and external dependencies

### 📋 Console logs text wrapping control

Aspire 9.4 introduces a new toggle option in the dashboard console logs to control text wrapping behavior, giving you better control over how long log lines are displayed.

:::image type="content" source="media/dashboard-console-logs-wrapping.gif" lightbox="media/dashboard-console-logs-wrapping.gif" alt-text="Recording of toggling line wrapping on console logs page.":::

Some Aspire users have run into trouble with viewing large console logs, which is tracked in this GitHub issue: [Console logs not showing, plus browser window size affecting displayed logs #7969](https://github.com/microsoft/aspire/issues/7969). If you're having trouble with logs please try experimenting with disabling wrapping and see whether it improves your user experience. Feedback on this issue would be very helpful.

### 👁️ Show/hide hidden resources in dashboard

Aspire 9.4 introduces the ability to show or hide hidden resources in the dashboard, giving you complete visibility into your application's infrastructure components and internal resources that are normally hidden from view.

:::image type="content" source="media/dashboard-hidden-resources.png" lightbox="media/dashboard-hidden-resources.png" alt-text="Dashboard resources page with the show/hide hidden resources UI visible.":::

If there are no hidden resources in your Aspire app then the show/hide UI is disabled.

### 🏗️ Enhanced dashboard infrastructure with proxied endpoints

Aspire 9.4 introduces significant infrastructure improvements to the dashboard system, implementing proxied endpoints that make dashboard launching more reliable and avoiding port reuse problems. This architectural enhancement resolves issues with dashboard connectivity during application startup and shutdown scenarios. The UI when the dashboard is attempting to reconnect has also been updated to be more reliable and with a new cohesive look and animation.

### 🐳 Docker Compose with integrated Aspire Dashboard

Managing observability in Docker Compose environments often requires running separate monitoring tools or losing the rich insights that Aspire provides during development. Aspire 9.4 introduces native Aspire Dashboard integration for Docker Compose environments.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var compose = builder.AddDockerComposeEnvironment("production")
                    .WithDashboard(dashboard => dashboard.WithHostPort(8080)); // Configure dashboard with specific port

// Add services that will automatically report to the dashboard
builder.AddProject<Projects.Frontend>("frontend");
builder.AddProject<Projects.Api>("api");

builder.Build().Run();
```

## 🔗 Updated integrations

### 🐙 GitHub Models integration

Aspire 9.4 introduces support for [GitHub Models](https://docs.github.com/en/github-models), enabling easy integration with AI models hosted on GitHub's platform. This provides a simple way to incorporate AI capabilities into your applications using GitHub's model hosting service.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Add GitHub Model - API key parameter is automatically created
var model = builder.AddGitHubModel("chat-model", "gpt-4o-mini");

// You can also specify an API key explicitly if needed
var apiKey = builder.AddParameter("github-api-key", secret: true);
var explicitModel = builder.AddGitHubModel("explicit-chat", "gpt-4o-mini")
    .WithApiKey(apiKey);

// Use the model in your services
var chatService = builder.AddProject<Projects.ChatService>("chat")
    .WithReference(model);

builder.Build().Run();
```

The [GitHub Models integration](../github/github-models-integration.md) provides:

- **Simple model integration** with GitHub's hosted AI models
- **Automatic API key parameter creation** with the pattern `{name}-gh-apikey`
- **Explicit API key support** using `WithApiKey()` for custom scenarios
- **GITHUB_TOKEN fallback** when no explicit API key is provided
- **Built-in health checks** for model availability

### 🤖 Azure AI Foundry integration

Aspire 9.4 introduces comprehensive [Azure AI Foundry](https://ai.azure.com/) support, bringing enterprise AI capabilities directly into your distributed applications. This integration simplifies working with AI models and deployments through the Azure AI platform, supporting both Azure-hosted deployments and local development with [Foundry Local](https://github.com/microsoft/Foundry-Local).

#### Hosting configuration

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Add Azure AI Foundry project
var foundry = builder.AddAzureAIFoundry("foundry");

// Add specific model deployments
var chat = foundry.AddDeployment("chat", "qwen2.5-0.5b", "1", "Microsoft");
var embedding = foundry.AddDeployment("embedding", "text-embedding-ada-002", "2", "OpenAI");

// Connect your services to AI capabilities
var webService = builder.AddProject<Projects.WebService>("webservice")
    .WithReference(chat)
    .WaitFor(chat);

builder.Build().Run();
```

##### Azure AI Foundry Local support

[Azure AI Foundry Local](https://learn.microsoft.com/azure/ai-foundry/foundry-local/) is an on-device AI inference solution that runs models locally on your hardware, providing performance, privacy, and cost advantages without requiring an Azure subscription. It's ideal for scenarios requiring data privacy, offline operation, cost reduction, or low-latency responses.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// For local development, run with Foundry Local
var localFoundry = builder.AddAzureAIFoundry("foundry")
    .RunAsFoundryLocal()
    .AddDeployment("chat", "phi-3.5-mini", "1", "Microsoft");

var webService = builder.AddProject<Projects.WebService>("webservice")
    .WithReference(localFoundry)
    .WaitFor(localFoundry);

builder.Build().Run();
```

#### Client integration

Once you've configured the [Azure AI Foundry resource](../azureai/azureai-foundry-integration.md) in your app host, consume it in your services using the [Azure AI Inference SDK](../azureai/azureai-inference-integration.md) or [OpenAI SDK](../azureai/azureai-openai-integration.md) for compatible models:

**Using Azure AI Inference SDK:**

```csharp
// In Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.AddAzureChatCompletionsClient("chat")
       .AddChatClient();

var app = builder.Build();

// Minimal API endpoint for chat completion
app.MapPost("/generate", async (IChatClient chatClient, ChatRequest request) =>
{
    var messages = new List<ChatMessage>
    {
        new(ChatRole.System, "You are a helpful assistant."),
        new(ChatRole.User, request.Prompt)
    };

    var response = await chatClient.GetResponseAsync(messages);
    return Results.Ok(new { Response = response.Text });
});

app.Run();

public record ChatRequest(string Prompt);
```

**Using OpenAI SDK (for compatible models):**

```csharp
// In Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.AddOpenAIClient("chat")
       .AddChatClient();

// Usage is identical to the Azure AI Inference SDK example above
```

**Key differences between Azure AI Foundry and Foundry Local:**

- **Azure AI Foundry** - Cloud-hosted models with enterprise-grade scaling, supports all Azure AI model deployments
- **Foundry Local** - On-device inference with different model selection optimized for local hardware, no Azure subscription required

The `RunAsFoundryLocal()` method enables local development scenarios using [Azure AI Foundry Local](https://learn.microsoft.com/azure/ai-foundry/foundry-local/), allowing you to test AI capabilities without requiring cloud resources during development. This supports automatic model downloading, loading, and management through the integrated Foundry Local runtime.

### 🗄️ Database hosting improvements

Several database integrations have been updated with **improved initialization patterns**:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// MongoDB - new WithInitFiles method (replaces WithInitBindMount)
var mongo = builder.AddMongoDB("mongo")
    .WithInitFiles("./mongo-init");  // Initialize with scripts

// MySQL - improved initialization with better file handling
var mysql = builder.AddMySql("mysql", password: builder.AddParameter("mysql-password"))
    .WithInitFiles("./mysql-init");  // Initialize with SQL scripts

// Oracle - enhanced setup capabilities with consistent API
var oracle = builder.AddOracle("oracle")
    .WithInitFiles("./oracle-init");  // Initialize with Oracle scripts

builder.Build().Run();
```

All database providers now support `WithInitFiles()` method, replacing the more complex `WithInitBindMount()` method and enabling better error handling.

## ☁️ Azure goodies

### 🏷️ Consistent resource name exposure

Aspire 9.4 now consistently exposes the actual names of all Azure resources through `Aspire.Hosting.Azure.NameOutputReference` property. This enables applications to access the real Azure resource names that get generated during deployment, which is essential for scenarios requiring direct Azure resource coordination. This is particularly valuable for external automation scripts and monitoring and alerting systems that reference resources by their actual names.

### 🗄️ Azure Cosmos DB

#### Hierarchical partition keys

Aspire 9.4 introduces support for **hierarchical partition keys** (subpartitioning) in Azure Cosmos DB, enabling multi-level partitioning for better data distribution and query performance.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var cosmos = builder.AddAzureCosmosDB("cosmos");
var database = cosmos.AddCosmosDatabase("ecommerce");

// Traditional single partition key
var ordersContainer = database.AddContainer("orders", "/customerId");

// New hierarchical partition keys (up to 3 levels)
var productsContainer = database.AddContainer("products", 
    ["/category", "/subcategory", "/brand"]);

// Multi-tenant scenario
var eventsContainer = database.AddContainer("events",
    ["/tenantId", "/userId", "/sessionId"]);

builder.Build().Run();
```

**Key benefits:**

- **Scale beyond 20GB per logical partition** through multi-level distribution
- **Improved query performance** with efficient routing to relevant partitions
- **Better data distribution** for multi-dimensional datasets
- **Enhanced scalability** up to 10,000+ RU/s per logical partition prefix

For detailed guidance on design patterns and best practices, see the [Azure Cosmos DB hierarchical partition keys documentation](https://learn.microsoft.com/azure/cosmos-db/hierarchical-partition-keys).

#### Serverless support

Azure Cosmos DB accounts now default to serverless mode for cost optimization with consumption-based billing.

```csharp
// Default behavior: Creates serverless account (new in 9.4)
var cosmos = builder.AddAzureCosmosDB("cosmos");

// Explicitly enable provisioned throughput mode
var provisionedCosmos = builder.AddAzureCosmosDB("cosmos")
    .WithDefaultAzureSku(); // Uses provisioned throughput instead of serverless
```

**Serverless benefits:**

- **Pay-per-use** - Only charged for consumed Request Units and storage
- **No minimum costs** - Ideal for intermittent or unpredictable workloads
- **Automatic scaling** - No capacity planning required
- **Perfect for development/testing** environments

**Use serverless for:** Variable workloads, development/testing, applications with low average-to-peak traffic ratios.  
**Use provisioned throughput for:** Sustained traffic requiring predictable performance guarantees.

For detailed comparison and limits, see [Azure Cosmos DB serverless documentation](https://learn.microsoft.com/azure/cosmos-db/serverless).

### 🆔 Consistent user-assigned managed identity support

Aspire 9.4 introduces comprehensive support for Azure user-assigned managed identities, providing enhanced security and consistent identity management across your Azure infrastructure:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Create a user-assigned managed identity
var appIdentity = builder.AddAzureUserAssignedIdentity("app-identity");

// Create the container app environment
var containerEnv = builder.AddAzureContainerAppEnvironment("container-env");

// Apply the identity to compute resources
var functionApp = builder.AddAzureFunctionsProject<Projects.Functions>("functions")
    .WithAzureUserAssignedIdentity(appIdentity);

// The identity can be shared across multiple resources
var webApp = builder.AddProject<Projects.WebApp>("webapp")
    .WithAzureUserAssignedIdentity(appIdentity);

// Use the same identity for accessing Azure services
var keyVault = builder.AddAzureKeyVault("secrets");
var storage = builder.AddAzureStorage("storage");

// Services using the shared identity can access resources securely
var processor = builder.AddProject<Projects.DataProcessor>("processor")
    .WithAzureUserAssignedIdentity(appIdentity)
    .WithReference(keyVault)
    .WithReference(storage);

builder.Build().Run();
```

This approach provides:

- **Flexible identity control** - Override Aspire's secure defaults when you need specific identity configurations
- **Consistent identity management** across all compute resources

#### 🔐 Disabled local authentication to enforce managed identity

Aspire 9.4 automatically disables local authentication for [Azure EventHubs](../messaging/azure-event-hubs-integration.md) and [Azure Web PubSub](../messaging/azure-web-pubsub-integration.md) resources, enforcing managed identity authentication by default.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Azure EventHubs with automatic local auth disabled
var eventHubs = builder.AddAzureEventHubs("eventhubs");
var hub = eventHubs.AddEventHub("orders");

// Azure Web PubSub with automatic local auth disabled  
var webPubSub = builder.AddAzureWebPubSub("webpubsub");

// Services connect using managed identity automatically
var processor = builder.AddProject<Projects.EventProcessor>("processor")
    .WithReference(hub)
    .WithReference(webPubSub);

builder.Build().Run();
```

This change automatically applies to all Azure EventHubs and Web PubSub resources, ensuring secure-by-default behavior.

### 🔐 Azure Key Vault enhancements

Aspire 9.4 introduces significant improvements to the [Azure Key Vault integration](../security/azure-security-key-vault-integration.md) with new secret management APIs that provide strongly typed access to secrets:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var secrets = builder.AddAzureKeyVault("secrets");

// Add a secret from a parameter
var connectionStringParam = builder.AddParameter("connectionString", secret: true);
var connectionString = secrets.AddSecret("connection-string", connectionStringParam);

// Add a secret with custom secret name in Key Vault
var apiKeyParam = builder.AddParameter("api-key", secret: true);
var apiKey = secrets.AddSecret("api-key", "ApiKey", apiKeyParam);

// Get a secret reference for consumption (for existing secrets)
var existingSecret = secrets.GetSecret("ExistingSecret");

// Use in your services
var webApi = builder.AddProject<Projects.WebAPI>("webapi")
    .WithEnvironment("CONNECTION_STRING", connectionString)
    .WithEnvironment("API_KEY", apiKey)
    .WithEnvironment("EXISTING_SECRET", existingSecret);
```

**Key features**:

- `Aspire.Hosting.Azure.KeyVault.AzureKeyVaultResourceExtensions.AddSecret` method for adding new secrets to Key Vault from parameters or expressions
- `Aspire.Hosting.Azure.KeyVault.AzureKeyVaultResourceExtensions.GetSecret` method for referencing existing secrets in Key Vault
- **Strongly-typed secret references** that can be used with `WithEnvironment()` for environment variables
- **Custom secret naming** support with optional `secretName` parameter

#### 📥Resource Deep Linking for Azure Storage Queues

Aspire 9.4 expands resource deep linking to include Azure Queue Storage queues, building on the model already used for Azure Blob Storage, Cosmos DB, etc.

You can now model individual storage queues directly in your app host, then inject scoped QueueClient instances into your services—making it easy to interact with queues without manually configuring connection strings or access.

**AppHost:**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var storage = builder.AddAzureStorage("storage");

// Model individual queues as first-class resources
var orderQueue = storage.AddQueue("orders", "order-processing");
var notificationQueue = storage.AddQueue("notifications", "user-notifications");

// Services get scoped access to specific queues
builder.AddProject<Projects.OrderProcessor>("order-processor")
       .WithReference(orderQueue);  // Only has access to order-processing queue

builder.AddProject<Projects.NotificationService>("notifications")
       .WithReference(notificationQueue);  // Only has access to user-notifications queue

builder.Build().Run();
```

**In the OrderProcessor project:**

```csharp
using Azure.Storage.Queues;

var builder = WebApplication.CreateBuilder(args);

// Register the queue client
builder.AddAzureQueue("orders");

var app = builder.Build();

// Minimal POST endpoint for image upload
app.MapPost("/process-order", async (QueueClient ordersQueue) =>
{
    // read a message for the queue
    var message = await ordersQueue.ReceiveMessageAsync();
    ProcessMessage(message);

    return Results.Ok();
});

app.Run();
```

This approach provides clean separation of concerns, secure container scoping, and minimal ceremony—ideal for microservices that interact with specific storage queues.

### 📡 OpenTelemetry tracing support for Azure App Configuration

Aspire 9.4 introduces **OpenTelemetry tracing support** for [Azure App Configuration](../azure/azure-app-configuration-integration.md), completing the observability story for this integration. The Azure App Configuration integration now automatically instruments configuration retrieval operations and refresh operations with distributed tracing.

```csharp
var builder = WebApplication.CreateBuilder(args);

// Azure App Configuration now includes automatic tracing
builder.AddAzureAppConfiguration("config", settings =>
{
    settings.Endpoint = new Uri("https://myconfig.azconfig.io");
    // Tracing is enabled by default - traces configuration operations
});

// Optionally disable tracing for specific scenarios
builder.AddAzureAppConfiguration("sensitive-config", settings =>
{
    settings.DisableTracing = true; // Disable OpenTelemetry tracing
});

var app = builder.Build();
```

**What gets traced:**

- **Configuration retrieval operations** - When configuration values are loaded from Azure App Configuration
- **Configuration refresh operations** - When the configuration is refreshed in the background
- **Activity source**: `Microsoft.Extensions.Configuration.AzureAppConfiguration` - for filtering and correlation

Tracing can be disabled using `DisableTracing = true` for sensitive scenarios.

This enhancement brings Azure App Configuration in line with other Azure components that support comprehensive observability, providing developers with better insights into configuration-related performance and behavior.

### ⚙️ Enhanced Azure provisioning interaction

Aspire 9.4 significantly improves the Azure provisioning experience by leveraging the interaction services to streamline Azure subscription and resource group configuration during deployment workflows.

The enhanced Azure provisioning system:

- **Automatically prompts for missing Azure configuration** during deploy operations
- **Saves configuration to user secrets** for future deployments
- **Provides smart defaults** like auto-generated resource group names
- **Includes validation callbacks** for Azure-specific inputs like subscription IDs and locations
- **Supports rich HTML prompts** with links to create free Azure accounts

This enhancement makes Azure deployment significantly more user-friendly, especially for developers new to Azure or setting up projects for the first time. The interaction system ensures that all necessary Azure configuration is collected interactively and stored securely for subsequent deployments.

### 🐳 Azure App Service container support

Aspire 9.4 introduces support for deploying containerized applications with Dockerfiles to Azure App Service environments. This enables a seamless transition from local container development to Azure App Service deployment.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Create an Azure App Service environment
builder.AddAzureAppServiceEnvironment("app-service-env");

// Add a containerized project with Dockerfile
var containerApp = builder.AddContainer("my-app", "my-app:latest")
    .WithDockerfile("./Dockerfile");

// Or add a project that builds to a container
var webApp = builder.AddProject<Projects.WebApp>("webapp");

builder.Build().Run();
```

This feature bridges the gap between container development and Azure App Service deployment, allowing developers to use the same container-based workflows they use locally in production Azure environments.

### 🏗️ Improvements to the Azure Container Apps integration

Managing complex Azure Container Apps environments often requires integrating with existing Azure resources like Log Analytics workspaces. Aspire 9.4 enhances the [Container Apps integration](../azure/configure-aca-environments.md) with support for existing Azure resources and improved configuration.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Reference existing Log Analytics workspace
var workspaceName = builder.AddParameter("workspace-name");
var workspaceRg = builder.AddParameter("workspace-rg");

var logWorkspace = builder.AddAzureLogAnalyticsWorkspace("workspace")
                          .AsExisting(workspaceName, workspaceRg);

var containerEnv = builder.AddAzureContainerAppEnvironment("production")
                          .WithAzureLogAnalyticsWorkspace(logWorkspace);

builder.AddProject<Projects.Api>("api")
       .WithComputeEnvironment(containerEnv);

builder.Build().Run();
```

This also helps manage cost control by reusing existing resources like Log Analytics.

#### 🛡️ Automatic DataProtection configuration for .NET on ACA

Aspire 9.4 automatically configures DataProtection for .NET projects deployed to Azure Container Apps, ensuring applications work correctly when scaling beyond a single instance.

When ASP.NET Core applications scale to multiple instances, they need shared DataProtection keys to decrypt cookies, authentication tokens, and other protected data across all instances. Without proper configuration, users experience authentication issues and data corruption when load balancers route requests to different container instances.

Aspire now automatically enables `autoConfigureDataProtection` for all .NET projects deployed to Azure Container Apps:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddAzureContainerAppEnvironment("production");

// DataProtection is automatically configured for scaling
var api = builder.AddProject<Projects.WebApi>("api");

var frontend = builder.AddProject<Projects.BlazorApp>("frontend");

builder.Build().Run();
```

This enhancement aligns Aspire-generated deployments with Azure Developer CLI (`azd`) behavior and resolves common production scaling issues without requiring manual DataProtection configuration.

### ⚡ Azure Functions Container Apps integration

Aspire 9.4 improves Azure Functions deployment to Azure Container Apps by automatically setting the correct function app kind. This ensures Azure Functions are properly recognized and managed within the Azure Container Apps environment.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddAzureContainerAppEnvironment("functions-env");

// Azure Functions project deployed to Container Apps
var functionsApp = builder.AddAzureFunctionsProject<Projects.MyFunctions>("functions");

builder.Build().Run();
```

This change resolves issues where Azure Functions deployed to Container Apps weren't properly recognized by Azure tooling and monitoring systems, providing a more seamless serverless experience.

## 📋 Project template improvements

Aspire 9.4 introduces enhancements to project templates, including .NET 10 support and improved file naming conventions.

### 🚀 .NET 10 framework support

All Aspire project templates now support .NET 10 with framework selection. .NET 9.0 remains the default target framework.

```bash
# Create a new Aspire project targeting .NET 10
dotnet new aspire --framework net10.0

# Create an app host project targeting .NET 10  
dotnet new aspire-apphost --framework net10.0
```

### 📝 Improved file naming convention

The `aspire-apphost` template now uses a more descriptive file naming convention making it easier to distinguish app host files in multi-project solutions. Instead of `Program.cs`, the main program file is now named `AppHost.cs`.

The content and functionality remain unchanged — only the filename has been updated to be more descriptive.

## 💔 Breaking changes

### 🔑 Azure Key Vault secret reference changes

Azure Key Vault secret handling has been updated with improved APIs that provide better type safety and consistency:

```csharp
// ❌ Before (obsolete):
var keyVault = builder.AddAzureKeyVault("secrets");
var secretOutput = keyVault.GetSecretOutput("ApiKey");           // Obsolete
var secretRef = new BicepSecretOutputReference(secretOutput);    // Obsolete - class removed

// ✅ After (recommended):
var keyVault = builder.AddAzureKeyVault("secrets");
var secretRef = keyVault.GetSecret("ApiKey");                    // New strongly-typed API

// For environment variables:
// ❌ Before (obsolete):
builder.AddProject<Projects.Api>("api")
       .WithEnvironment("API_KEY", secretRef);  // Using BicepSecretOutputReference

// ✅ After (recommended):
builder.AddProject<Projects.Api>("api")
       .WithEnvironment("API_KEY", secretRef);  // Using IAzureKeyVaultSecretReference
```

**Migration impact**: Replace `GetSecretOutput()` and `BicepSecretOutputReference` usage with the new `GetSecret()` method that returns `IAzureKeyVaultSecretReference`.

### 📦 Azure Storage blob container creation changes

Azure Storage blob container creation has been moved from specialized blob storage resources to the main storage resource for better consistency:

```csharp
// ❌ Before (obsolete):
var storage = builder.AddAzureStorage("storage");
var blobs = storage.AddBlobs("blobs");
var container = blobs.AddBlobContainer("images");     // Obsolete

// ✅ After (recommended):
var storage = builder.AddAzureStorage("storage");
var container = storage.AddBlobContainer("images");   // Direct on storage resource
```

**Migration impact**: Use `AddBlobContainer()` directly on `AzureStorageResource` instead of on specialized blob storage resources.

### 🔐 Keycloak realm import simplification

The `WithRealmImport` method signature has been **simplified by removing the confusing `isReadOnly` parameter**:

```csharp
// ❌ Before (deprecated):
var keycloak = builder.AddKeycloak("keycloak")
    .WithRealmImport("./realm.json", isReadOnly: false);  // Confusing parameter

// ✅ After (recommended):
var keycloak = builder.AddKeycloak("keycloak")
    .WithRealmImport("./realm.json");  // Clean, simple API

// If you need explicit read-only control:
var keycloak = builder.AddKeycloak("keycloak")
    .WithRealmImport("./realm.json", isReadOnly: true);  // Still available as overload
```

**Migration impact**: Remove the `isReadOnly` parameter from single-parameter `WithRealmImport()` calls - the method now defaults to appropriate behavior. Use the two-parameter overload if explicit control is needed.

### 🔧 Milvus configuration method updates

Milvus configuration has been updated with more descriptive method names:

```csharp
// ❌ Before (deprecated):
var milvus = builder.AddMilvus("milvus")
    .WithConfigurationBindMount("./milvus.yaml");  // Old method name

// ✅ After (recommended):
var milvus = builder.AddMilvus("milvus")
    .WithConfigurationFile("./milvus.yaml");  // Method renamed for clarity
```

**Migration impact**: Update method calls to use `WithConfigurationFile` instead of `WithConfigurationBindMount` for Milvus configuration.

### 🔄 Azure Storage client registration updates

Client registration methods for Azure Storage have been standardized with new naming conventions:

```csharp
// ❌ Before (obsolete):
builder.AddAzureTableClient("tables");         // Obsolete
builder.AddKeyedAzureTableClient("tables");    // Obsolete
builder.AddAzureBlobClient("blobs");            // Obsolete
builder.AddKeyedAzureBlobClient("blobs");       // Obsolete
builder.AddAzureQueueClient("queues");          // Obsolete
builder.AddKeyedAzureQueueClient("queues");     // Obsolete

// ✅ After (recommended):
builder.AddAzureTableServiceClient("tables");         // Standardized naming
builder.AddKeyedAzureTableServiceClient("tables");    // Standardized naming
builder.AddAzureBlobServiceClient("blobs");           // Standardized naming
builder.AddKeyedAzureBlobServiceClient("blobs");      // Standardized naming
builder.AddAzureQueueServiceClient("queues");         // Standardized naming
builder.AddKeyedAzureQueueServiceClient("queues");    // Standardized naming
```

**Migration impact**: Update all client registration calls to use the new `*ServiceClient` naming convention.

### 🗄️ Database initialization method changes

Several database resources have **deprecated `WithInitBindMount` in favor of the more consistent `WithInitFiles`**:

```csharp
// ❌ Before (deprecated):
var mongo = builder.AddMongoDB("mongo")
    .WithInitBindMount("./init", isReadOnly: true);  // Complex parameters

var mysql = builder.AddMySql("mysql")  
    .WithInitBindMount("./mysql-scripts", isReadOnly: false);

var oracle = builder.AddOracle("oracle")
    .WithInitBindMount("./oracle-init", isReadOnly: true);

var postgres = builder.AddPostgres("postgres")
    .WithInitBindMount("./postgres-init", isReadOnly: true);

// ✅ After (recommended):
var mongo = builder.AddMongoDB("mongo")
    .WithInitFiles("./init");  // Simplified, consistent API

var mysql = builder.AddMySql("mysql")
    .WithInitFiles("./mysql-scripts");  // Same pattern across all providers

var oracle = builder.AddOracle("oracle")
    .WithInitFiles("./oracle-init");  // Unified approach

var postgres = builder.AddPostgres("postgres")
    .WithInitFiles("./postgres-init");  // Consistent across all databases
```

**Affected database providers**: MongoDB, MySQL, Oracle, and PostgreSQL

**Migration impact**: Replace `WithInitBindMount()` calls with `WithInitFiles()` - the new method handles read-only mounting automatically and provides better error handling.

### Resource lifecycle event updates

The generic `AfterEndpointsAllocatedEvent` has been deprecated in favor of more specific, type-safe events:

```csharp
// ❌ Before (deprecated):
builder.Services.AddSingleton<IDistributedApplicationLifecycleHook, MyLifecycleHook>();

public class MyLifecycleHook : IDistributedApplicationLifecycleHook
{
    public Task AfterEndpointsAllocatedAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken)
    {
        // Generic event handling - deprecated
        return Task.CompletedTask;
    }
}

// ✅ After (recommended):
var api = builder.AddProject<Projects.Api>("api")
    .OnBeforeResourceStarted(async (resource, evt, cancellationToken) =>
    {
        // Resource-specific event handling
    })
    .OnResourceEndpointsAllocated(async (resource, evt, cancellationToken) =>
    {
        // Endpoint-specific event handling
    });
```

**Migration impact**: Replace usage of `AfterEndpointsAllocatedEvent` with resource-specific lifecycle events like `OnBeforeResourceStarted` or `OnResourceEndpointsAllocated` for better type safety and clarity.

### 🧊 Azure Container Apps hybrid mode removal

Azure Container Apps hybrid mode support has been **removed** to simplify the deployment model and improve consistency. Previously, `PublishAsAzureContainerApp` would automatically create Azure infrastructure, but this behavior has been streamlined.

```csharp
// ❌ Before (hybrid mode - no longer supported):
// In hybrid mode, this would automatically add Azure Container Apps infrastructure
var api = builder.AddProject<Projects.Api>("api")
    .PublishAsAzureContainerApp((infrastructure, containerApp) =>
    {
        app.Template.Scale.MinReplicas = 0;
    });

// The hybrid approach mixed azd-generated environments with Aspire-managed infrastructure
// This caused confusion and maintenance complexity

// ✅ After (required approach):
// Explicitly add Azure Container App Environment first
var containerAppEnvironment = builder.AddAzureContainerAppEnvironment("cae");

// When coming from hybrid mode, the names of the resources will change
// WithAzdResourceNaming will keep the older naming convention that azd uses
// while making this transition to aspire owned infrastructure.
containerAppEnvironment.WithAzdResourceNaming();

// Then use PublishAsAzureContainerApp for customization only (same API)
var api = builder.AddProject<Projects.Api>("api")
    .PublishAsAzureContainerApp((infrastructure, containerApp) =>
    {
        app.Template.Scale.MinReplicas = 0;
    });
```

**Key changes:**

- `PublishAsAzureContainerApp()` **no longer automatically creates infrastructure** - it only adds customization annotations
- **BicepSecretOutput APIs have been removed** from the Azure Container Apps logic for simplified secret handling

**Migration impact:**

1. **Add explicit Azure Container App Environment**: Use `builder.AddAzureContainerAppEnvironment("name")` before calling `PublishAsAzureContainerApp()`
2. **Update secret references**: Replace any `BicepSecretOutputReference` usage with proper Azure Key Vault resources using `IAzureKeyVaultSecretReference`
3. **Review infrastructure setup**: Ensure your Bicep templates or infrastructure setup properly creates the Container App Environment that your apps will deploy to

This change provides **clearer separation** between infrastructure provisioning (handled by explicit resource creation) and application deployment configuration (handled by `PublishAsAzureContainerApp`), making the deployment process more predictable and easier to understand.

### ⚠️ Known parameter deprecations

Several auto-injected known parameters have been deprecated and removed from Azure resources in favor of explicit resource modeling:

**Deprecated parameters:**

- `AzureBicepResource.KnownParameters.KeyVaultName`
- `AzureBicepResource.KnownParameters.LogAnalyticsWorkspaceId`

#### KeyVaultName parameter deprecation

The `AzureBicepResource.KnownParameters.KeyVaultName` parameter is now obsolete. Previously, this parameter was automatically injected into Azure resources to reference Key Vault instances for storing secrets.

```csharp
// ❌ Before (deprecated):
var customResource = builder.AddAzureInfrastructure("custom", infra =>
{
    // Custom Bicep template that expected keyVaultName parameter to be auto-filled
    var kvNameParam = new ProvisioningParameter(AzureBicepResource.KnownParameters.KeyVaultName, typeof(string));
    infra.Add(kvNameParam);
    
    var keyVault = KeyVaultService.FromExisting("keyVault");
    keyVault.Name = kvNameParam;  // This was auto-populated by Aspire
    infra.Add(keyVault);
    
    // Store secrets in the auto-injected Key Vault
    var secret = new KeyVaultSecret("mySecret", keyVault)
    {
        Properties = { Value = "sensitive-value" }
    };
    infra.Add(secret);
});

// ✅ After (recommended):
var keyVault = builder.AddAzureKeyVault("secrets");
var customResource = builder.AddAzureInfrastructure("custom", infra =>
{
    // Use explicit Key Vault resource reference
    var existingKeyVault = (KeyVaultService)keyVault.Resource.AddAsExistingResource(infra);
    
    var secret = new KeyVaultSecret("mySecret", existingKeyVault)
    {
        Properties = { Value = "sensitive-value" }
    };
    infra.Add(secret);
});
```

#### LogAnalyticsWorkspaceId parameter deprecation

The `AzureBicepResource.KnownParameters.LogAnalyticsWorkspaceId` parameter is now obsolete. Application Insights resources will now automatically create their own Log Analytics workspace or use explicitly provided ones.

```csharp
// ❌ Before (deprecated):
var appInsights = builder.AddAzureApplicationInsights("ai")
    .WithParameter(AzureBicepResource.KnownParameters.LogAnalyticsWorkspaceId, workspaceId);

// ✅ After (recommended):
// Option 1: Auto-generated workspace (default behavior)
var appInsights = builder.AddAzureApplicationInsights("ai");

// Option 2: Explicit workspace resource
var workspace = builder.AddAzureLogAnalyticsWorkspace("workspace");
var appInsights = builder.AddAzureApplicationInsights("ai")
    .WithLogAnalyticsWorkspace(workspace);

// Option 3: Reference existing workspace from another resource
var env = builder.AddAzureContainerAppEnvironment("env");
var appInsights = builder.AddAzureApplicationInsights("ai")
    .WithLogAnalyticsWorkspace(env.GetOutput("AZURE_LOG_ANALYTICS_WORKSPACE_ID"));
```

#### Container App Environment parameter changes

Previously, container app environment properties (managed identity, workspace ID) were automatically injected into other Azure resources. These are no longer auto-injected as Aspire now supports multiple compute environments.

```csharp
// ❌ Before (auto-injection):
// These properties were automatically available in other resources:
// - MANAGED_IDENTITY_NAME
// - MANAGED_IDENTITY_PRINCIPAL_ID
// - logAnalyticsWorkspaceId

// ✅ After (explicit references):
var env = builder.AddAzureContainerAppEnvironment("env");
var resource = builder.AddAzureInfrastructure("custom", infra =>
{
    // Use explicit references when needed
    var managedEnv = (ContainerAppManagedEnvironment)env.Resource.AddAsExistingResource(infra);
    // Access properties through the bicep resource directly
});
```

**Migration impact**: Replace auto-injected parameters with explicit resource modeling for better resource graph representation and support for multiple Azure compute environments. See [Azure resource customization docs](https://learn.microsoft.com/microsoft/aspire/azure/customize-azure-resources) for more details.

### 🔧 ParameterResource.Value synchronous behavior change

The `ParameterResource.Value` property now blocks synchronously when waiting for parameter value resolution, which can potentially cause deadlocks in async contexts. The new `GetValueAsync()` method should be used instead for proper async handling.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Parameters that need resolution
var apiKey = builder.AddParameter("api-key", secret: true);
var connectionString = builder.AddParameter("connection-string", secret: true);

// ❌ Before (can cause deadlocks in async contexts):
builder.AddProject<Projects.Api>("api")
    .WithEnvironment("API_KEY", apiKey.Resource.Value)  // Blocks synchronously - can deadlock
    .WithEnvironment("CONNECTION_STRING", connectionString.Resource.Value);

// ✅ After (recommended for async contexts):
// Use the parameter resources directly with WithEnvironment - they handle async resolution internally
builder.AddProject<Projects.Api>("api")
    .WithEnvironment("API_KEY", apiKey)  // Let Aspire handle async resolution
    .WithEnvironment("CONNECTION_STRING", connectionString);

// Or if you need the actual value in custom code with WithEnvironment callback:
builder.AddProject<Projects.Api>("api")
    .WithEnvironment("API_KEY", async (context, cancellationToken) =>
    {
        return await apiKey.Resource.GetValueAsync(cancellationToken);  // Proper async handling
    })
    .WithEnvironment("CONNECTION_STRING", async (context, cancellationToken) =>
    {
        return await connectionString.Resource.GetValueAsync(cancellationToken);
    });

// For non-async contexts where blocking is acceptable:
var syncValue = apiKey.Resource.Value;  // Still works but may block
```

**Migration impact**: When working with `ParameterResource` values in async contexts, use the new `GetValueAsync()` method instead of the `Value` property to avoid potential deadlocks. For `WithEnvironment()` calls, prefer passing the parameter resource directly rather than accessing `.Value` synchronously.

With every release, we strive to make Aspire better. However, some changes may break existing functionality. For complete details on breaking changes in this release, see:

- [Breaking changes in Aspire 9.4](../compatibility/9.4/index.md)

## 🎯 Upgrade today

Follow the directions outlined in the [Upgrade to Aspire 9.4](#️-upgrade-to-net-aspire-94) section to make the switch to 9.4 and take advantage of all these new features today! As always, we're listening for your feedback on [GitHub](https://github.com/microsoft/aspire/issues)—and looking out for what you want to see in 9.5 ☺️.

For a complete list of issues addressed in this release, see [Aspire GitHub repository—9.4 milestone](https://github.com/microsoft/aspire/issues?q=is%3Aissue%20state%3Aclosed%20milestone%3A9.4%20).
