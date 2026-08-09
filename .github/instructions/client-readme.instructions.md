---
applyTo: "src/Components/**/README.md"
---

# README.md Instructions for Client Integration Packages

This document provides guidelines for writing and maintaining README.md files for Aspire client integration packages located under `src/Components/**/README.md`.

## Purpose

Client integration packages implement wrappers for client-side code that help applications work better within Aspire for local development and are optimized for production use (with retries, tracing, health checks, etc.). The README.md files help developers understand how to use these packages in their service projects to connect to infrastructure resources.

## Standard Structure

All client integration README.md files should follow this structure:

### 1. Title and Description

```markdown
# Aspire.{Technology} library

Registers {a/an} [{ClientInterface}]({link to docs}) in the DI container for connecting {to} {technology description}. Enables corresponding health check{s}, {metrics,} logging{,} and telemetry.
```

**Guidelines:**
- Title format: `# Aspire.{Technology} library`
- Use "library" (not "package" or "component")
- Start with "Registers [InterfaceName](link) in the DI container..."
- Link the client interface to its official documentation
- Specify what the component enables: health checks, metrics (if applicable), logging, telemetry/tracing
- Common patterns:
  - "Enables corresponding health check, logging and telemetry"
  - "Enables corresponding health checks, logging and telemetry"
  - "Enables corresponding health check, metrics, logging and telemetry"
  - "Enables connection pooling, retries, health check, logging and telemetry" (for EF Core)

### 2. Getting Started Section

```markdown
## Getting started

### Prerequisites

- {Technology} {server/database/service} and {connection string/hostname} for {accessing/connecting to} the {resource}.

### Install the package

Install the Aspire {Technology} library with [NuGet](https://www.nuget.org):

\```dotnetcli
dotnet add package Aspire.{Technology}
\```
```

**Guidelines:**
- Always include a "Prerequisites" subsection listing what's needed
- Common prerequisites: server/database and connection string or hostname
- For Azure services, include: "Azure subscription - [create one for free](https://azure.microsoft.com/free/)"
- Installation command should be in a `dotnetcli` code block
- Use consistent phrasing: "Install the Aspire {Technology} library with [NuGet](https://www.nuget.org):"

### 3. Usage Example

```markdown
## Usage example

In the _Program.cs_ file of your project, call the `Add{Technology}{Client/DbContext}` extension method to register {a/an} `{InterfaceName}` for use via the dependency injection container. The method takes a connection name parameter.

\```csharp
builder.Add{Technology}{Client}("{connectionName}");
\```

You can then retrieve the `{InterfaceName}` instance using dependency injection. For example, to retrieve the {client/connection/context} from a Web API controller:

\```csharp
private readonly {InterfaceName} _{variableName};

public ProductsController({InterfaceName} {variableName})
{
    _{variableName} = {variableName};
}
\```

{Optional: See the [{Technology} documentation](link) for examples on using the `{InterfaceName}`.}
```

**Guidelines:**
- Start with "In the _Program.cs_ file of your project, call the `Add{Technology}...` extension method..."
- Show the minimal registration call with a connection name parameter
- Include a dependency injection example using a controller
- Use meaningful variable names (e.g., `_client`, `_cache`, `_context`, `_dataSource`)
- Use `ProductsController` as the example controller name for consistency
- Link to official technology documentation for additional usage examples
- Note: Say "_Program.cs_ file" (not "AppHost.cs" - that's for hosting integrations)

### 4. Configuration Section

This is the most detailed section and should cover all configuration approaches:

```markdown
## Configuration

The Aspire {Technology} {component/library} provides multiple options to configure the {connection/resource} based on the requirements and conventions of your project. {Note about required configuration if applicable}

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.Add{Technology}{Client}()`:

\```csharp
builder.Add{Technology}{Client}("{connectionName}");
\```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section:

\```json
{
  "ConnectionStrings": {
    "{connectionName}": "{example connection string}"
  }
}
\```

{For components that support both ServiceUri and ConnectionString, show both formats in subsections}

See the [ConnectionString documentation]({link}) for more information on how to format this connection string.

### Use configuration providers

The Aspire {Technology} {component/library} supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `{SettingsClassName}` {and `{ClientOptionsClassName}` if applicable} from configuration by using the `Aspire:{Technology}:{Component}` key. Example `appsettings.json` that configures some of the options:

\```json
{
  "Aspire": {
    "{Technology}": {
      "{Component}": {
        "{ConfigProperty}": {value},
        "DisableHealthChecks": true,
        "DisableTracing": false
      }
    }
  }
}
\```

### Use inline delegates

{Also/You can also pass} the `Action<{SettingsClassName}> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

\```csharp
builder.Add{Technology}{Client}("{connectionName}", settings => settings.DisableHealthChecks = true);
\```

{If applicable, show configureOptions delegate example}

You can also setup the [{ClientOptionsClassName}]({link}) using the {`Action<{ClientOptionsClassName}> configureOptions` delegate parameter/optional `Action<IAzureClientBuilder<{ClientInterface}, {ClientOptionsClassName}>> configureClientBuilder` parameter} of the `Add{Technology}{Client}` method. For example{, to set the connection timeout/to configure client options}:

\```csharp
builder.Add{Technology}{Client}("{connectionName}", configureOptions: options => options.{Property} = {value});
\```
```

**Guidelines:**
- Always show three configuration approaches: connection strings, configuration providers, and inline delegates
- Connection string examples should be realistic and properly formatted
- Configuration provider examples should show the nested JSON structure with the proper `Aspire:` prefix
- Include the configuration key path (e.g., `Aspire:StackExchange:Redis`)
- Show `DisableHealthChecks` and `DisableTracing` as common examples
- Inline delegate examples should demonstrate practical use cases
- Link to official documentation for connection string format and client options
- For Azure clients, show both ServiceUri (recommended) and ConnectionString formats
- Include notes about DefaultAzureCredential when applicable

### 5. AppHost Extensions Section

```markdown
## AppHost extensions

In your AppHost project, install the `Aspire.Hosting.{Technology}` library with [NuGet](https://www.nuget.org):

\```dotnetcli
dotnet add package Aspire.Hosting.{Technology}
\```

Then, in the _AppHost.cs_ file of `AppHost`, register {a/an} {Technology} {server/database/resource} and consume the connection using the following methods:

\```csharp
var {resource} = builder.Add{Technology}("{resourceName}"){.AddDatabase("dbname") if applicable};

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference({resource});
\```

The `WithReference` method configures a connection in the `MyService` project named `{connectionName}`. In the _Program.cs_ file of `MyService`, the {connection/client} can be consumed using:

\```csharp
builder.Add{Technology}{Client}("{connectionName}");
\```
```

**Guidelines:**
- Show the complete round-trip from AppHost to service project
- Include installation of the corresponding hosting package
- Demonstrate the `Add{Technology}` and `WithReference` pattern
- Explicitly explain that `WithReference` configures the connection name
- Show how the connection name matches between AppHost and service project
- For Azure services in AppHost, may need to show `ExecutionContext.IsPublishMode` pattern
- Note the file name difference: _AppHost.cs_ in AppHost vs _Program.cs_ in service project

### 6. Additional Documentation

```markdown
## Additional documentation

* {Link to official technology documentation}
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire
```

**Guidelines:**
- Always include a link to the official technology/SDK documentation
- Always include the link to Aspire Components README: `https://github.com/microsoft/aspire/tree/main/src/Components/README.md`
- Use bulleted list format with `*` prefix
- "Feedback & contributing" section should be separate with just the GitHub link

### 7. Trademark Notices (if applicable)

```markdown
_{Trademark notice text}_
```

**Guidelines:**
- Place trademark notices at the very end after "Feedback & contributing"
- Use markdown italic formatting.
- The trademark itself should be bolded using asterisks
- Common examples:
  - Redis: `_*Redis* is a registered trademark of Redis Ltd. Any rights therein are reserved to Redis Ltd._`
  - PostgreSQL: `_*Postgres*, *PostgreSQL* and the *Slonik Logo* are trademarks or registered trademarks of the PostgreSQL Community Association of Canada, and used with their permission._`

## Complete Example

Here's a complete example for a client integration:

```markdown
# Aspire.Npgsql library

Registers [NpgsqlDataSource](https://www.npgsql.org/doc/api/Npgsql.NpgsqlDataSource.html) in the DI container for connecting to a PostgreSQL®* database. Enables corresponding health check, metrics, logging and telemetry.

## Getting started

### Prerequisites

- PostgreSQL database and connection string for accessing the database.

### Install the package

Install the Aspire PostgreSQL Npgsql library with [NuGet](https://www.nuget.org):

\```dotnetcli
dotnet add package Aspire.Npgsql
\```

## Usage example

In the _Program.cs_ file of your project, call the `AddNpgsqlDataSource` extension method to register a `NpgsqlDataSource` for use via the dependency injection container. The method takes a connection name parameter.

\```csharp
builder.AddNpgsqlDataSource("postgresdb");
\```

You can then retrieve the `NpgsqlDataSource` instance using dependency injection. For example, to retrieve the data source from a Web API controller:

\```csharp
private readonly NpgsqlDataSource _dataSource;

public ProductsController(NpgsqlDataSource dataSource)
{
    _dataSource = dataSource;
}
\```

## Configuration

The Aspire PostgreSQL Npgsql component provides multiple options to configure the database connection based on the requirements and conventions of your project.

### Use a connection string

When using a connection string from the `ConnectionStrings` configuration section, you can provide the name of the connection string when calling `builder.AddNpgsqlDataSource()`:

\```csharp
builder.AddNpgsqlDataSource("myConnection");
\```

And then the connection string will be retrieved from the `ConnectionStrings` configuration section:

\```json
{
  "ConnectionStrings": {
    "myConnection": "Host=myserver;Database=test"
  }
}
\```

See the [ConnectionString documentation](https://www.npgsql.org/doc/connection-string-parameters.html) for more information on how to format this connection string.

### Use configuration providers

The Aspire PostgreSQL Npgsql component supports [Microsoft.Extensions.Configuration](https://learn.microsoft.com/dotnet/api/microsoft.extensions.configuration). It loads the `NpgsqlSettings` from configuration by using the `Aspire:Npgsql` key. Example `appsettings.json` that configures some of the options:

\```json
{
  "Aspire": {
    "Npgsql": {
      "DisableHealthChecks": true,
      "DisableTracing": true
    }
  }
}
\```

### Use inline delegates

Also you can pass the `Action<NpgsqlSettings> configureSettings` delegate to set up some or all the options inline, for example to disable health checks from code:

\```csharp
builder.AddNpgsqlDataSource("postgresdb", settings => settings.DisableHealthChecks = true);
\```

## AppHost extensions

In your AppHost project, install the `Aspire.Hosting.PostgreSQL` library with [NuGet](https://www.nuget.org):

\```dotnetcli
dotnet add package Aspire.Hosting.PostgreSQL
\```

Then, in the _AppHost.cs_ file of `AppHost`, register a Postgres database and consume the connection using the following methods:

\```csharp
var postgresdb = builder.AddPostgres("pg").AddDatabase("postgresdb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(postgresdb);
\```

The `WithReference` method configures a connection in the `MyService` project named `postgresdb`. In the _Program.cs_ file of `MyService`, the database connection can be consumed using:

\```csharp
builder.AddNpgsqlDataSource("postgresdb");
\```

## Additional documentation

* https://www.npgsql.org/doc/basic-usage.html
* https://github.com/microsoft/aspire/tree/main/src/Components/README.md

## Feedback & contributing

https://github.com/microsoft/aspire

_*Postgres*, *PostgreSQL* and the *Slonik Logo* are trademarks or registered trademarks of the *PostgreSQL Community Association of Canada*, and used with their permission._
```

## Key Principles

1. **Be comprehensive**: Client READMEs need to cover all configuration approaches
2. **Show the full workflow**: Demonstrate both AppHost and service project usage
3. **Configuration is key**: Provide detailed configuration examples with all three methods
4. **Enable observability**: Emphasize health checks, metrics, logging, and telemetry
5. **Link to documentation**: Provide links to both Aspire and technology-specific docs
6. **Consistent formatting**: Use the same structure and phrasing across all client integration READMEs

## Configuration Key Patterns

Client integrations use consistent configuration key patterns:

- **Pattern**: `Aspire:{Technology}:{Component}`
- Examples:
  - `Aspire:Npgsql` (single word)
  - `Aspire:StackExchange:Redis`
  - `Aspire:Microsoft:EntityFrameworkCore:SqlServer`
  - `Aspire:Azure:Storage:Blobs`
  - `Aspire:MongoDB:Driver`
  - `Aspire:RabbitMQ:Client`

## Settings Class Naming

Settings classes follow consistent naming:
- **Pattern**: `{Technology}{Component}Settings`
- Examples:
  - `NpgsqlSettings`
  - `StackExchangeRedisSettings`
  - `MicrosoftEntityFrameworkCoreSqlServerSettings`
  - `AzureStorageBlobsSettings`
  - `MongoDBSettings`
  - `RabbitMQClientSettings`

## Common Mistakes to Avoid

- ❌ Don't skip any of the three configuration approaches (connection string, providers, inline delegates)
- ❌ Don't forget the "AppHost extensions" section - this is critical for showing the full workflow
- ❌ Don't use "AppHost.cs" when referring to service project code (use "_Program.cs_")
- ❌ Don't omit prerequisites
- ❌ Don't forget to show the dependency injection example
- ❌ Don't use inconsistent configuration key naming
- ❌ Don't forget the link to `https://github.com/microsoft/aspire/tree/main/src/Components/README.md`
- ❌ Don't explain health checks, telemetry, or observability in detail (they're automatically enabled)
- ❌ Don't forget trademark notices when applicable

## Entity Framework Core Specific Guidelines

For EF Core integrations (e.g., `Aspire.Microsoft.EntityFrameworkCore.SqlServer`):

1. **Title description** should mention:
   - "Registers [EntityFrameworkCore](link) [DbContext](link) service..."
   - "Enables connection pooling, retries, health check, logging and telemetry"

2. **Usage section** should show:
   - `Add{Technology}DbContext<TContext>("connectionName")`
   - Also show the `Enrich{Technology}DbContext<TContext>` pattern for manual DbContext registration

3. **Configuration section** should note:
   - The `Enrich` method doesn't use the `ConnectionStrings` configuration section

## Azure Service Specific Guidelines

For Azure service integrations:

1. **Prerequisites** should include:
   - Link to create free Azure subscription
   - Links to create the specific Azure resource

2. **Connection string formats** should show:
   - ServiceUri (recommended, works with DefaultAzureCredential)
   - Connection string (alternative)

3. **AppHost extensions** may show:
   - `ExecutionContext.IsPublishMode` pattern for conditional Azure resource usage

## When to Update

Update client integration README.md files when:
- Adding new configuration options
- Changing the primary usage pattern or API
- Adding new extension methods
- Updating configuration key paths
- New features are added (health checks, metrics, etc.)
- New aspire.dev or technology documentation becomes available

## Review Checklist

When reviewing or creating a client integration README.md:

- [ ] Title follows the format: `# Aspire.{Technology} library`
- [ ] Description specifies what's registered in DI and what's enabled
- [ ] Prerequisites section lists all requirements
- [ ] Installation uses correct package name in `dotnetcli` code block
- [ ] Usage example shows registration and dependency injection
- [ ] Configuration section includes all three approaches (connection string, providers, delegates)
- [ ] Configuration key path follows the `Aspire:{Technology}:{Component}` pattern
- [ ] JSON examples are properly formatted and realistic
- [ ] AppHost extensions section shows the full workflow
- [ ] Connection name matches between AppHost and service examples
- [ ] Additional documentation includes technology link and Components README link
- [ ] Feedback & contributing section is present
- [ ] Trademark notices are included if applicable
- [ ] Consistent formatting and style with other client integration READMEs
- [ ] No confusion between "_AppHost.cs_" and "_Program.cs_" contexts
