---
applyTo: "src/Aspire.Hosting*/README.md"
---

# README.md Instructions for Hosting Integration READMEs

This document provides guidelines for writing and maintaining README.md files for Aspire hosting integrations located under `src/Aspire.Hosting*/README.md`.

## Purpose

Hosting integrations model infrastructure resources (databases, message queues, caches, cloud services, and more) in an Aspire AppHost. The README.md files help developers add and configure those resources from an AppHost written in C# or TypeScript.

## Standard Structure

All hosting integration README.md files should follow this structure:

### 1. Title and Description

```markdown
# {Technology} hosting integration

Use this integration to model, configure, and orchestrate {a/an} {Technology} {resource type} in an Aspire solution.
```

**Guidelines:**

- Title format: `# {Technology} hosting integration`
- Do not use "library", "package", or "component" in the title
- Start description with "Use this integration to..."
- Be specific about what type of resource is being configured (e.g., "a SQL Server database resource", "a MongoDB resource", "Azure CosmosDB")

### 2. Getting Started Section

````markdown
## Getting started

### Prerequisites

{List any prerequisites such as Azure subscription, if applicable}

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.{Technology}` integration with the Aspire CLI:

\```bash
aspire add Aspire.Hosting.{Technology}
\```
````

**Guidelines:**

- Include a "Prerequisites" subsection only if there are specific requirements (e.g., Azure subscription for Azure resources)
- Installation command should be in a `bash` code block
- Use consistent phrasing: "From your AppHost directory, add the `Aspire.Hosting.{Technology}` integration with the Aspire CLI:"
- Use the exact integration ID with `aspire add` so the command works without relying on fuzzy matching or interactive selection

### 3. Usage Example

````markdown
## Usage example

Then, in the AppHost, add {a/an} {Technology} resource and reference it from another resource with either C# or TypeScript:

**C#**

\```csharp
var {resourceName} = builder.Add{Technology}("{name}"){.AddDatabase("dbname") if applicable};

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference({resourceName});
\```

**TypeScript**

\```typescript
const {resourceName} = await builder.add{Technology}("{name}"){.addDatabase("dbname") if applicable};

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
    .withReference({resourceName});
\```
````

**Guidelines:**

- Start with "Then, in the AppHost, add..."
- Show the minimal working example
- Use descriptive variable names that match the technology (e.g., `redis`, `postgres`, `sql`, `mongodb`)
- Include chained methods like `.AddDatabase()` when applicable
- Show the `WithReference` pattern to demonstrate resource references
- Include both C# and TypeScript AppHost samples when the APIs are exported for TypeScript. Do not invent TypeScript examples for advanced or C#-only APIs.
- Keep examples simple and focused on the most common use case

### 4. Additional Sections (Optional)

#### Emulator Usage (if applicable)

For Azure services that support emulators:

````markdown
### Emulator usage

Aspire supports the usage of the {Azure Service} emulator. To use the emulator, add the following to your AppHost:

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
to be available via AppHost configuration. From your AppHost directory, set these values with `aspire secret set`:

\```bash
aspire secret set Azure:SubscriptionId "<your subscription id>"
aspire secret set Azure:ResourceGroupPrefix "<prefix for the resource group>"
aspire secret set Azure:Location "<azure location>"
\```

See [Local Azure Provisioning](https://aspire.dev/integrations/cloud/azure/local-provisioning/) for more details.

> NOTE: Developers must have Owner access to the target subscription so that role assignments
> can be configured for the provisioned resources.
```

### 5. Additional Documentation

```markdown
## Additional documentation

https://aspire.dev/integrations/gallery/
{Specific hosting integration docs URL from aspire docs search, when available}
{Links to relevant Microsoft Learn documentation}
{Links to technology-specific documentation}
```

**Guidelines:**

- Include links to relevant aspire.dev documentation
- Include `https://aspire.dev/integrations/gallery/`
- Use `aspire docs search "{Technology}"` to find and include the specific hosting integration docs page when one exists
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
# PostgreSQL hosting integration

Use this integration to model, configure, and orchestrate a PostgreSQL resource in an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.PostgreSQL` integration with the Aspire CLI:

\```bash
aspire add Aspire.Hosting.PostgreSQL
\```

## Usage example

Then, in the AppHost, add a PostgreSQL resource and reference it from another resource with either C# or TypeScript:

**C#**

\```csharp
var db = builder.AddPostgres("pgsql").AddDatabase("mydb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(db);
\```

**TypeScript**

\```typescript
const db = await builder.addPostgres("pgsql").addDatabase("mydb");

const myService = await builder.addNodeApp("myService", "../my-service", "server.js")
    .withReference(db);
\```

## Additional documentation

https://aspire.dev/integrations/gallery/
https://aspire.dev/integrations/databases/postgres/postgres-host/
https://www.postgresql.org/docs/

## Feedback & contributing

https://github.com/microsoft/aspire

_*Postgres*, *PostgreSQL* and the *Slonik Logo* are trademarks or registered trademarks of the *PostgreSQL Community Association of Canada*, and used with their permission._
````

## Key Principles

1. **Keep it simple**: Hosting READMEs should be concise and focused on the AppHost usage pattern
2. **Be consistent**: Use the same structure, phrasing, and formatting across all hosting integration READMEs
3. **Focus on the AppHost**: The primary audience is developers configuring their AppHost model
4. **Minimal examples**: Show the simplest working example; don't overwhelm with options
5. **Clear resource flow**: Demonstrate the pattern of adding a resource and referencing it from another resource
6. **Link to detailed docs**: Use the "Additional documentation" section for deeper dive content

## Common Mistakes to Avoid

- ❌ Don't include consuming-app setup; hosting READMEs should focus on the AppHost resource model
- ❌ Don't explain dependency-injection registration; that belongs in consuming-app docs
- ❌ Don't include health check, telemetry, or observability details in hosting READMEs
- ❌ Don't use "library", "package", or "component" in titles
- ❌ Don't omit the `WithReference` pattern in examples
- ❌ Don't forget trademark notices when applicable

## When to Update

Update hosting integration README.md files when:

- Adding new resource types or major extension methods
- Changing the primary usage pattern
- Adding emulator support
- Updating prerequisites or installation steps
- New aspire.dev documentation becomes available

## Review Checklist

When reviewing or creating a hosting integration README.md:

- [ ] Title follows the format: `# {Technology} hosting integration`
- [ ] Description starts with "Use this integration to..."
- [ ] Installation section uses `aspire add` with the correct integration ID in a `bash` code block
- [ ] Usage example shows `Add{Technology}` method with `WithReference` pattern
- [ ] Usage example includes C# and TypeScript AppHost samples when the API is exported for TypeScript
- [ ] Usage example uses appropriate variable names and resource names
- [ ] "Additional documentation" section includes `https://aspire.dev/integrations/gallery/`
- [ ] "Additional documentation" section links to the specific hosting integration docs page found with `aspire docs search` when one exists
- [ ] "Feedback & contributing" section is present at the end
- [ ] Trademark notices are included if applicable
- [ ] No consuming-app setup or dependency-injection details
- [ ] Consistent formatting and style with other hosting integration READMEs
