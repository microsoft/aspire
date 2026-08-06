# Connection properties

Resources that can be referenced by applications should expose structured connection metadata with `IResourceWithConnectionString.GetConnectionProperties`.

## Standard properties

Use these property names when available:

| Property | Use |
| --- | --- |
| `Host` | Hostname or IP endpoint |
| `Port` | Port number |
| `Username` | User name for authentication |
| `Password` | Password or secret reference |
| `Uri` | Protocol URI, for example `postgres://user:password@host:port/database` |
| `DatabaseName` | Database or logical subresource name |
| `JdbcConnectionString` | JDBC connection string when the ecosystem has a documented JDBC format |
| `Azure` | Only for resources that can be Azure-hosted or non-Azure based on mode/context |

Do not define `Azure` for resources that do not have an Azure/non-Azure split such as `IsContainer`, `IsEmulator`, or `InnerResource`.

## Expressions

DO:

- Use `ReferenceExpression` or `ReferenceExpressionBuilder`.
- Use URI formatting for credentials and path segments, for example `{PasswordParameter:uri}`.
- Keep connection values late-bound.
- Expose both server-level and child-level properties when child resources represent different connection targets.
- Use `UriExpression` for URI-shaped values.

DON'T:

- Don't eagerly resolve endpoint host/port in constructors.
- Don't concatenate secrets into plain strings outside reference expressions.
- Don't omit optional properties when the resource actually has them.
- Don't invent non-standard property names when a common name exists.

## Parent-child resources

Child resources should implement `IResourceWithParent<TParent>` and inherit parent connection properties.

Use parent property combination and override child-specific values:

```csharp
IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties() =>
    Parent.CombineProperties(
    [
        new("DatabaseName", ReferenceExpression.Create($"{DatabaseName}")),
        new("Uri", UriExpression),
        new("JdbcConnectionString", JdbcConnectionString),
    ]);
```

## Dual-mode resources

When a resource can run as a container/emulator or publish as Azure/cloud:

- Connection properties must work in every mode.
- Mode-specific values should branch through `IsContainer`, `IsEmulator`, or `InnerResource`.
- Child resources must preserve the same property names across modes.
- The `Azure` property should be `"true"` or `"false"` only when that distinction is meaningful to consumers.
