# Aspire.Hosting.EntityFrameworkCore library

Provides extension methods and resource definitions for an Aspire AppHost to configure Entity Framework Core migration management.

## Getting started

### Prerequisites

The target project must reference `Microsoft.EntityFrameworkCore.Design`. Add the following to your project file:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="x.y.z" />
</ItemGroup>
```

Note: Using `dotnet add package` will add the reference with `PrivateAssets="All"` which may not work correctly with the migration commands.

### Install the package

In your AppHost project, install the Aspire EntityFrameworkCore Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.EntityFrameworkCore
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add EF migrations to a project resource:

```csharp
var api = builder.AddProject<Projects.Api>("api");

// Add EF migrations for a specific DbContext
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext");
```

### Resource Commands

When `AddEFMigrations` is called, the migration resource appears in the Aspire Dashboard with the following commands:

| Command | Description |
|---------|-------------|
| Update Database | Apply pending migrations to the database |
| Drop Database | Delete the database (requires confirmation) |
| Reset Database | Drop and recreate the database with all migrations (requires confirmation) |
| Add Migration... | Create a new migration |
| Remove Migration | Remove the last migration |
| Get Database Status | Show the current migration status |

> **Note:** After adding or removing a migration, all commands are disabled until the target project is recompiled. This prevents executing commands against stale assemblies.

### Automatic Tool Installation

The `dotnet-ef` tool is automatically downloaded and executed using `dotnet tool exec` when commands are run. You don't need to install it globally or in a local tool manifest.

### Configuring the dotnet-ef Tool

You can customize the `dotnet-ef` tool version, NuGet sources, or allow prerelease versions:

```csharp
var api = builder.AddProject<Projects.Api>("api");

// Use a specific version of dotnet-ef
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext",
    configureToolResource: tool =>
    {
        tool.WithToolVersion("10.0.0");
    });

// Allow prerelease versions
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext",
    configureToolResource: tool =>
    {
        tool.WithToolPrerelease();
    });

// Use a custom NuGet source
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext",
    configureToolResource: tool =>
    {
        tool.WithToolSource("https://api.nuget.org/v3/index.json")
            .WithToolSource("https://my-feed.example.com/v3/index.json");
    });
```

### Running migrations on startup

You can configure migrations to run automatically when the AppHost starts:

```csharp
var api = builder.AddProject<Projects.Api>("api");

// Add EF migrations and run on startup
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext")
    .RunDatabaseUpdateOnStart();

// Other resources can wait for migrations to complete
var worker = builder.AddProject<Projects.Worker>("worker")
                    .WaitFor(apiMigrations);
```

When `RunDatabaseUpdateOnStart()` is called, a health check is automatically registered for the migration resource. This enables other resources to use `.WaitFor()` to wait until migrations complete before starting. The resource transitions through the following states:

- **Pending** - Initial state before migrations start
- **Running** - Migrations are being applied
- **Active** - Migrations completed successfully
- **FailedToStart** - Migration failed

### Migration Configuration Options

Configure where new migrations are created using the Add Migration command:

```csharp
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext")
    .WithMigrationOutputDirectory("Data/Migrations")  // Custom output directory
    .WithMigrationNamespace("MyApp.Data.Migrations"); // Custom namespace
```

### Separate Migration Project

When migrations are in a different project than the startup project, use `WithMigrationsProject`:

```csharp
var startup = builder.AddProject<Projects.Api>("api");

// Using a project metadata type (recommended)
var apiMigrations = startup.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext")
    .WithMigrationsProject<Projects.Data>();

// Or using a project path
var apiMigrations = startup.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext")
    .WithMigrationsProject("../MyApp.Data/MyApp.Data.csproj");
```

### Multiple DbContexts

You can add migrations for multiple DbContexts in the same project:

```csharp
var api = builder.AddProject<Projects.Api>("api");

var userMigrations = api.AddEFMigrations("user-migrations", "MyApp.Data.UserDbContext");
var orderMigrations = api.AddEFMigrations("order-migrations", "MyApp.Data.OrderDbContext");
```

### Publishing Support

Configure migration script or bundle generation during publishing:

```csharp
// Generate SQL script during publish
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext")
    .PublishAsMigrationScript();

// Or generate a self-contained migration bundle executable
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext")
    .PublishAsMigrationBundle();
```

When publishing, the subscriber will generate the configured artifacts and log the results.

## Additional documentation

<!-- TODO: Update this to the EntityFrameworkCore-specific page once published, https://github.com/microsoft/aspire.dev/issues/536 -->
https://learn.microsoft.com/dotnet/aspire/
https://learn.microsoft.com/ef/core/managing-schemas/migrations/

## Feedback & contributing

https://github.com/dotnet/aspire
