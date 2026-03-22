# ATS (Aspire Type System) Attributes for Third-Party Integrations

The ATS capability scanner discovers attributes by their **full type name**, not by concrete type reference. This means third-party integration authors can define their own copies of the ATS attributes without taking a package reference to `Aspire.Hosting`, but the copied attributes must use the same namespace and type names as the built-in ones.

## How It Works

The scanner looks for attributes with these exact full type names:

| Full Type Name                            | Purpose                                                  |
|-------------------------------------------|----------------------------------------------------------|
| `Aspire.Hosting.AspireExportAttribute`      | Marks methods, types, or assemblies for ATS export       |
| `Aspire.Hosting.AspireExportIgnoreAttribute`| Excludes a member from automatic ATS export              |
| `Aspire.Hosting.AspireDtoAttribute`         | Marks a class/struct as a serializable DTO               |
| `Aspire.Hosting.AspireUnionAttribute`       | Specifies that a parameter/property accepts a union type |

The attributes must live in the `Aspire.Hosting` namespace. Matching the simple type name alone is not sufficient.

## Defining Your Own Attributes

Copy the attribute definitions below into your integration project. Keep them in the `Aspire.Hosting` namespace so their full names match what the scanner expects.

### AspireExportAttribute

```csharp
namespace Aspire.Hosting;

[AttributeUsage(
    AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Interface
        | AttributeTargets.Assembly | AttributeTargets.Property,
    Inherited = false,
    AllowMultiple = true)]
public sealed class AspireExportAttribute : Attribute
{
    /// <summary>
    /// Capability export on methods. The capability ID is computed as {AssemblyName}/{id}.
    /// </summary>
    public AspireExportAttribute(string id)
    {
        Id = id;
    }

    /// <summary>
    /// Type export (parameterless). The type ID is derived as {AssemblyName}/{TypeName}.
    /// </summary>
    public AspireExportAttribute()
    {
    }

    /// <summary>
    /// Assembly-level export for types you don't own.
    /// </summary>
    public AspireExportAttribute(Type type)
    {
        Type = type;
    }

    /// <summary>
    /// The method name for capability exports (null for type exports).
    /// </summary>
    public string? Id { get; }

    /// <summary>
    /// The CLR type for assembly-level type exports.
    /// </summary>
    public Type? Type { get; set; }

    /// <summary>
    /// A description of what this export does.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Override the generated method name in polyglot SDKs.
    /// </summary>
    public string? MethodName { get; set; }

    /// <summary>
    /// When true, the type's properties are exposed as get/set capabilities.
    /// </summary>
    public bool ExposeProperties { get; set; }

    /// <summary>
    /// When true, the type's public instance methods are exposed as capabilities.
    /// </summary>
    public bool ExposeMethods { get; set; }
}
```

### AspireExportIgnoreAttribute

```csharp
namespace Aspire.Hosting;

[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Method,
    Inherited = false,
    AllowMultiple = false)]
public sealed class AspireExportIgnoreAttribute : Attribute
{
    /// <summary>
    /// Optional reason why this member is excluded from ATS export.
    /// </summary>
    public string? Reason { get; set; }
}
```

### AspireDtoAttribute

```csharp
namespace Aspire.Hosting;

[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct,
    Inherited = false,
    AllowMultiple = false)]
public sealed class AspireDtoAttribute : Attribute
{
    /// <summary>
    /// Optional type identifier. When set, serialized JSON includes a $type field.
    /// </summary>
    public string? DtoTypeId { get; set; }
}
```

### AspireUnionAttribute

```csharp
namespace Aspire.Hosting;

[AttributeUsage(
    AttributeTargets.Parameter | AttributeTargets.Property,
    AllowMultiple = false)]
public sealed class AspireUnionAttribute : Attribute
{
    /// <summary>
    /// Specifies the CLR types that form the union. Must have at least 2 types.
    /// </summary>
    public AspireUnionAttribute(params Type[] types)
    {
        Types = types;
    }

    public Type[] Types { get; }
}
```

## Usage Example

```csharp
using Aspire.Hosting;

// Mark a static method as an ATS capability
[AspireExport("addMyDatabase", Description = "Adds a MyDatabase resource")]
public static IResourceBuilder<MyDatabaseResource> AddMyDatabase(
    this IDistributedApplicationBuilder builder,
    string name)
{
    // ...
}

// Mark a resource type for ATS export
[AspireExport]
public class MyDatabaseResource : ContainerResource
{
    public MyDatabaseResource(string name) : base(name) { }
}

// Mark a context type whose properties should be exposed
[AspireExport(ExposeProperties = true)]
public class MyCallbackContext
{
    public string ConnectionString { get; set; } = "";

    [AspireExportIgnore(Reason = "Internal implementation detail")]
    public ILogger Logger { get; set; } = null!;
}

// Mark a DTO for structured data passing
[AspireDto]
public sealed class AddMyDatabaseOptions
{
    public required string Name { get; init; }
    public int? Port { get; init; }
}
```

## Important Notes

- **Constructor signatures must match by arity and argument type**: `()`, `(string)`, `(Type)` for `AspireExportAttribute`; `(params Type[])` for `AspireUnionAttribute`. Parameter names can differ.
- **Property names must match exactly**: `Type`, `Description`, `MethodName`, `ExposeProperties`, `ExposeMethods`, `Reason`, `DtoTypeId`, `Types`.
- **Namespaces must match exactly**: copied attributes need to be declared in the `Aspire.Hosting` namespace so their full names match the built-in ATS attributes.
- If you later add a reference to `Aspire.Hosting` and both your custom attribute and the official one are applied to the same member, both will be detected (the scanner takes the first match).
- The scanner uses `CustomAttributeData` which reads metadata without instantiating the attribute, so your attribute types don't need to be loadable at scan time — only the full name and supported members must match.
