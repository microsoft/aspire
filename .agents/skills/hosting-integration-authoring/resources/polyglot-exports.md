# Polyglot exports and multi-language integration authoring

Aspire hosting integrations are C# libraries. Multi-language AppHosts call the C# implementation through generated SDKs. The Aspire CLI loads the integration assembly, scans ATS metadata such as `[AspireExport]`, generates a typed SDK, and dispatches calls back to the C# code over JSON-RPC.

Design the ATS contract first: the methods, DTOs, callbacks, values, and docs that generated SDK users should see. Keep the C# API ergonomic, then adapt it to ATS with explicit exports, DTO/options types, unions, small context/editor types, ignored C#-only overloads, and internal export adapters. See `api-naming-and-shape.md` for the naming rules that keep the C# and generated AppHost APIs aligned.

## Analyzer enablement

Every integration that exports ATS APIs should have integration analyzer coverage.

Use one supported path:

- If the project already references `Aspire.Hosting`, set `<EnableAspireIntegrationAnalyzers>true</EnableAspireIntegrationAnalyzers>`.
- Otherwise, reference `Aspire.Hosting.Integration.Analyzers` with `PrivateAssets="all"` using the same Aspire package version.

A clean build with zero analyzer warnings or errors is the baseline, but it is not enough. Inspect the generated SDK signatures and docs before shipping.

Do not manually edit generated API or ATS baseline files as part of ordinary integration authoring. In the Aspire repo, checked-in `src/Aspire.Hosting*/api/*.cs` and `*.ats.txt` files are release compatibility baselines; PR validation generates the current surface separately and the release/review workflow updates checked-in baselines after API changes are accepted.

## Export attributes

DO:

- Mark generated-SDK-compatible APIs with `[AspireExport]`.
- Mark resource types with `[AspireExport]` so generated SDKs can reference typed handles.
- Use `[AspireExport(ExposeProperties = true)]` only on small resource/handle types where every public property should be projected.
- Prefer individual `[AspireExport]` attributes on callback context and editor properties.
- Add `[ResourceName]` to Aspire resource-name parameters.
- Use `[AspireExportIgnore(Reason = "...")]` for C#-only overloads, convenience overloads, deprecated APIs, unsupported types, or implementation details.
- Use `[AspireExport(RunSyncOnBackgroundThread = true)]` when an exported method invokes a synchronous callback inline, including async-returning methods that invoke the callback before the first `await`.
- Use `[AspireUnion(...)]` for method parameters that accept a bounded set of live AppHost value shapes.
- Use `[AspireDto]` for JSON-shaped options/configuration objects.
- Use `[AspireValue]` for immutable predefined value catalogs such as model names, SKUs, or regions.

DON'T:

- Don't export every C# overload and hope generated names make the API usable.
- Don't rely on C# overload resolution; ATS dispatch is not C# overload resolution.
- Don't expose interpolated string handlers, loggers, service providers, delegates, configuration objects, mutable framework types, or opaque implementation handles in DTOs.
- Don't bury live handles such as `EndpointReference`, `ReferenceExpression`, resources, or `IResourceBuilder<T>` inside DTOs. Accept them as union-shaped method parameters or editor method parameters.
- Don't expose provisioning-only handles such as `BicepOutputReference` through broad `ExposeProperties`; mark them ignored, make them non-public, or export an ATS-compatible projection.
- Don't turn on broad `ExposeProperties` or `ExposeMethods` for large framework-style types.
- Don't omit the `Reason` from `[AspireExportIgnore]`.

## XML docs and ATS doc overrides

Generated SDK docs come from XML documentation. Treat docs as part of the ATS contract.

Document every exported method, DTO, parameter, property, callback context, editor method, and value catalog with language-neutral XML comments. Avoid C#-specific implementation descriptions when generated SDK users cannot see those types.

Use ATS override tags when standard C# XML docs do not translate well:

- `<ats-summary>` overrides `<summary>`.
- `<ats-param name="...">` overrides a specific `<param>`.
- `<ats-returns>` overrides `<returns>`.
- `<ats-remarks>` overrides `<remarks>`.

An empty `ats-*` tag intentionally suppresses the matching standard doc in the generated SDK.

Use `<ats-see cref="!:kind:identifier.path" />` and `<ats-seealso cref="!:kind:identifier.path" />` for generated SDK links. Supported `kind` values are `type`, `method`, and `field`. The `!:` prefix prevents the C# compiler from validating the custom `cref`.

## Capability IDs and generated names

Capability IDs are runtime dispatch identifiers. They do not include the C# receiver type, parameter list, generic constraints, or overload signature.

Before adding or reviewing an export, answer two questions:

1. What should the generated AppHost call look like?
2. Is that generated call still clear if the caller has never seen the C# overload set?

DO:

- Keep capability IDs unique and stable within an assembly.
- Avoid explicit export IDs when the convention-derived ID is already correct.
- Use `MethodName` only when the runtime capability ID must be unique but the generated methods live on different target types and can safely share a friendly method name.
- Inspect generated member names per target type, not just runtime capability IDs.
- Prefer existing framework exports for shared concepts such as container registries; target-specific convenience wrappers can create generated member collisions without adding capability.

DON'T:

- Don't reuse explicit export IDs across methods.
- Don't set an explicit export ID that duplicates the convention-derived name.
- Don't use `MethodName` to give multiple exports the same generated method name on the same generated target type.
- Don't assume different C# receiver types prevent capability collisions.
- Don't export a target-specific overload solely to change annotation mutation behavior when the generic exported helper already expresses the same user concept.

For one user concept, export one ATS method and model variation with a DTO/options object, `[AspireUnion]`, or an internal dispatcher.

For different concepts on the same generated target type, use distinct generated method names.

When C# and ATS need different shapes, keep the C# API public and ergonomic, and add an internal exported adapter:

```csharp
[AspireExport("publishAsStaticWebsite")]
internal static IResourceBuilder<TResource> PublishAsStaticWebsitePolyglot<TResource>(
    this IResourceBuilder<TResource> builder,
    string? apiPath = null,
    IResourceBuilder<IResourceWithServiceDiscovery>? apiTarget = null)
    where TResource : JavaScriptAppResource
{
    return PublishAsStaticWebsiteCore(builder, apiPath, apiTarget);
}
```

Common cases that need a C# API plus a polyglot adapter:

- Callback configuration such as `Action<T>`, `Func<IResourceBuilder<T>, IResourceBuilder<T>>`, or tool-specific option delegates.
- C# generic metadata markers such as `IProjectMetadata`, package metadata, or strongly typed project references.
- Mutable dictionaries or framework types that do not project cleanly.
- Endpoint-reference overloads when generated SDKs need a string/parameter/external-service alternative.

DO:

- Mark the C#-only overload `[AspireExportIgnore(Reason = "...")]` with a reason that names the incompatible type and the replacement API.
- Export a polyglot-friendly overload with primitive, DTO, resource-builder, parameter, or external-service inputs.
- Use `MethodName` when the generated name should match the C# concept but the internal adapter method needs a unique CLR name.

DON'T:

- Don't leave C# callback or generic-metadata overloads as the only way to configure an exported feature.

## DTOs, options, unions, and live values

Use flat DTO/options objects for optional configuration:

```csharp
[AspireDto]
public sealed class AddMyWorkerOptions
{
    public string? ImageTag { get; init; }
    public string[] Args { get; init; } = [];
}
```

Generated SDKs should feel like plain object literals:

```typescript
const worker = await builder.addMyWorker("worker", {
    imageTag: "v1",
    args: ["--debug"]
});
```

DTO rules:

- Mark DTOs with `[AspireDto]`.
- Keep DTOs JSON-serializable.
- Use `init` setters for input properties.
- Use arrays, records, primitives, enums, other DTOs, and `Dictionary<string, T>` where `T` is ATS-compatible.
- Prefer flat options objects over nested `{ options: { ... } }` shapes for one optional options parameter.
- Avoid getter-only raw `List<T>` or `Dictionary<TKey, TValue>` properties on exported DTO/model types. Use `init`-settable arrays/DTO properties for JSON input/output shapes, or expose explicit editor methods/wrapper types for live mutation.

Use `[AspireUnion]` for live AppHost values:

```csharp
[AspireExport]
public static IResourceBuilder<T> WithSetting<T>(
    this IResourceBuilder<T> builder,
    string name,
    [AspireUnion(
        typeof(string),
        typeof(ReferenceExpression),
        typeof(EndpointReference),
        typeof(IResourceBuilder<ParameterResource>),
        typeof(IResourceBuilder<IResourceWithConnectionString>),
        typeof(IExpressionValue))]
    object value)
    where T : IResourceWithEnvironment
{
    return WithSettingCore(builder, name, value);
}
```

## Callback contexts and editor types

Generated SDK callbacks can call back into ATS/RPC. In TypeScript this usually means callbacks can `await` generated SDK calls.

DO:

- Keep callback context types small.
- Mark context types with `[AspireExport]`.
- Export only the members callback authors need.
- Expose editor objects for mutable state instead of raw mutable collections.
- Use editor methods such as `set`, `add`, and `remove`.
- Use exported editor/context types for callback mutations that must round-trip from generated SDKs.
- Set `RunSyncOnBackgroundThread = true` on exported methods that invoke synchronous callbacks inline.
- Make service access explicit through generated service-provider methods when callbacks need services.

DON'T:

- Don't expose raw `Dictionary`, `List`, `IServiceProvider`, or framework context objects directly unless they are known ATS-compatible and intentionally part of the generated contract.
- Don't export callbacks that mutate a DTO/options object and expect those mutations to round-trip from polyglot callers; DTOs model JSON-shaped input, not live editor state.
- Don't block an RPC thread while invoking a callback that may re-enter the generated SDK.

Example context/editor shape:

```csharp
[AspireExport]
internal sealed class EnvironmentEditor(Dictionary<string, object> environmentVariables)
{
    [AspireExport]
    public void Set(
        string name,
        [AspireUnion(
            typeof(string),
            typeof(ReferenceExpression),
            typeof(EndpointReference),
            typeof(IResourceBuilder<ParameterResource>),
            typeof(IResourceBuilder<IResourceWithConnectionString>))]
        object value)
    {
        environmentVariables[name] = value;
    }
}
```

## Exposed properties and methods

`ExposeProperties = true` and `ExposeMethods = true` are broad expansion switches. They export every compatible public member on the type, including inherited members where applicable.

Use them only on small, purpose-built handle/context types. Otherwise, export members individually.

Generated TypeScript property shapes differ by C# property shape:

| C# shape | Generated shape |
| --- | --- |
| Getter-only property | Async method, for example `resource(): Promise<T>` |
| Getter-only `AspireList<T>` or `AspireDict<K,V>` | Async method returning the wrapper |
| Settable mutable collection wrapper | Readonly synchronous wrapper property |
| Scalar read-write property | Readonly `PropertyAccessor<T>` with async `get()` and `set(value)` |

Use `[AspireExportIgnore]` on properties that should not be exposed. If TypeScript callers need to mutate state, prefer an explicit method or editor type over exposing broad mutable properties.

## Value catalogs

Use `[AspireValue]` for immutable predefined values that generated SDK users should reference as typed constants.

Rules:

- Apply to public static fields or public static properties with public static getters.
- Use a valid generated SDK catalog name and identifier path.
- Values are snapped once at scan time and emitted as generated SDK constants.
- Supported copied shapes include primitives, enums, arrays, read-only dictionaries, and DTOs containing supported copied shapes.

DON'T:

- Don't use `List<T>`, mutable `Dictionary<K,V>`, runtime handles, resources, builders, delegates, or runtime state in value catalogs.
- Don't expect `[AspireValue]` values to refresh at runtime.

## ATS-compatible type categories

Exported method signatures can use:

- Primitives: `string`, `bool`, numeric types.
- Value types: `DateTime`, `TimeSpan`, `Guid`, `Uri`.
- Enums.
- Handles: `IDistributedApplicationBuilder`, `IResourceBuilder<T>`, and resource types marked with `[AspireExport]`.
- DTOs marked with `[AspireDto]`.
- Static values marked with `[AspireValue]`.
- Collections where the element type is ATS-compatible.
- Delegates such as `Action<T>` and `Func<T>`.
- Core exported services such as `ILogger`, `IServiceProvider`, and `IConfiguration`.
- Special value types such as `ParameterResource`, `ReferenceExpression`, `EndpointReference`, `IExpressionValue`, and `CancellationToken`.
- Nullable forms of compatible types.

Types that are not ATS-compatible include interpolated string handlers and custom complex types without `[AspireExport]` or `[AspireDto]`.

## Analyzer diagnostics to understand

| ID | Meaning |
| --- | --- |
| ASPIREEXPORT001 | Standalone `[AspireExport]` method must be static |
| ASPIREEXPORT002 | Invalid export ID format |
| ASPIREEXPORT003 | Return type is not ATS-compatible |
| ASPIREEXPORT004 | Parameter type is not ATS-compatible |
| ASPIREEXPORT005 | `[AspireUnion]` requires at least two types |
| ASPIREEXPORT006 | Union type is not ATS-compatible |
| ASPIREEXPORT007 | Duplicate export ID for same target type |
| ASPIREEXPORT008 | Public extension method on exported type missing `[AspireExport]` or `[AspireExportIgnore]` |
| ASPIREEXPORT009 | Export name may collide with other integrations |
| ASPIREEXPORT010 | Synchronous callback invoked inline may deadlock |
| ASPIREEXPORT011 | Explicit export ID matches convention-derived name |
| ASPIREEXPORT012 | Callback context type missing `[AspireExport]` |
| ASPIREEXPORT013 | Duplicate polyglot capability ID across exports in same assembly |
| ASPIREEXPORT014 | Duplicate generated member name on same SDK target type |
| ASPIREEXPORT015 | `[AspireExport(Description = ...)]` is deprecated; use XML docs |
| ASPIREEXPORT016 | DTO property is a get-only mutable collection; add an init accessor |

## Local generated SDK validation

Test exports with a TypeScript AppHost that references the integration project directly in `aspire.config.json`.

Example package mapping:

```json
{
  "appHost": {
    "path": "apphost.mts",
    "language": "typescript/nodejs"
  },
  "packages": {
    "MyCompany.Hosting.MyDatabase": "../src/MyCompany.Hosting.MyDatabase/MyCompany.Hosting.MyDatabase.csproj"
  }
}
```

Then run `aspire restore` or `aspire run`, inspect `.aspire/modules/`, and verify:

- Generated imports.
- `.d.ts` method signatures.
- DTO shapes.
- Callback context accessors.
- Property accessor shapes.
- JSDoc generated from XML and `ats-*` docs.
- Capability/member name collisions.

New TypeScript AppHosts use `apphost.mts` and `.aspire/modules/*.mjs` imports. Generated TypeScript APIs are promise-heavy: await `createBuilder`, fluent calls, getter-only property methods, and property accessor `get()`/`set(value)` calls.
