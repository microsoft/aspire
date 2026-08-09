---
title: What's new in Aspire 9.2
description: Learn what's new in the official general availability release of Aspire 9.2.
ms.date: 04/10/2025
---

## What's new in Aspire 9.2

📢 Aspire 9.2 is the next minor version release of Aspire; it supports:

- .NET 8.0 Long Term Support (LTS)
- .NET 9.0 Standard Term Support (STS)

If you have feedback, questions, or want to contribute to Aspire, collaborate with us on [:::image type="icon" source="../media/github-mark.svg" border="false"::: GitHub](https://github.com/microsoft/aspire) or join us on [:::image type="icon" source="../media/discord-icon.svg" border="false"::: Discord](https://discord.com/invite/h87kDAHQgJ) to chat with team members.

It's important to note that Aspire releases out-of-band from .NET releases. While major versions of Aspire align with .NET major versions, minor versions are released more frequently. For more information on .NET and Aspire version support, see:

- [.NET support policy](https://dotnet.microsoft.com/platform/support/policy): Definitions for LTS and STS.
- [Aspire support policy](https://dotnet.microsoft.com/platform/support/policy/aspire): Important unique product life cycle details.

## ⬆️ Upgrade to Aspire 9.2

> [!IMPORTANT]
> If you are using `azd` to deploy Azure PostgreSQL or Azure SQL Server, you now have to configure Azure Managed Identities. For more information, see [🛡️ Improved Managed Identity defaults](#️-improved-managed-identity-defaults).

Moving between minor releases of Aspire is simple:

1. In your app host project file (that is, _MyApp.AppHost.csproj_), update the [📦 Aspire.AppHost.Sdk](https://www.nuget.org/packages/Aspire.AppHost.Sdk) NuGet package to version `9.2.0`:

    ```diff
    <Project Sdk="Microsoft.NET.Sdk">

        <Sdk Name="Aspire.AppHost.Sdk" Version="9.2.0" />
        
        <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net9.0</TargetFramework>
    -       <IsAspireHost>true</IsAspireHost>
            <!-- Omitted for brevity -->
        </PropertyGroup>
        
        <ItemGroup>
            <PackageReference Include="Aspire.Hosting.AppHost" Version="9.2.0" />
        </ItemGroup>
    
        <!-- Omitted for brevity -->
    </Project>
    ```

    > [!IMPORTANT]
    > The `IsAspireHost` property is no longer required in the project file. For more information, see [🚧 Project file changes](#-project-file-changes).

    For more information, see [Aspire SDK](xref:microsoft/aspire/sdk).

1. Check for any NuGet package updates, either using the NuGet Package Manager in Visual Studio or the **Update NuGet Package** command in VS Code.
1. Update to the latest [Aspire templates](../fundamentals/aspire-sdk-templates.md) by running the following .NET command line:

    ```dotnetcli
    dotnet new update
    ```

    > [!IMPORTANT]
    > The `dotnet new update` command updates all of your templates to the latest version.

If your app host project file doesn't have the `Aspire.AppHost.Sdk` reference, you might still be using Aspire 8. To upgrade to 9.0, follow [the upgrade guide](../get-started/upgrade-to-aspire-9.md).

## 🖥️ App host enhancements

The [app host](../fundamentals/app-host-overview.md) is the core of Aspire, providing the local hosting environment for your distributed applications. In Aspire 9.2, we've made several improvements to the app host:

### 🚧 Project file changes

The Aspire app host project file no longer requires the `IsAspireHost` property. This property was moved to the `Aspire.AppHost.Sdk` SDK, therefore, you can remove it from your project file. For more information, see [microsoft/aspire issue #8144](https://github.com/microsoft/aspire/pull/8144).

### 🔗 Define custom resource URLs

Resources can now define custom URLs. This makes it easier to build custom experiences for your resources. For example, you can define a custom URL for a database resource that points to the database management console. This makes it easier to access the management console directly from the dashboard, you can even give it a friendly name.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var catalogDb = builder.AddPostgres("postgres")
                       .WithDataVolume()
                       .WithPgAdmin(resource =>
                       {
                           resource.WithUrlForEndpoint("http", u => u.DisplayText = "PG Admin");
                       })
                       .AddDatabase("catalogdb");
```

The preceding code sets the display text for the `PG Admin` URL to `PG Admin`. This makes it easier to access the management console directly from the dashboard.

For more information, see [Define custom resource URLs](../fundamentals/custom-resource-urls.md).

## 🔧 Dashboard user experience improvements

Aspire 9.2 adds new features to the [dashboard](../fundamentals/dashboard/overview.md), making it a more powerful developer tool than ever. The following features were added to the dashboard in Aspire 9.2:

### 🧩 Resource graph

The resource graph is a new way to visualize the resources in your apps. It displays a graph of resources, linked by relationships. Click the 'Graph' tab on the Resources page to view the resource graph. See it in action on [James's BlueSky](https://bsky.app/profile/james.newtonking.com/post/3lj7odu4re22p).

For more information, see [Aspire dashboard: Resources page](../fundamentals/dashboard/explore.md#resources-page).

### 🎨 Resource icons

We've added resource icons to the resources page. The icon color matches the resource's telemetry in structured logs and traces.

:::image type="content" source="media/dashboard-resource-icons.png" lightbox="media/dashboard-resource-icons.png" alt-text="Screenshot of dashboard resource's page showing the new resource icons.":::

### ⏯️ Pause and resume telemetry

New buttons were added to the **Console logs**, **Structured logs**, **Traces** and **Metrics** pages to pause collecting telemetry. Click the pause button again to resume collecting telemetry.

This feature allows you to pause telemetry in the dashboard while continuing to interact with your app.

:::image type="content" source="media/dashboard-pause-telemetry.png" lightbox="media/dashboard-pause-telemetry.png" alt-text="Screenshot of the dashboard showing the pause button.":::

### ❤️‍🩹 Metrics health warning

The dashboard now warns you when a metric exceeds the configured cardinality limit. Once exceeded, the metric no longer provides accurate information.

:::image type="content" source="media/dashboard-cardinality-limit.png" lightbox="media/dashboard-cardinality-limit.png" alt-text="Screenshot of a metric with the cardinality limit warning.":::

### 🕰️ UTC Console logs option

Console logs now supports UTC timestamps. The setting is accessible via the console logs options button.

:::image type="content" source="media/dashboard-console-logs-utc.png" lightbox="media/dashboard-console-logs-utc.png" alt-text="Screenshot of console logs page showing the UTC timestamps option.":::

### 🔎 Trace details search text box

We've added a search text box to trace details. Now you can quickly filter large traces to find the exact span you need. See it in action on [BluSky](https://bsky.app/profile/james.newtonking.com/post/3llunn7fc4s2p).

### 🌐 HTTP-based resource command functionality

[Custom resource commands](../fundamentals/custom-resource-commands.md) now support HTTP-based functionality with the addition of the `WithHttpCommand` API, enabling you to define endpoints for tasks like database migrations or resets. These commands can be run directly from the Aspire dashboard.

Adds WithHttpCommand(), which lets you define a resource command that sends an HTTP request to your app during development. Useful for triggering endpoints like seed or reset from the dashboard.

```csharp
if (builder.Environment.IsDevelopment())
{
    var resetDbKey = Guid.NewGuid().ToString();

    catalogDbApp.WithEnvironment("DatabaseResetKey", resetDbKey)
                .WithHttpCommand("/reset-db", "Reset Database",
                    commandOptions: new()
                    {
                        Description = "Reset the catalog database to its initial state. This will delete and recreate the database.",
                        ConfirmationMessage = "Are you sure you want to reset the catalog database?",
                        IconName = "DatabaseLightning",
                        PrepareRequest = requestContext =>
                        {
                            requestContext.Request.Headers.Add("Authorization", $"Key {resetDbKey}");
                            return Task.CompletedTask;
                        }
                    });
}
```

For more information, see [Custom HTTP commands in Aspire](../fundamentals/http-commands.md).

### 🗂️ Connection string resource type

We've introduced a new `ConnectionStringResource` type that makes it easier to build dynamic connection strings without defining a separate resource type. This makes it easier to work with and build dynamic parameterized connection strings.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var apiKey = builder.AddParameter("apiKey");
var cs = builder.AddConnectionString("openai", 
    ReferenceExpression.Create($"Endpoint=https://api.openai.com/v1;AccessKey={apiKey};"));

var api = builder.AddProject<Projects.Api>("api")
                .WithReference(cs);
```

### 📥 Container resources can now specify an image pull policy

Container resources can now specify an `ImagePullPolicy` to control when the image is pulled. This is useful for resources that are updated frequently or that have a large image size. The following policies are supported:

- `Default`: Default behavior (which is the same as `Missing` in 9.2).
- `Always`: Always pull the image.
- `Missing`: Ensures the image is always pulled when the container starts.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache")
                   .WithImageTag("latest")
                   .WithImagePullPolicy(ImagePullPolicy.Always)
                   .WithRedisInsight();
```

The `ImagePullPolicy` is set to `Always`, which means the image will always be pulled when the resource is created. This is useful for resources that are updated frequently.

### 📂 New container files API

In Aspire 9.2, we've added a new `WithContainerFiles` API, a way to create files and folders inside a container at runtime by defining them in code. Under the hood, it uses `docker cp` / `podman cp` to copy the files in. Supports setting contents, permissions, and ownership—no bind mounts or temp files needed.

## 🤝 Integrations updates

Integrations are a key part of Aspire, allowing you to easily add and configure services in your app. In Aspire 9.2, we've made several updates to integrations:

### 🔐 Redis/Valkey/Garnet: Password support enabled by default

The Redis, Valkey, and Garnet containers enable password authentication by default. This is part of our goal to be secure by default—protecting development environments with sensible defaults while still making them easy to configure. Passwords can be set explicitly or generated automatically if not provided.

### 💾 Automatic database creation support

There's [plenty of feedback and confusion](https://github.com/microsoft/aspire/issues/7101) around the `AddDatabase` API. The name implies that it adds a database, but it didn't actually create the database. In Aspire 9.2, the `AddDatabase` API now creates a database for the following hosting integrations:

| Hosting integration | API reference |
|--|--|
| [📦 Aspire.Hosting.SqlServer](https://www.nuget.org/packages/Aspire.Hosting.SqlServer) | <xref:Aspire.Hosting.SqlServerBuilderExtensions.AddDatabase*> |
| [📦 Aspire.Hosting.PostgreSql](https://www.nuget.org/packages/Aspire.Hosting.PostgreSql) | <xref:Aspire.Hosting.PostgresBuilderExtensions.AddDatabase*> |

The Azure SQL and Azure PostgreSQL hosting integrations also expose `AddDatabase` APIs which work with their respective `RunAsContainer` methods. For more information, see [Understand Azure integration APIs](../azure/integrations-overview.md#understand-azure-integration-apis).

By default, Aspire will create an empty database if it doesn't exist. You can also optionally provide a custom script to run during creation for advanced setup or seeding.

Example using Postgres:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("pg1");

postgres.AddDatabase("todoapp")
    .WithCreationScript($$"""
        CREATE DATABASE {{databaseName}}
            ENCODING = 'UTF8';
        """);
```

For more information and examples of using the `AddDatabase` API, see:

- [Add PostgreSQL resource with database scripts](../database/postgresql-integration.md#add-postgresql-resource-with-database-scripts)
- [Add SQL Server resource with database scripts](../database/sql-server-integration.md#add-sql-server-resource-with-database-scripts)

The following hosting integrations don't currently support database creation:

- [📦 Aspire.Hosting.MongoDb](https://www.nuget.org/packages/Aspire.Hosting.MongoDb)
- [📦 Aspire.Hosting.MySql](https://www.nuget.org/packages/Aspire.Hosting.MySql)
- [📦 Aspire.Hosting.Oracle](https://www.nuget.org/packages/Aspire.Hosting.Oracle)

## ☁️ Azure integration updates

In Aspire 9.2, we've made significant updates to Azure integrations, including:

### ⚙️ Configure Azure Container Apps environments

Aspire 9.2 introduces `AddAzureContainerAppEnvironment`, allowing you to define an Azure Container App environment directly in your app model. This adds an `AzureContainerAppsEnvironmentResource` that lets you configure the environment and its supporting infrastructure (like container registries and volume file shares) using C# and the <xref:Azure.Provisioning> APIs—without relying on `azd` for infrastructure generation.

> [!IMPORTANT]
> This uses a different resource naming scheme than `azd`. If you're upgrading an existing deployment, this may create duplicate resources. To avoid this, you can opt into `azd`'s naming convention:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddAzureContainerAppEnvironment("my-env")
       .WithAzdResourceNaming();
```

For more information, see [Configure Azure Container Apps environments](../azure/configure-aca-environments.md).

### 🆕 New Client integrations: Azure PostgreSQL (Npgsql & EF Core)

Aspire 9.2 adds client integrations for working with **Azure Database for PostgreSQL**, supporting both local development and secure cloud deployment.

These integrations automatically use **Managed Identity (Entra ID)** in the cloud and during local development by default. They also support username/password, if configured in your AppHost. No application code changes are required to switch between authentication models.

- [📦 Aspire.Azure.Npgsql](https://www.nuget.org/packages/Aspire.Azure.Npgsql)
- [📦 Aspire.Azure.Npgsql.EntityFrameworkCore.PostgreSQL](https://www.nuget.org/packages/Aspire.Azure.Npgsql.EntityFrameworkCore.PostgreSQL)

**In AppHost:**

```csharp
var postgres = builder.AddAzurePostgresFlexibleServer("pg")
                      .AddDatabase("postgresdb");

builder.AddProject<Projects.MyService>()
       .WithReference(postgres);
```

**In MyService:**

```csharp
builder.AddAzureNpgsqlDbContext<MyDbContext>("postgresdb");
```

### 🖇️ Resource Deep Linking for Cosmos DB, Event Hubs, Service Bus, and OpenAI

CosmosDB databases and containers, EventHub hubs, ServiceBus queues/topics, and Azure OpenAI deployments now support **resource deep linking**. This allows connection information to target specific child resources—like a particular **Cosmos DB container**, **Event Hubs**, or **OpenAI deployment**—rather than just the top-level account or namespace.

Hosting integrations preserve the full resource hierarchy in connection strings, and client integrations can resolve and inject clients scoped to those specific resources.

**AppHost:**

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var cosmos = builder.AddAzureCosmosDB("cosmos")
                    .RunAsPreviewEmulator(e => e.WithDataExplorer());

var db = cosmos.AddCosmosDatabase("appdb");
db.AddContainer("todos", partitionKey: "/userId");
db.AddContainer("users", partitionKey: "/id");

builder.AddProject<Projects.TodoApi>("api")
       .WithReference(db);
```

**In the API project:**

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.AddAzureCosmosDatabase("appdb")
       .AddKeyedContainer("todos")
       .AddKeyedContainer("users");

app.MapPost("/todos", async ([FromKeyedServices("todos")] Container container, TodoItem todo) =>
{
    todo.Id = Guid.NewGuid().ToString();
    await container.CreateItemAsync(todo, new PartitionKey(todo.UserId));
    return Results.Created($"/todos/{todo.Id}", todo);
});
```

This makes it easy and convenient to use the SDKs to interact with specific resources directly—without extra wiring or manual configuration. It's especially useful in apps that deal with multiple containers or Azure services.

### 🛡️ Improved Managed Identity defaults

Starting in _Aspire 9.2_, each Azure Container App now gets its _own dedicated managed identity_ by default. This is a significant change from previous versions, where all apps shared a single, highly privileged identity.

This change strengthens Aspire's *secure by default* posture:

- Each app only gets access to the Azure resources it needs.
- It enforces the principle of least privilege.
- It provides better isolation between apps in multi-service environments.

By assigning identities individually, Aspire can now scope role assignments more precisely—improving security, auditability, and alignment with Azure best practices.

This is a _behavioral breaking change_ and may impact apps using:

- _Azure SQL Server_ - Azure SQL only supports one Azure AD admin. With multiple identities, only the _last deployed app_ will be granted admin access by default. Other apps will need explicit users and role assignments.

- _Azure PostgreSQL_ - The app that creates the database becomes the owner. Other apps (like those running migrations or performing data operations) will need explicit `GRANT` permissions to access the database correctly.

See the [breaking changes](../compatibility/9.2/index.md) page for more details.

This new identity model is an important step toward more secure and maintainable applications in Aspire. While it introduces some setup considerations, especially for database integrations, it lays the groundwork for better default security across the board.

### 🔑 Least-privilege role assignment functionality

Aspire now supports APIs for modeling **least-privilege role assignments** when referencing Azure resources. This enables more secure defaults by allowing you to define exactly which roles each app needs for specific Azure resources.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var storage = builder.AddAzureStorage("storage")
                     .RunAsEmulator(c => c.WithLifetime(ContainerLifetime.Persistent));

var blobs = storage.AddBlobs("blobs");

builder.AddProject<Projects.AzureContainerApps_ApiService>("api")
       .WithExternalHttpEndpoints()
       .WithReference(blobs)
       .WithRoleAssignments(storage, StorageBuiltInRole.StorageBlobDataContributor);
```

In this example, the API project is granted **Storage Blob Data Contributor** only for the referenced storage account. This avoids over-provisioning permissions and helps enforce the principle of least privilege.

Each container app automatically gets its own **managed identity**, and Aspire now generates the necessary role assignment infrastructure for both default and per-reference roles. When targeting existing Azure resources, role assignments are scoped correctly using separate Bicep resources.

### 1️⃣ First-class Azure Key Vault Secret support

Aspire now supports `IAzureKeyVaultSecretReference`, a new primitive for modeling secrets directly in the app model. This replaces `BicepSecretOutputReference` and gives finer grain control over Key Vault creation when using `AzureBicepResource`.

You can now:

- Add a shared Key Vault in C#
- Configure services that support keys (e.g., Redis, Cosmos DB) to store their secrets there
- Reference those secrets in your app as environment variables or via the Key Vault config provider

Use KeyVault directly in the "api" project:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var vault = builder.AddAzureKeyVault("kv");

var redis = builder.AddAzureRedis("redis")
                   .WithAccessKeyAuthentication(vault);

builder.AddProject<Projects.Api>("api")
       .WithReference(vault);
```

Let the compute environment handle the secret management for you:

```csharp
var redis = builder.AddAzureRedis("redis")
                   .WithAccessKeyAuthentication();

builder.AddProject<Projects.Api>("api")
       .WithReference(redis);
```

**Previous behavior:**

  `azd` created and managed secret outputs using a key vault per resource, with no visibility in the app model. These Key Vault resources were handled implicitly and couldn't be customized in C#.

**New behavior in 9.2:**

  Calling `WithKeyAccessAuthentication` or `WithPasswordAuthentication` now creates an actual `AzureKeyVaultResource` (or accepts a reference to one), and stores connection strings there. Secret names follow the pattern `connectionstrings--{resourcename}` to prevent naming conflicts with other vault entries.

### 🔒 Improved default permissions for Azure Key Vault references

When referencing a Key Vault, Aspire previously granted the broad **Key Vault Administrator** role by default. In 9.2, this has been changed to **Key Vault Secrets User**, which provides read-only access to secrets—suitable for most application scenarios.

This update continues the security-focused improvements in this release.

## 🚀 Deployment improvements

We're excited to announce several new deployment features in Aspire 9.2, including:

### 📦 Publishers (Preview)

Publishers are a new extensibility point in Aspire that allow you to define how your distributed application gets transformed into deployable assets. Rather than relying on an [intermediate manifest format](../deployment/manifest-format.md), publishers can now plug directly into the application model to generate Docker Compose files, Kubernetes manifests, Azure resources, or whatever else your environment needs.

When Aspire launched, it introduced a deployment manifest format—a serialized snapshot of the application model. While useful it burdened deployment tools with interpreting the manifest and resource authors with ensuring accurate serialization. This approach also complicated schema evolution and target-specific behaviors.

Publishers simplify this process by working directly with the full application model in-process, enabling richer, more flexible, and maintainable publishing experiences.

The following NuGet packages expose preview publishers:

- [📦 Aspire.Hosting.Azure](https://www.nuget.org/packages/Aspire.Hosting.Azure)
- [📦 Aspire.Hosting.Docker (Preview)](https://www.nuget.org/packages/Aspire.Hosting.Docker)
- [📦 Aspire.Hosting.Kubernetes (Preview)](https://www.nuget.org/packages/Aspire.Hosting.Kubernetes)

> [!IMPORTANT]
> The Docker and Kubernetes publishers were contributed by community contributor, [Dave Sekula](https://github.com/Prom3theu5)—a great example of the community stepping up to extend the model. 💜 Thank you, Dave!

To use a publisher, add the corresponding NuGet package to your app host project file and then call the `Add[Name]Publisher()` method in your app host builder.

```csharp
builder.AddDockerComposePublisher();
```

> [!TIP]
> Publisher registration methods follow the `Add[Name]Publisher()` convention.

You can also build your own publisher by implementing the publishing APIs and calling your custom registration method. Some publishers are still in preview, and the APIs are subject to change. The goal is to provide a more flexible and extensible way to publish distributed applications, making it easier to adapt to different deployment environments and scenarios.

### 🆕 Aspire CLI (Preview)

Aspire 9.2 introduces the new **`aspire` CLI**, a tool for creating, running, and publishing Aspire applications from the command line. It provides a rich, interactive experience tailored for Aspire users.

The CLI is available as a .NET tool and can be installed with:

```bash
dotnet tool install --global aspire.cli --prerelease
```

#### Example usage

```bash
aspire new
aspire run
aspire add redis
aspire publish --publisher docker-compose
```

#### Available commands

- `new <template>` – Create a new Aspire sample project  
- `run` – Run an Aspire app host in development mode  
- `add <integration>` – Add an integration to your project  
- `publish` – Generate deployment artifacts from your app host

🧪 The CLI is **preview**. We're exploring how to make it a first-class experience for Aspire users—your feedback is welcome!

## 🧪 Testing template updates

The xUnit testing project template now supports a version selector, allowing the user to select either:

- `v2`: The previous xUnit testing experience.
- `v3`: The new xUnit testing experience and template.
- `v3 with Microsoft Test Platform`: The next xUnit testing experience, template and uses the [Microsoft Testing Platform](/dotnet/core/testing/microsoft-testing-platform-intro).

By default, to the `v3` experience. For more information, see:

- [What's new in xUnit v.3](https://xunit.net/docs/getting-started/v3/whats-new)
- [Microsoft Testing Platform support in xUnit.net v3](https://xunit.net/docs/getting-started/v3/microsoft-testing-platform)

> [!NOTE]
> Both `v3` versions are only supported with Aspire 9.2 or later.

## 💔 Breaking changes

With every release, we strive to make Aspire better. However, some changes may break existing functionality. The following breaking changes are introduced in Aspire 9.2:

- [Breaking changes in Aspire 9.2](../compatibility/9.2/index.md)