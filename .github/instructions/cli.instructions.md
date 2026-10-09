---
applyTo: "src/Aspire.Cli/**/*.cs"
---

# CLI agent instructions

## Application code, not a library

- The CLI is an application, not a reusable library. Its public C# types and members are implementation details, not a supported public API.
- Do not flag breaking changes to those types or members, or require compatibility overloads or deprecation shims.
- XML documentation is optional for application types and members, regardless of accessibility. Do not flag missing XML documentation or require it when adding or changing code; this overrides the shared XML documentation requirements.
- Continue reviewing compatibility of user-visible behavior and external contracts, such as CLI commands, options, exit codes, and machine-readable output.
