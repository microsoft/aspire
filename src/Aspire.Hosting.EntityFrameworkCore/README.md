# Entity Framework Core hosting integration

Use this integration to model, configure, and orchestrate Entity Framework Core migration management in an Aspire solution.

## Getting started

### Prerequisites

The target project must expose EF Core design-time services so migration commands can create design-time `DbContext` instances.

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.EntityFrameworkCore` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.EntityFrameworkCore
aspire add Aspire.Hosting.Dotnet
```

## Usage example

In the AppHost, add EF migrations to a project resource:

**C#**

```csharp
var api = builder.AddDotnetProject("api", "../Api/Api.csproj");

// Add EF migrations for a specific DbContext
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext");
```

The four generalized `AddEFMigrations` forms (with or without a context name and tool callback) accept
`IDotnetProgramResource` builders without `ASPIREPROJECTS001`, including V2 and retained legacy project
resources. Each returns `IResourceBuilder<EFMigrationResource>`, not a project builder.
SDK-style C#, F#, and VB project files are supported. File-based C# apps are not supported because the
EF Core CLI requires a project file for its `--project` and `--startup-project` arguments.

**TypeScript**

```typescript
const api = await builder.addDotnetProject("api", "../Api/Api.csproj");

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
var api = builder.AddDotnetProject("api", "../Api/Api.csproj");

// Use a specific migration tool version
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext",
    configureToolResource: tool =>
    {
        tool.WithToolVersion("10.0.0");
    });
```

Alternatively, allow prerelease versions:

```csharp
// Allow prerelease versions
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext",
    configureToolResource: tool =>
    {
        tool.WithToolPrerelease();
    });
```

Or use a custom package source:

```csharp
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
var api = builder.AddDotnetProject("api", "../Api/Api.csproj");

// Add EF migrations and run on startup
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext")
    .RunDatabaseUpdateOnStart();

// Other resources can wait for migrations to complete
var worker = builder.AddDotnetProject("worker", "../Worker/Worker.csproj")
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
var startup = builder.AddDotnetProject("api", "../Api/Api.csproj");

var apiMigrations = startup.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext")
    .WithMigrationsProject("../Data/Data.csproj");
```

Ensure `Data` is built by the startup project's normal references or explicitly before run-mode EF
commands. The path overload sets the migrations path; it does not register build readiness or
preserve a resource's configured providers.

The existing `WithMigrationsProject<Projects.Data>()` form is still valid when its metadata producer
and required project/build references are retained. It is not an obsolete `AddProject` overload.
Do not delete those references merely because the startup resource now uses a path. Likewise, a
polyglot resource-backed migrations-project argument preserves identity, providers, and coordinated
build readiness that a path alone cannot replace. See the [migration guide](../../docs/migrate-from-addproject.md#a-separate-migrations-project).

### Multiple DbContexts

You can add migrations for multiple DbContexts in the same project:

```csharp
var api = builder.AddDotnetProject("api", "../Api/Api.csproj");

var userMigrations = api.AddEFMigrations("user-migrations", "MyApp.Data.UserDbContext");
var orderMigrations = api.AddEFMigrations("order-migrations", "MyApp.Data.OrderDbContext");
```

### Publishing Support

Configure migration script or bundle generation during publishing:

```csharp
var apiMigrations = api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext")
    .PublishAsMigrationScript();
```

Or generate a self-contained migration bundle executable instead:

```csharp
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
var api = builder.AddDotnetProject("api", "../Api/Api.csproj").WithReference(db);

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

## Resource identity and direct construction

Use `apiMigrations.Resource.StartupProjectResource` for the startup program across both resource
models. The already-obsolete `ProjectResource` property retains its legacy return type and throws
when the startup resource is V2.

Both `EFMigrationResource(string, ProjectResource, string?)` and the public generalized
`EFMigrationResource(string, IDotnetProgramResource, string?)` constructor create **data only**.
The generalized constructor is non-experimental, but neither constructor registers a resource or
provides `AddEFMigrations`' duplicate checks, tool initialization, environment copying, dashboard
commands, parent relationship, or pipeline setup. Use the factory for a working integration:

```csharp
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.EntityFrameworkCore;

IResourceBuilder<EFMigrationResource> migrations =
    api.AddEFMigrations("api-migrations", "MyApp.Data.MyDbContext");
IDotnetProgramResource startup = migrations.Resource.StartupProjectResource;
```

## Coordinated builds and custom build inputs

With `AddDotnetProject`, local EF operations wait for the coordinated build to finish successfully before
starting `dotnet-ef`. This includes automatic startup migrations and dashboard commands. The wait is for build
output, not for the application to start, so `api.WaitFor(migrations)` remains supported.

EF operations do **not** forward Aspire-specific `WithBuildEnvironment` customizations as MSBuild global
properties to `dotnet-ef`. When the startup or a known migrations project has `WithBuildEnvironment`,
`WithDotnetProgramBuildEnvironment`, or custom build-property metadata, each requested EF operation logs a
warning and continues. This also applies to script and bundle generation during publishing.

Waiting for the build does not guarantee that EF selects the same assemblies: EF may use suitable output,
select different or stale output, or fail because its expected output is missing. Ordinary run-mode commands
retain `--no-build`; publish-time script and bundle generation allow EF to build the participating projects.
Actual EF failures still fail the operation, including automatic migrations and publishing steps.

Where equivalent, move the required settings into shared project-file or `Directory.Build.props` configuration
so both evaluations agree. Runtime `WithEnvironment` is not equivalent to MSBuild global properties, especially
when the project assigns a property unconditionally. The warning does not resolve this compatibility risk.

## Additional documentation

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/databases/efcore/migrations/
https://learn.microsoft.com/ef/core/managing-schemas/migrations/

## Feedback & contributing

https://github.com/microsoft/aspire
