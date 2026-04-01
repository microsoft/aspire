# C# AppHosts

Use this when the AppHost is implemented in C# and the task involves understanding APIs, extension methods, overloads, or builder chains before editing code.

## Scenario: I Need Official Docs For An Unfamiliar C# AppHost API

Use these commands when you need the documented Aspire pattern before changing C# AppHost code.

```bash
aspire docs search <query>
aspire docs get <slug>
```

Keep these points in mind:

- Use Aspire docs first when the task is about understanding an unfamiliar resource builder API, extension method, dashboard command pattern, or integration workflow.
- Search for the resource or pattern name before guessing the C# API shape.
- Use the docs to confirm the recommended pattern before editing the AppHost.

## Scenario: I Need To Read The Local C# API Surface More Closely

Use this when the docs tell you what concept to use, but you still need to inspect local symbols, signatures, or overloads in C# code.

Keep these points in mind:

- If the `dotnet-inspect` skill is available, use it to inspect local C# APIs, extension methods, overloads, and chained builder return types.
- Keep `dotnet-inspect` scoped to understanding APIs and symbols; do not treat it as a replacement for Aspire docs.
- When `dotnet-inspect` is not available, fall back to reading local AppHost code together with the relevant Aspire docs pages.
