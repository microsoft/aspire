# API Documentation and Code Samples

## 🔑 **API SOURCES**

### **API CHANGES DIFF** - Source of Truth for What's New:

```text
analysis-output/api-changes-build-current/api-changes-diff.txt
```

**START HERE** - This file contains **ONLY NEW/CHANGED APIs** between releases:
- **New APIs** added in this release
- **Modified API signatures** 
- **Breaking changes** and removals
- **Actual API diffs** showing what developers need to know

### **UBER FILE** - Complete API Reference for Usage Examples:

```text
analysis-output/api-changes-build-current/all-api-changes.txt
```

**Use for writing accurate code samples** - This file contains **ALL APIs** from the current build:
- **Complete API files** with method signatures for all integrations  
- **All integrations**: Azure services, hosting platforms, data services
- **Exact method signatures** including parameter names and types

## Writing Accurate Documentation

### Workflow for Documenting API Changes

#### Step 1: Start with API Changes Diff to Identify What's New

```bash
grep -A 5 -B 2 "AddFoundry" analysis-output/api-changes-build-current/api-changes-diff.txt
```

#### Step 2: Use Uber File Only for Writing Accurate Usage Examples  

```bash
grep -A 10 -B 2 "AddFoundry" analysis-output/api-changes-build-current/all-api-changes.txt
```

#### Step 3: Extract Complete API Signatures for Code Samples

```csharp
// From uber file - get complete API signature for accurate usage examples: 
public static ApplicationModel.IResourceBuilder<Azure.FoundryResource> AddFoundry(this IDistributedApplicationBuilder builder, string name)
public static ApplicationModel.IResourceBuilder<Azure.FoundryDeploymentResource> AddDeployment(this ApplicationModel.IResourceBuilder<Azure.FoundryResource> builder, string name, string modelName, string modelVersion, string format)
```

#### Step 4: Write Usage Example with Correct API Signatures

```csharp
// ✅ CORRECT: Usage example based on actual API signatures from uber file
var aiFoundry = builder.AddFoundry("ai-foundry");
var deployment = aiFoundry.AddDeployment("gpt-4", "gpt-4", "1106", "OpenAI");
```

## Do Not Invent APIs

```csharp
// ❌ WRONG: These methods are NOT found in the API diff or uber file
builder.AddFoundry("ai")
    .WithModel("gpt-4", modelId: "gpt-4-1106")      // INVENTED - not in uber file
    .WithEndpoint("chat", deployment: "chat-latest") // INVENTED - not in uber file
    .WithRoleAssignments(roles: KeyVaultBuiltInRole.SecretsUser) // INVENTED - not in uber file
```

## Builder Context Matters

**IDistributedApplicationBuilder vs IHostApplicationBuilder**: Pay attention to which builder interface the extension methods target:

### **`IDistributedApplicationBuilder`** - Extension methods for app model resources and hosting

```csharp
var builder = DistributedApplication.CreateBuilder(args);  // Returns IDistributedApplicationBuilder
builder.AddAzureStorage("storage");     // ✅ Extension method on IDistributedApplicationBuilder
builder.AddProject<Projects.Api>("api"); // ✅ Extension method on IDistributedApplicationBuilder
```

### **`IHostApplicationBuilder`** - Extension methods for service registration

```csharp
var builder = WebApplication.CreateBuilder(args);  // Returns WebApplicationBuilder : IHostApplicationBuilder
builder.AddAzureTableServiceClient("tables");     // ✅ Extension method on IHostApplicationBuilder
builder.AddKeyedAzureBlobServiceClient("blobs");  // ✅ Extension method on IHostApplicationBuilder
```

### **❌ Common Mistake**: Using service registration extension methods on `IDistributedApplicationBuilder`:

```csharp
// ❌ WRONG: AddAzureTableServiceClient is an extension method on IHostApplicationBuilder registration, not app hosting
var builder = DistributedApplication.CreateBuilder(args);
builder.AddAzureTableServiceClient("tables");  // Does not exist on IDistributedApplicationBuilder
```

## CLI Changes Source

For CLI-related content, use the commit analysis from:

```text
analysis-output/Aspire.Cli.md
```

**Verified CLI changes from commits:**

- `aspire exec` command (feature-flagged, preview)
- `aspire deploy` command (feature-flagged, preview)
- `aspire config` with dot notation support
- Enhanced `aspire new`, `aspire add`, `aspire run`, `aspire publish`
- Health column added to `aspire run`
- Localization support added

### ❌ DO NOT document CLI commands not found in commit analysis

## Documentation Linking Guidelines

### Find Related Documentation

- Using the MicrosoftDocs MCP, search for existing documentation about the feature, starting with Aspire documentation.
- Documentation can be referenced in multiple ways. If the doc is part of the aspire docset (learn.microsoft.com/microsoft/aspire/*) you can use a relative path (assume What's New is one level - ie ../ - below "root" microsoft/aspire/)
- If the docset is on learn.microsoft.com, but not from Aspire, you can use an xref in the link path - for example, something under Azure docs (learn.microsoft.com/azure/ai-foundry/overview) would be [Microsoft Foundry documentation](xref:/azure/ai-foundry/overview)
- If a new API is explicitly called out, use an xref to the API docs via the fully qualified API namespace. For example, `Aspire.Hosting.ApplicationModel.BeforeStartEvent` or simply `BeforeStartEvent` becomes <xref:aspire.hosting.applicationmodel.beforestartevent>
- Do NOT put links to docs or APIs in the sample code. Only put them in the brief description above to set context, or the explanation for follow up content and API docs.
- Some conceptual docs may not exist yet. If you are unsure of if a link's content fits, or if something is relevant, do not add a link at all.

## Validation Process

Before writing any API samples:

1. **Check API changes diff first** to identify what's actually new:

   ```bash
   grep -n "MethodName" analysis-output/api-changes-build-current/api-changes-diff.txt
   ```

2. **Get complete API details** from uber file for accurate samples:

   ```bash
   grep -n "MethodName" analysis-output/api-changes-build-current/all-api-changes.txt
   ```

3. **Verify CLI commands** against Aspire.Cli.md commit analysis
4. **Cross-reference commit analysis** to understand the context of changes

## API Documentation Rules

### Core Rules

1. **Always start with API changes diff** to identify what's new in this release
2. **Only use uber file for writing usage examples** with correct API signatures
3. **Match parameter names and types exactly** as shown in uber file
4. **Show migration steps for breaking changes** found in diffs
5. **Never invent or assume APIs exist** - verify everything starts in the diff

### CLI Documentation Rules

1. **Every CLI change must show the actual command syntax**
2. **Use commit analysis from `Aspire.Cli.md` for verified CLI changes**
3. **Include examples of command usage and output where applicable**
4. **Document any new flags, options, or command behaviors**

### Accuracy Standards

- **🔍 START WITH DIFF**: Check api-changes-diff.txt first to identify what's actually new
- **✅ USE UBER FILE FOR SAMPLES**: Get complete API signatures from all-api-changes.txt  
- **💻 PROVIDE SAMPLES FOR API CHANGES**: Every new API should include a code sample
- **⚡ INCLUDE CLI COMMANDS**: Every CLI change should show the actual command syntax
- **❌ NEVER INVENT APIS**: No made-up methods, parameters, or fluent chains

## Traceability References

Store traceability references for each documented change:

- **Commit SHA or message** from component analysis
- **GitHub Issue ID** (if referenced in commit message)
- **GitHub Pull Request number** (if available)
- **Component name** where the change was found

This enables verification and backtracking to actual source changes. Example format:

```
Feature: Dashboard telemetry navigation improvements
Source: commit "Add telemetry peer navigation" in Aspire.Dashboard
GitHub PR: #10648
GitHub Issue: #10645 (if referenced)
```
