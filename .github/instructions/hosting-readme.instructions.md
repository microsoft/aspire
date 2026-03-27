---
applyTo: "src/Aspire.Hosting*/README.md"
---

# README.md Instructions for Hosting Integration Packages

This document provides guidelines for writing and maintaining README.md files for Aspire hosting integration packages located under `src/Aspire.Hosting*/README.md`.

## Purpose

Hosting integration packages provide extension methods and resource definitions for the Aspire AppHost. They enable developers to configure and orchestrate infrastructure resources (databases, message queues, caches, cloud services, etc.) in their distributed applications. The README.md files help developers understand how to add and configure these resources in their AppHost project.

## Standard Structure

All hosting integration README.md files should follow this structure:

### 1. Title and Description

```markdown
# Aspire.Hosting.{Technology} library

Provides extension methods and resource definitions for an Aspire AppHost to configure {a/an} {Technology} {resource type}.
```

**Guidelines:**

- Title format: `# Aspire.Hosting.{Technology} library`
- Use "library" (not "package" or "component")
- Start description with "Provides extension methods and resource definitions for an Aspire AppHost to configure..."
- Be specific about what type of resource is being configured (e.g., "a SQL Server database resource", "a MongoDB resource", "Azure CosmosDB")

### 2. Getting Started Section

````markdown
## Getting started

### Prerequisites

{List any prerequisites such as Azure subscription, if applicable}

### Install the package

In your AppHost project, install the Aspire {Technology} Hosting library with [NuGet](https://www.nuget.org):

\```dotnetcli
dotnet add package Aspire.Hosting.{Technology}
\```
````

**Guidelines:**

- Include a "Prerequisites" subsection only if there are specific requirements (e.g., Azure subscription for Azure resources)
- Installation command should be in a `dotnetcli` code block
- Use consistent phrasing: "In your AppHost project, install the Aspire {Technology} Hosting library with [NuGet](https://www.nuget.org):"

### 3. Usage Example

````markdown
## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add {a/an} {Technology} resource and consume the connection using the following methods:

\```csharp
var {resourceName} = builder.Add{Technology}("{name}"){.AddDatabase("dbname") if applicable};

var myService = builder.AddProject<Projects.MyService>()
.WithReference({resourceName});
\```
````

**Guidelines:**

- Start with "Then, in the _AppHost.cs_ file of `AppHost`, add..."
- Show the minimal working example
- Use descriptive variable names that match the technology (e.g., `redis`, `postgres`, `sql`, `mongodb`)
- Include chained methods like `.AddDatabase()` when applicable
- Show the `WithReference` pattern to demonstrate resource consumption
- Keep examples simple and focused on the most common use case

### 4. Additional Sections (Optional)

#### Emulator Usage (if applicable)

For Azure services that support emulators:

````markdown
### Emulator usage

Aspire supports the usage of the {Azure Service} emulator. To use the emulator, add the following to your AppHost project:

\```csharp
// AppHost
var {resource} = builder.Add{AzureService}("{name}").RunAsEmulator();
\```

When the AppHost starts up, a local container running the {Azure Service} emulator will also be started.
````

#### Azure Provisioning Configuration (if applicable)

For Azure resources:

```markdown
## Configure Azure Provisioning for local development

Adding Azure resources to the Aspire application model will automatically enable development-time provisioning
for Azure resources so that you don't need to configure them manually. Provisioning requires a number of settings
to be available via .NET configuration. The Aspire dashboard will prompt you to set these values if they are not already
configured. See [Local Azure Provisioning](https://aspire.dev/integrations/cloud/azure/local-provisioning/) for more details.

> NOTE: Developers must have Owner access to the target subscription so that role assignments
> can be configured for the provisioned resources.
```

### 5. Additional Documentation

```markdown
## Additional documentation

{Links to relevant Microsoft Learn documentation}
{Links to technology-specific documentation}
```

**Guidelines:**

- Include links to aspire.dev documentation for the component
- Include links to official technology documentation
- Use the format: `https://aspire.dev/...`
- For multiple links, use a bulleted list with `*` prefix (hosting READMEs) or separate lines (simpler hosting READMEs)

### 6. Feedback & Contributing

```markdown
## Feedback & contributing

https://github.com/microsoft/aspire
```

**Guidelines:**

- Always include this section at the end
- Use exactly this format with no additional text

### 7. Trademark Notices (if applicable)

For technologies with trademark requirements (e.g., Redis, PostgreSQL):

```markdown
_{Trademark notice text}_
```

**Guidelines:**

- Place trademark notices at the very end after "Feedback & contributing"
- Use markdown italic formatting.
- The trademark itself should be bolded using asterisks
- Common examples:
  - Redis: `_*Redis* is a registered trademark of Redis Ltd. Any rights therein are reserved to *Redis Ltd*._`
  - PostgreSQL: `_*Postgres*, *PostgreSQL* and the *Slonik Logo* are trademarks or registered trademarks of the *PostgreSQL Community Association of Canada*, and used with their permission._`

## Complete Example

Here's a complete example for a hosting integration:

````markdown
# Aspire.Hosting.PostgreSQL library

Provides extension methods and resource definitions for an Aspire AppHost to configure a PostgreSQL resource.

## Getting started

### Install the package

In your AppHost project, install the Aspire PostgreSQL Hosting library with [NuGet](https://www.nuget.org):

\```dotnetcli
dotnet add package Aspire.Hosting.PostgreSQL
\```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a PostgreSQL resource and consume the connection using the following methods:

\```csharp
var db = builder.AddPostgres("pgsql").AddDatabase("mydb");

var myService = builder.AddProject<Projects.MyService>()
.WithReference(db);
\```

## Additional documentation

https://aspire.dev/integrations/databases/postgres-client/
https://aspire.dev/integrations/databases/postgresql-extensions/

## Feedback & contributing

https://github.com/microsoft/aspire

_*Postgres*, *PostgreSQL* and the *Slonik Logo* are trademarks or registered trademarks of the *PostgreSQL Community Association of Canada*, and used with their permission._
````

## Key Principles

1. **Keep it simple**: Hosting READMEs should be concise and focused on the AppHost usage pattern
2. **Be consistent**: Use the same structure, phrasing, and formatting across all hosting integration READMEs
3. **Focus on the AppHost**: The primary audience is developers configuring their AppHost project
4. **Minimal examples**: Show the simplest working example; don't overwhelm with options
5. **Clear resource flow**: Demonstrate the pattern of adding a resource and referencing it in a project
6. **Link to detailed docs**: Use the "Additional documentation" section for deeper dive content

## Common Mistakes to Avoid

- ❌ Don't include detailed configuration options (these belong in client integration READMEs)
- ❌ Don't explain DI container registration (that's for client integrations)
- ❌ Don't include health check, telemetry, or observability details in hosting READMEs
- ❌ Don't use "component" or "package" in titles - always use "library"
- ❌ Don't omit the `WithReference` pattern in examples
- ❌ Don't forget trademark notices when applicable

## When to Update

Update hosting integration README.md files when:

- Adding new resource types or major extension methods
- Changing the primary usage pattern
- Adding emulator support
- Updating prerequisites or installation steps
- New Microsoft Learn documentation becomes available

## Review Checklist

When reviewing or creating a hosting integration README.md:

- [ ] Title follows the format: `# Aspire.Hosting.{Technology} library`
- [ ] Description starts with "Provides extension methods and resource definitions..."
- [ ] Installation section uses correct package name and `dotnetcli` code block
- [ ] Usage example shows `Add{Technology}` method with `WithReference` pattern
- [ ] Usage example uses appropriate variable names and resource names
- [ ] "Additional documentation" section includes relevant Microsoft Learn links
- [ ] "Feedback & contributing" section is present at the end
- [ ] Trademark notices are included if applicable
- [ ] No configuration details that belong in client integration READMEs
- [ ] Consistent formatting and style with other hosting integration READMEs
