---
applyTo: "src/**/*.cs"
---

# XML Documentation Standards for Aspire

This document provides comprehensive guidelines for writing high-quality XML documentation comments in the Aspire repository.

**Note:** This guide applies to all C# source files in the `src/` directory. API files under `src/*/api/*.cs` are auto-generated and should not be manually edited.

## Purpose

XML documentation comments serve multiple purposes:

- Generate IntelliSense tooltips in IDEs for developers using Aspire APIs
- Create public API documentation published to aspire.dev
- Improve code maintainability and understanding
- Support automated documentation generation tools

## General Principles

### Scope: Public vs Internal APIs

**Public APIs require comprehensive documentation:**

- All public classes, interfaces, methods, properties, and events must be well-documented
- Include detailed `<summary>`, `<remarks>`, `<example>`, and other appropriate tags
- Focus on explaining purpose, usage patterns, and providing practical examples
- This documentation will be published to aspire.dev and shown in IntelliSense

**Internal APIs require minimal documentation:**

- Internal classes and members should have brief `<summary>` tags only
- Avoid verbose `<remarks>`, `<example>`, or detailed parameter descriptions for internal APIs
- Keep internal documentation concise and focused on what the code does
- Internal documentation is for maintainers, not public consumption

### Quality Standards

High-quality XML documentation should:

1. **Be Complete**: Document all public APIs (classes, interfaces, methods, properties, events)
2. **Be Concise for Internal APIs**: Internal types should have minimal documentation
3. **Be Clear**: Use plain language that developers can understand
4. **Be Accurate**: Ensure documentation matches the actual behavior of the code
5. **Provide Context**: Explain why something exists and how it should be used (public APIs only)
6. **Include Examples**: Show practical usage when appropriate (public APIs only)
7. **Reference Related APIs**: Link to related types and members using `<see cref=""/>` and `<seealso cref=""/>`

### What Makes Good Documentation

**Good documentation:**

- Explains the purpose and use case
- Describes behavior, not implementation
- Includes examples for complex scenarios
- Documents exceptions that can be thrown
- Provides migration guidance for deprecated APIs
- Uses appropriate tags (`<remarks>`, `<example>`, `<param>`, `<returns>`)

**Poor documentation:**

- Simply restates the member name ("Gets or sets the name")
- Lacks context about when/why to use the API
- Missing parameter descriptions
- No examples for complex APIs
- Inconsistent terminology

## XML Documentation Tags

### Required Tags

#### `<summary>`

Brief description of the type or member. Should be a single sentence or short paragraph.

```csharp
/// <summary>
/// Represents an Azure Cosmos DB resource with NoSQL API support.
/// </summary>
```

#### `<param>`

Describes each parameter. Required for all public method/constructor parameters.

```csharp
/// <param name="builder">The distributed application builder.</param>
/// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
```

#### `<returns>`

Describes the return value. Required for all public methods that return a value (except `void`).

```csharp
/// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
```

### Recommended Tags

#### `<remarks>`

Additional details, usage notes, or important information that doesn't fit in the summary.

```csharp
/// <remarks>
/// This method is typically used when testing Aspire applications where the original resource builder cannot be
/// referenced directly. Using this method allows for easier mutation of resources within the test scenario.
/// </remarks>
```

#### `<example>`

Code examples showing how to use the API. Include for complex or frequently-used APIs.

```csharp
/// <example>
/// This example adds an Azure Cosmos DB resource with a database:
/// <code>
/// var cosmos = builder.AddAzureCosmosDB("cosmos");
/// var database = cosmos.AddDatabase("mydb");
/// </code>
/// </example>
```

#### `<exception>`

Documents exceptions that can be thrown. Include for all public methods that throw exceptions.

```csharp
/// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
/// <exception cref="InvalidOperationException">Thrown when the resource with the specified name is not found.</exception>
```

#### `<value>`

Describes the meaning of a property value. Use for properties where the summary isn't sufficient.

```csharp
/// <value>
/// A dictionary where the key is the name of the service (as a string), and the value
/// is a <see cref="Service"/> object containing the configuration of the service.
/// </value>
```

#### `<seealso>`

Links to related APIs or documentation.

```csharp
/// <seealso cref="AddAzureCosmosDB"/>
/// <seealso href="https://learn.microsoft.com/azure/cosmos-db/">Azure Cosmos DB documentation</seealso>
```

#### `<list>`

Creates lists in XML documentation. Use `type="bullet"` for bulleted lists, `type="number"` for numbered lists, or `type="table"` for tables.

**Bulleted list:**

```csharp
/// <summary>
/// Supports multiple environments:
/// <list type="bullet">
/// <item>Development environment</item>
/// <item>Staging environment</item>
/// <item>Production environment</item>
/// </list>
/// </summary>
```

**Numbered list:**

```csharp
/// <summary>
/// Follow these steps:
/// <list type="number">
/// <item>Initialize the resource</item>
/// <item>Configure the settings</item>
/// <item>Start the resource</item>
/// </list>
/// </summary>
```

**List with descriptions:**

```csharp
/// <summary>
/// Configuration options:
/// <list type="bullet">
/// <item>
/// <term>Development</term>
/// <description>Uses local emulator with debug logging enabled</description>
/// </item>
/// <item>
/// <term>Production</term>
/// <description>Connects to Azure with optimized performance settings</description>
/// </item>
/// </list>
/// </summary>
```

**Important:** Do not use markdown-style lists with hyphens (`-`) or asterisks (`*`) in XML documentation, as they will not render correctly. Always use the `<list>` tag with appropriate `<item>` elements.

## Templates by Construct Type

### Classes

```csharp
/// <summary>
/// Represents [what this class models/encapsulates].
/// </summary>
/// <remarks>
/// [Additional context about when/how to use this class]
/// [Important behavioral notes]
/// </remarks>
public class ResourceName
{
}
```

**Example:**

```csharp
/// <summary>
/// Represents an Azure Cosmos DB resource with NoSQL API support.
/// </summary>
/// <remarks>
/// This resource can be configured to run as the Azure Cosmos DB emulator for local development
/// or as a provisioned Azure resource for production scenarios.
/// </remarks>
public class AzureCosmosDBResource
{
}
```

### Interfaces

```csharp
/// <summary>
/// Defines [the contract/behavior this interface requires].
/// </summary>
/// <remarks>
/// Implement this interface to [purpose/requirement].
/// </remarks>
public interface IInterfaceName
{
}
```

**Example:**

```csharp
/// <summary>
/// Defines a builder for generating YARP JSON configuration files.
/// </summary>
/// <remarks>
/// Implementations of this interface provide methods to add routes, clusters, and configuration files
/// to a YARP reverse proxy resource.
/// </remarks>
public interface IYarpJsonConfigGeneratorBuilder
{
}
```

### Methods

```csharp
/// <summary>
/// [Action this method performs] [on/for what].
/// </summary>
/// <param name="paramName">Description of the parameter.</param>
/// <returns>Description of what is returned.</returns>
/// <exception cref="ExceptionType">When this exception is thrown.</exception>
/// <remarks>
/// [Additional behavioral details]
/// [When to use this method]
/// </remarks>
/// <example>
/// <code>
/// // Practical usage example
/// </code>
/// </example>
public ReturnType MethodName(Type paramName)
{
}
```

**Example:**

```csharp
/// <summary>
/// Adds an Azure Cosmos DB connection to the application model.
/// </summary>
/// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
/// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
/// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
/// <remarks>
/// This method registers an Azure Cosmos DB resource in the distributed application.
/// Use <see cref="RunAsEmulator"/> to run the resource as the Azure Cosmos DB emulator for local development.
/// </remarks>
/// <example>
/// Add an Azure Cosmos DB resource:
/// <code>
/// var builder = DistributedApplication.CreateBuilder(args);
/// var cosmos = builder.AddAzureCosmosDB("cosmos");
/// </code>
/// </example>
public static IResourceBuilder<AzureCosmosDBResource> AddAzureCosmosDB(
    this IDistributedApplicationBuilder builder,
    string name)
{
}
```

### Properties

```csharp
/// <summary>
/// Gets [or sets] [what this property represents].
/// </summary>
/// <value>
/// [Detailed description of valid values and their meaning, if needed]
/// </value>
/// <remarks>
/// [Additional context about when/how this property is used]
/// </remarks>
public Type PropertyName { get; set; }
```

**Example:**

```csharp
/// <summary>
/// Gets the primary gRPC endpoint for the Milvus database.
/// </summary>
/// <value>
/// An <see cref="EndpointReference"/> pointing to the Milvus gRPC service endpoint.
/// </value>
/// <remarks>
/// This endpoint is used for all Milvus database operations including vector storage and retrieval.
/// </remarks>
public EndpointReference PrimaryEndpoint { get; }
```

### Constructors

```csharp
/// <summary>
/// Initializes a new instance of the <see cref="ClassName"/> class.
/// </summary>
/// <param name="paramName">Description of parameter.</param>
/// <exception cref="ExceptionType">When this exception is thrown.</exception>
public ClassName(Type paramName)
{
}
```

**Example:**

```csharp
/// <summary>
/// Initializes a new instance of the <see cref="MilvusServerResource"/> class.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="apiKey">A <see cref="ParameterResource"/> that contains the authentication API key/token.</param>
/// <exception cref="ArgumentNullException">Thrown when <paramref name="apiKey"/> is null.</exception>
public MilvusServerResource(string name, ParameterResource apiKey) : base(name)
{
}
```

### Enums

```csharp
/// <summary>
/// Specifies [what this enum represents/controls].
/// </summary>
public enum EnumName
{
    /// <summary>
    /// [Description of this enum value and when to use it].
    /// </summary>
    Value1,

    /// <summary>
    /// [Description of this enum value and when to use it].
    /// </summary>
    Value2
}
```

### Events

```csharp
/// <summary>
/// Occurs when [condition that triggers the event].
/// </summary>
/// <remarks>
/// [Additional context about event handling]
/// </remarks>
public event EventHandler? EventName;
```

## Best Practices

### DO

✅ **Use consistent terminology** throughout the codebase

- "distributed application" not "app model"
- "resource" not "service" (unless specifically referring to a service)
- "endpoint" not "URL" or "address"

✅ **Start with a verb for methods**

- "Adds an Azure Cosmos DB resource..."
- "Configures the emulator..."
- "Gets the connection string..."

✅ **Use present tense for properties**

- "Gets the endpoint..." not "Will get the endpoint..."
- "Represents the configuration..." not "Will represent..."

✅ **Include the `<paramref>` tag when referring to parameters in text**

```csharp
/// <param name="name">The name of the resource.</param>
/// <remarks>
/// The <paramref name="name"/> must be unique within the application model.
/// </remarks>
```

✅ **Use `<see cref="">` for API references**

```csharp
/// <summary>
/// Extensions for <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
```

✅ **Provide examples for extension methods and fluent APIs**

```csharp
/// <example>
/// Configure a Redis resource with persistence:
/// <code>
/// var redis = builder.AddRedis("cache")
///     .WithDataVolume()
///     .WithPersistence();
/// </code>
/// </example>
```

✅ **Document default values**

```csharp
/// <param name="timeout">The timeout in seconds. Defaults to 30 seconds if not specified.</param>
```

✅ **Explain null behavior**

```csharp
/// <returns>
/// The database resource if found; otherwise, <c>null</c>.
/// </returns>
```

### DON'T

❌ **Don't just restate the member name**

```csharp
// BAD
/// <summary>
/// Gets or sets the name.
/// </summary>
public string Name { get; set; }

// GOOD
/// <summary>
/// Gets or sets the name of the resource as it appears in the distributed application model.
/// </summary>
/// <remarks>
/// This name is used as the connection string name when the resource is referenced in a dependency.
/// </remarks>
public string Name { get; set; }
```

❌ **Don't use implementation details in documentation**

```csharp
// BAD
/// <summary>
/// Adds the resource to the internal dictionary using the name as key.
/// </summary>

// GOOD
/// <summary>
/// Registers the resource in the distributed application model.
/// </summary>
```

❌ **Don't leave placeholder or TODO comments**

```csharp
// BAD
/// <summary>
/// TODO: Add documentation
/// </summary>
```

❌ **Don't use abbreviations without explanation**

```csharp
// BAD
/// <summary>
/// Gets the DB conn str.
/// </summary>

// GOOD
/// <summary>
/// Gets the connection string for the database.
/// </summary>
```

❌ **Don't use markdown-style lists in XML documentation**

```csharp
// BAD - Markdown lists don't render in XML docs
/// <summary>
/// Supports:
/// - Option 1
/// - Option 2
/// - Option 3
/// </summary>

// GOOD - Use XML list tags
/// <summary>
/// Supports:
/// <list type="bullet">
/// <item>Option 1</item>
/// <item>Option 2</item>
/// <item>Option 3</item>
/// </list>
/// </summary>
```

❌ **Don't add verbose documentation to internal APIs**

```csharp
// BAD - Too verbose for an internal class
/// <summary>
/// Provides utilities for managing virtual environments.
/// </summary>
/// <remarks>
/// <para>
/// This class handles platform-specific directory structures...
/// </para>
/// <para>
/// Used internally by the Python hosting infrastructure...
/// </para>
/// </remarks>
internal class VirtualEnvironment

// GOOD - Brief and concise for internal class
/// <summary>
/// Handles location of files within the virtual environment.
/// </summary>
internal class VirtualEnvironment
```

❌ **Don't include HTML tags** (except `<c>`, `<code>`, `<para>` which are standard XML doc tags)

## Documentation for Internal APIs

Internal classes, methods, and properties should have minimal documentation:

**DO:**

- ✅ Provide brief `<summary>` tags that explain what the code does
- ✅ Document parameters and return values concisely
- ✅ Keep it short and to the point

**DON'T:**

- ❌ Add verbose `<remarks>` sections
- ❌ Include `<example>` sections
- ❌ Write detailed explanations of usage patterns
- ❌ Add extensive parameter descriptions

**Example of good internal API documentation:**

```csharp
/// <summary>
/// Handles location of files within the virtual environment of a python app.
/// </summary>
/// <param name="virtualEnvironmentPath">The path to the virtual environment directory.</param>
internal sealed class VirtualEnvironment(string virtualEnvironmentPath)
{
    /// <summary>
    /// Locates an executable in the virtual environment.
    /// </summary>
    /// <param name="name">The name of the executable.</param>
    /// <returns>The path to the executable.</returns>
    public string GetExecutable(string name)
    {
        // Implementation...
    }
}
```

## Special Cases

### Generic Type Parameters

```csharp
/// <typeparam name="T">The type of resource.</typeparam>
```

### Obsolete APIs

```csharp
/// <summary>
/// Gets the connection string.
/// </summary>
/// <remarks>
/// This property is obsolete. Use <see cref="ConnectionStringExpression"/> instead.
/// </remarks>
[Obsolete("Use ConnectionStringExpression instead.")]
public string ConnectionString { get; }
```

### Experimental APIs

```csharp
/// <summary>
/// Configures the Azure Cosmos DB resource to use the Linux-based emulator (preview).
/// </summary>
/// <remarks>
/// This is an experimental feature and may change in future releases.
/// For more information, see <a href="https://learn.microsoft.com/azure/cosmos-db/emulator-linux">Azure Cosmos DB Linux Emulator</a>.
/// </remarks>
[Experimental("ASPIRECOSMOSDB001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static IResourceBuilder<AzureCosmosDBResource> RunAsPreviewEmulator(...)
{
}
```

### Extension Methods

```csharp
/// <summary>
/// Adds an Azure Cosmos DB resource to the application model.
/// </summary>
/// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
/// <param name="name">The name of the resource.</param>
/// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining additional configuration.</returns>
public static IResourceBuilder<AzureCosmosDBResource> AddAzureCosmosDB(
    this IDistributedApplicationBuilder builder,
    string name)
{
}
```

## Common Patterns in Aspire

### Resource Classes

Resource classes represent infrastructure components (databases, message queues, etc.).

```csharp
/// <summary>
/// Represents a [technology name] [resource type] in the distributed application model.
/// </summary>
/// <remarks>
/// [Key characteristics of this resource]
/// [When/how it's typically used]
/// </remarks>
/// <param name="name">The name of the resource.</param>
public class ResourceName(string name) : BaseResourceType(name)
{
}
```

### Extension Methods - Add\* Pattern

```csharp
/// <summary>
/// Adds a [resource description] to the application model.
/// </summary>
/// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
/// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
/// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
public static IResourceBuilder<ResourceType> AddResource(
    this IDistributedApplicationBuilder builder,
    string name)
{
}
```

### Extension Methods - With\* Pattern (Configuration)

```csharp
/// <summary>
/// Configures the [resource] to [behavior/feature being enabled].
/// </summary>
/// <param name="builder">The resource builder.</param>
/// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining additional configuration.</returns>
/// <remarks>
/// [Important details about this configuration]
/// [When to use it]
/// </remarks>
public static IResourceBuilder<ResourceType> WithFeature(
    this IResourceBuilder<ResourceType> builder)
{
}
```

### Extension Methods - RunAs\* Pattern (Emulators)

```csharp
/// <summary>
/// Configures the [Azure service] to be emulated using the [emulator name].
/// </summary>
/// <param name="builder">The resource builder.</param>
/// <param name="configureContainer">Optional callback to customize the emulator container.</param>
/// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
/// <remarks>
/// For local development, this configures the resource to use the [emulator name] instead of a cloud-hosted service.
/// For more information, see <a href="[documentation URL]">[emulator name] documentation</a>.
/// This version uses the [image tag] container image.
/// </remarks>
public static IResourceBuilder<ResourceType> RunAsEmulator(
    this IResourceBuilder<ResourceType> builder,
    Action<IResourceBuilder<EmulatorResourceType>>? configureContainer = null)
{
}
```

## Validation

Before committing code with XML documentation:

1. **Build the project** - XML doc warnings should be treated as errors
2. **Check IntelliSense** - Hover over APIs in an IDE to see how the documentation appears
3. **Review consistency** - Ensure terminology and style match existing documentation
4. **Spell check** - Use a spell checker on documentation comments
5. **Run API documentation tools** - If available, generate HTML docs to review formatting

## Resources

- [C# XML Documentation Comments (Microsoft Docs)](https://learn.microsoft.com/dotnet/csharp/language-reference/xmldoc/)
- [Recommended XML Tags (Microsoft Docs)](https://learn.microsoft.com/dotnet/csharp/language-reference/xmldoc/recommended-tags)
- [.NET API Documentation Guidelines](https://github.com/dotnet/dotnet-api-docs/wiki)

## Summary

High-quality XML documentation is essential for the Aspire project. Follow these guidelines to ensure that all public APIs are well-documented, making it easier for developers to understand and use Aspire effectively.
