# Quarantine Tools

Roslyn based utility to add or remove `[QuarantinedTest]` or `[ActiveIssue]` attributes on test methods.

- Quarantine: adds `[QuarantinedTest("<issue url>")]` or `[ActiveIssue("<issue url>")]`
- Unquarantine: removes any `QuarantinedTest` or `ActiveIssue` attribute from the method

## Prerequisites

- .NET SDK 10+ (for source-file `dotnet run`)

## Preferred usage (dotnet run)

```bash
# Show help
dotnet run --project tools/QuarantineTools -- --help

# Quarantine a test with QuarantinedTest (default mode)
dotnet run --project tools/QuarantineTools -- -q -i https://github.com/microsoft/aspire/issues/1234 Full.Namespace.Type.Method

# Disable a test with ActiveIssue attribute
dotnet run --project tools/QuarantineTools -- -q -m activeissue -i https://github.com/microsoft/aspire/issues/1234 Full.Namespace.Type.Method

# Unquarantine a test with QuarantinedTest (removes attribute)
dotnet run --project tools/QuarantineTools -- -u Full.Namespace.Type.Method

# Re-enable a test with ActiveIssue (removes attribute)
dotnet run --project tools/QuarantineTools -- -u -m activeissue Full.Namespace.Type.Method

# Specify a custom tests root folder (defaults to <repo-root>/tests)
dotnet run --project tools/QuarantineTools -- -q -r tests/MySubset -i https://github.com/org/repo/issues/1 N1.N2.C.M

# Use a custom attribute full name (overrides mode default)
dotnet run --project tools/QuarantineTools -- -q -a MyCompany.Testing.CustomQuarantinedTest -i https://example.com/issue/1 N1.N2.C.M
```

## Notes on CLI flags

- `-q`/`--quarantine` and `-u`/`--unquarantine` are mutually exclusive (pick one).
- `-i`/`--url <issue-url>` is required when using `-q`.
- `-m`/`--mode <mode>` controls which attribute to add/remove:
  - `quarantine` (default): Uses `Aspire.TestUtilities.QuarantinedTest`
  - `activeissue`: Uses `Xunit.ActiveIssueAttribute`
- `-a`/`--attribute <Full.Name>` overrides the default attribute based on mode. The tool accepts name with or without the `Attribute` suffix. If a namespace is supplied, the tool adds a `using` directive when inserting the short attribute name.
- `-r`/`--root <folder>` overrides the scan root (by default, `<repo-root>/tests`). Relative paths are resolved against the repository root.
- `-h`/`--help` prints usage information and exits.

## Notes

- Methods are matched by fully-qualified name: `Namespace.Type.Method`. Nested types can use `+`, e.g. `Namespace.Outer+Inner.TestMethod`.
- The tool scans the repo `tests/` folder and edits files in place, ignoring `bin/` and `obj/`.
- Quarantine is idempotent (won't duplicate attributes); unquarantine only removes the attribute if present.
- When using `activeissue` mode, the tool adds `using Xunit;` if needed.
- When using `quarantine` mode (default), the tool adds `using Aspire.TestUtilities;` if needed.
