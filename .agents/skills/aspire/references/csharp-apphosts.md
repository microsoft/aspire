# C# AppHosts

Use this when the AppHost is implemented in C# and the task involves understanding APIs, extension methods, overloads, or builder chains before editing code.

## Scenario: I Need Official Docs For An Unfamiliar C# AppHost API

Use these commands when you need the documented Aspire pattern and the C# API reference before changing AppHost code.

```bash
aspire docs search <query>
aspire docs get <slug>
aspire docs api search <query> --language csharp
aspire docs api get <id>
```

Keep these points in mind:

- Use Aspire docs first when the task is about understanding an unfamiliar integration workflow or dashboard command pattern.
- Use `aspire docs api` when the task is about finding the C# reference entry for a resource builder API, extension method, or member.
- Search for the resource or pattern name before guessing the C# API shape.

## Scenario: I Need To Read The Local C# API Surface More Closely

Use this when the docs tell you what concept to use, but you still need to inspect local symbols, signatures, or overloads in C# code.

Keep these points in mind:

- If the `dotnet-inspect` skill is available, use it to inspect local C# APIs, extension methods, overloads, and chained builder return types.
- Keep `dotnet-inspect` scoped to understanding APIs and symbols; do not treat it as a replacement for Aspire docs.
- When `dotnet-inspect` is not available, fall back to reading local AppHost code together with the relevant Aspire docs pages.
