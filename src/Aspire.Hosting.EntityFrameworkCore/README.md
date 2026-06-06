# Entity Framework Core hosting integration

Use this integration to model, configure, and orchestrate Entity Framework Core migration management in an Aspire solution.

## Getting started

### Prerequisites

The target project must expose EF Core design-time services so migration commands can create design-time `DbContext` instances.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.EntityFrameworkCore` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.EntityFrameworkCore
```

## Usage example

In the AppHost, add EF migrations to a project resource:

**C#**

```csharp
var api = builder.AddProject<Projects.Api>("api");

// Add EF migrations for a specific DbContext
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext");
```

**TypeScript**

```typescript
const api = await builder.addProject("api", "../Api");

// Add EF migrations for a specific DbContext
const apiMigrations = await api.addEFMigrations("api-migrations", "MyApp.Data.MyDbContext");
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

> **Note:** After adding or removing a migration, all commands are disabled until the target app is rebuilt. This prevents executing commands against stale assemblies.

### Automatic tool installation

The EF migration command-line tool is downloaded and executed on demand when commands run. You don't need to install it globally.

### Configuring the migration tool

You can customize the migration tool version, package sources, or allow prerelease versions:

```csharp
var api = builder.AddProject<Projects.Api>("api");

// Use a specific migration tool version
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

// Use a custom package source
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext",
    configureToolResource: tool =>
    {
        tool.WithToolSource("https://my-feed.example.com/v3/index.json");
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
                    .WaitForCompletion(apiMigrations);
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

var apiMigrations = startup.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext")
    .WithMigrationsProject<Projects.Data>();
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
// Generate a SQL migration script during publish
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext")
    .PublishAsMigrationScript();

// Or generate a self-contained migration bundle executable
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext")
    .PublishAsMigrationBundle();
```

When publishing, these methods add pipeline steps that run during `aspire publish` and write
their artifacts into the publish output directory under `efmigrations/`.

#### Publishing the migration bundle as a container image

Passing `publishContainer: true` to `PublishAsMigrationBundle` tells Aspire to also wrap the
generated bundle in a container image. The migration resource becomes a compute resource that
each compute environment (Docker Compose, Azure Container Apps, Kubernetes, Azure App Service,
Azure Functions, etc.) deploys exactly like any other container you add to the AppHost.

```csharp
var db  = builder.AddPostgres("pg").AddDatabase("appdb");
var api = builder.AddProject<Projects.Api>("api").WithReference(db);

var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext")
    .WaitFor(db)                                    // required — see below
    .PublishAsMigrationBundle(publishContainer: true);
```

- The generated `Dockerfile` uses the runtime image selected by the integration for the configured target runtime.
- The `targetRuntime` argument defaults to `linux-x64` when `publishContainer: true`; override it
  explicitly to publish for a different architecture (e.g., `linux-arm64`).
- The container reads its connection string from the standard environment variable that aspire wires
  up automatically for every `IResourceWithConnectionString` that you pass to `.WaitFor(...)` on the
  migration resource — the same way it does for any other compute resource with a database dependency.
- `.WaitFor(database)` is required; the bundle cannot run without a connection string.
- Run mode (`aspire run`) is unaffected — no container image is built locally. The migration
  resource still appears in the dashboard with its tool commands (Update Database, etc.). Container
  wiring only activates under `aspire publish`.

#### Run-once semantics per environment

A migration bundle is idempotent — running it twice is safe, the second run is a no-op — but
different compute environments have different "run to completion" mechanisms, so there is no
single environment-agnostic flag that means "don't restart after the container exits". Use the
appropriate environment-specific helper after `PublishAsMigrationBundle(publishContainer: true)`
to avoid the container being restarted after it finishes:

**Azure Container Apps** — publish as a manually-triggered [Azure Container App Job](https://learn.microsoft.com/azure/container-apps/jobs):

```csharp
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext")
    .WaitFor(db)
    .PublishAsMigrationBundle(publishContainer: true)
    .PublishAsAzureContainerAppJob(); // requires Aspire.Hosting.Azure.AppContainers
```

The container app job runs once per manual invocation and stops.

**Docker Compose** — tell Compose not to restart the container after it exits:

```csharp
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext")
    .WaitFor(db)
    .PublishAsMigrationBundle(publishContainer: true)
    .PublishAsDockerComposeService((_, service) => service.Restart = "no"); // requires Aspire.Hosting.Docker
```

The bundle runs to completion during `docker compose up` and then stops; it will only run again
on subsequent `up` calls, which is safe because the bundle is idempotent.

**Kubernetes** — customize the generated manifest to a `Job` or set `restartPolicy: OnFailure`
using your Kubernetes publisher of choice (for example via `AddKubernetesEnvironment()` and the
matching customization hook).

**Azure App Service / Azure Functions** — App Service and Functions keep compute instances warm,
so the preferred pattern is **not** to deploy the bundle as part of those environments. Instead,
run the bundle as a pre-deployment or init step — see "Generate the bundle and execute it
yourself" below.

#### Generate the bundle and execute it yourself

If you only want the `aspire publish` step to produce the bundle artifact (for example to run it
from a deployment pipeline, a database-administrator's shell, or a CI job), omit
`publishContainer: true`:

```csharp
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext")
    .PublishAsMigrationBundle(); // artifact only, no Dockerfile
```

This writes the platform-native executable to `<publish-output>/efmigrations/<name>[.exe]` and
does not add any compute resource to the deployment manifest. Execute the bundle when appropriate
for your deployment flow:

```bash
./<publish-output>/efmigrations/api-migrations --connection "$CONNECTION_STRING" --verbose
```

Similarly you can use `PublishAsMigrationScript()` if you also want a raw SQL script produced.

## Additional documentation

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/databases/efcore/migrations/
https://learn.microsoft.com/ef/core/managing-schemas/migrations/

## Feedback & contributing

https://github.com/microsoft/aspire
