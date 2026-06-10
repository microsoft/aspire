# Writing Guidelines and Template Structure

## Template Structure Requirements

Follow the established structure from `data/whats-new-*.md` files:

1. **Frontmatter** (YAML header with title, description, ms.date)
2. **Main Title** (`# What's new in Aspire {version}`)
3. **Introduction** with supported .NET versions and feedback links
4. **Version Support Policy** information
5. **Major Sections** with emoji headers (🖥️, ✨, 🔗, 🚀, etc.)
6. **Code Examples** with proper syntax highlighting
7. **Breaking Changes** section (if applicable)
8. **Migration Guidance** for breaking changes

### Example Template Structure

```markdown
---
title: What's new in Aspire {version}
description: Learn what's new in Aspire {version}.
ms.date: {date}
---

# What's new in Aspire {version}

📢 Aspire {version} is the next [major|minor] version release...

## 🖥️ App model enhancements
### ✨ Feature Name
[Description and code samples]

## 🚀 CLI improvements
[CLI commands and examples]
```

## Core Documentation Rules

### 1. **Comprehensive Analysis**

- Review ALL component analysis files in `analysis-output/`
- Examine ALL commits in each file to understand the full scope of changes
- Use `api-changes-summary.md` to identify new API additions and changes
- Summarize major features and themes across components

### 2. **API Changes with Samples**

- Every API change must include a code sample
- Search uber file before writing ANY code sample
- Match parameter names and types exactly as shown
- Show migration steps for breaking changes found in diffs
- Never invent or assume APIs exist

### 3. **CLI Changes with Commands**

- Every CLI change must show the actual command syntax
- Use commit analysis from `Aspire.Cli.md` for verified CLI changes
- Include examples of command usage and output where applicable
- Document any new flags, options, or command behaviors

### 4. **Style and Structure**

- **Follow templates in `data/` directory** (e.g., `data/whats-new-91.md`, `data/whats-new-92.md`, `data/whats-new-93.md`)
- **Maintain consistent document structure**: frontmatter, intro, major sections with emojis
- Use active voice and developer-focused language
- Format code elements in backticks: `IDistributedApplicationBuilder`, `AddRedis()`
- If you reference an API for the first time link, or it is a new API, link to its docs with an inline xref like <xref:Fully.Qualified.Api.Namespace>
- Organize by impact: breaking changes, major features, enhancements
- Include emojis consistently per template (🖥️, ✨, 🔗, 🚀, etc.)

## Writing Style Guidelines

### Language and Tone

- **Use active voice**: "You can now deploy containers" instead of "Containers can now be deployed"
- **Developer-focused**: Write for the developer audience with practical examples
- **Clear and concise**: Avoid unnecessary jargon or verbose explanations
- **Action-oriented**: Focus on what developers can DO with the new features

### Code Formatting

- **Use proper syntax highlighting**: Always specify the language (```csharp, ```bash, ```json)
- **Include complete examples**: Show full, working code samples when possible
- **Highlight new features**: Use comments like `// ✨ New capability` to call out changes
- **Maintain consistency**: Use the same variable names and patterns throughout

### Section Organization

- **Start with most impactful changes**: Breaking changes first, then major features
- **Group related features**: Combine similar functionality into unified sections
- **Use descriptive headings**: Make it easy to scan and find specific improvements
- **Include migration guidance**: Always show how to update existing code for breaking changes

## Emoji Usage Guidelines

Follow the established emoji patterns from existing templates:

- **🖥️** - App model enhancements, hosting changes
- **✨** - New features and capabilities
- **🔗** - Integrations and connections
- **🚀** - CLI improvements and developer experience
- **🐳** - Container and Docker-related features
- **☁️** - Azure and cloud integrations
- **⚠️** - Breaking changes and important notices
- **🔧** - Configuration and setup improvements

## Example Feature Documentation

### Complete Example: New Feature

```markdown
### ✨ Microsoft Foundry integration

Aspire now provides first-class support for Microsoft Foundry, enabling you to easily integrate AI models and deployments into your distributed applications.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Add Microsoft Foundry resource
var aiFoundry = builder.AddFoundry("ai-foundry");

// Add model deployment
var gptDeployment = aiFoundry.AddDeployment("gpt-4", "gpt-4", "1106", "OpenAI");

// Reference in your projects
builder.AddProject<Projects.ChatApi>("chat-api")
    .WithReference(gptDeployment);

await builder.Build().RunAsync();
```

This integration streamlines AI model deployment and management, allowing you to focus on building intelligent applications rather than infrastructure setup. Learn more about [Microsoft Foundry integration](../azure/azure-ai-foundry.md).
```

### Complete Example: Breaking Change

```markdown
### ⚠️ Azure Storage API consolidation

The Azure Storage APIs have been consolidated for better consistency across blob, table, and queue services.

**Before (Aspire 9.0)**:
```csharp
// Old inconsistent naming
builder.AddAzureBlobStorage("storage");
builder.AddAzureDataTables("tables"); 
```

**After (Aspire 9.1)**:
```csharp
// New consistent naming
builder.AddAzureStorage("storage");
builder.AddAzureStorage("storage").AddBlobs("blobs");
builder.AddAzureStorage("storage").AddTables("tables");
```

**Migration Guide**:
1. Update all `AddAzureBlobStorage` calls to `AddAzureStorage().AddBlobs()`
2. Update all `AddAzureDataTables` calls to `AddAzureStorage().AddTables()`
3. Verify resource names remain consistent across your application

This change provides a more intuitive and consistent API surface for Azure Storage services.
```

## Quality Checklist

Before finalizing documentation:

- [ ] **All code samples verified** against uber file
- [ ] **All CLI commands verified** against commit analysis
- [ ] **Proper emoji usage** following established patterns
- [ ] **Complete migration guidance** for breaking changes
- [ ] **Consistent formatting** with existing templates
- [ ] **Clear, developer-focused language** throughout
- [ ] **Appropriate documentation links** (relative paths, xrefs)
- [ ] **No invented APIs or commands**
- [ ] **Logical organization** by impact and functionality
