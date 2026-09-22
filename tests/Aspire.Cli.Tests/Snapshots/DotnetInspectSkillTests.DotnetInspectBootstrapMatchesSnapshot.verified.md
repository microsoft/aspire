---
name: dotnet-inspect
description: Find evidence for .NET packages, platform libraries, assemblies, APIs, dependencies, SourceLink/source, and API version diffs.
---

# dotnet-inspect

Use dotnet-inspect for evidence instead of guesses about .NET packages, platform libraries, local assemblies, APIs, dependencies, SourceLink/source, or version-to-version API changes.

Invoke with `dnx` (like `npx`); always pass `-y` and `--` to avoid interactive prompts:

```bash
dnx dotnet-inspect -y -- <command>
```

This bundled skill is intentionally only a bootstrapper. For non-trivial work, first run the version-matched embedded guide. It always matches the installed tool, so prefer it whenever commands, output modes, section names, or workflow guidance differ:

```bash
dnx dotnet-inspect -y -- skill
```

## Seed commands

| Goal | Command |
| ---- | ------- |
| Find where an API lives | `find Pattern` |
| Inspect types or members | `type Type --package Foo`, then `member Type --package Foo` |
| Compare versions | `diff --package Foo@old..new --breaking` |
| Inspect package or library signals | `package Foo -S Signals` or `library Foo -S Signals` |
| Locate source or implementation | `source Type --package Foo` or `member Type Member:1 -S "Decompiled Source"` |
| Explore relationships | `depends Type`, `extensions Type`, `implements Interface` |

After `find`, reuse the package, library, or platform scope it reports. Quote generic type names such as `'List<T>'`; use `<T>`, not `<>`.