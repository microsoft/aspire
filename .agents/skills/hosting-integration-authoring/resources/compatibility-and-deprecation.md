# Compatibility and deprecation lifecycle

Hosting integrations evolve across releases. Keep public API changes intentional, documented, and friendly to generated SDKs.

## Experimental APIs

DO:

- Mark unstable APIs with `[Experimental("ASPIRE...")]`.
- Use unique diagnostic IDs.
- Apply experimental attributes consistently to dependent types and members.
- Include clear docs for known limitations.

DON'T:

- Don't expose new deployment, publishing, pipeline, or language-runtime APIs as stable by accident.
- Don't reuse an existing experimental diagnostic ID.

## Obsolete APIs

DO:

- Use `[Obsolete]` for APIs that have shipped stable and need a migration path.
- Provide actionable obsolete messages.
- Keep obsolete APIs hidden from generated SDKs when they should not appear in new AppHost languages.
- Use `[EditorBrowsable(EditorBrowsableState.Never)]` when the API should be hidden from IntelliSense but retained for compatibility.
- Add tests or API review notes for important compatibility shims.

DON'T:

- Don't add obsolete shims for preview-only APIs that can still be renamed or removed directly.
- Don't leave obsolete APIs exported to generated SDKs without a reason.
- Don't remove stable public APIs without an explicit breaking-change decision.

## Deprecating integrations

When an entire integration is being retired, apply a consistent soft-deprecation path:

- Mark public API `[Obsolete]`.
- Add a README warning.
- Hide the package from `aspire add` discovery.
- Remove or disable integration-specific automation only when it is no longer needed.
- Suppress resulting warnings in first-party consumers.
- Keep one final obsolete release when required.

## Renames and replacements

DO:

- Prefer additive replacements for stable APIs.
- Keep old APIs delegating to new APIs when compatibility requires it.
- Preserve behavior unless the breaking change is intentional.
- Update C# docs, generated SDK docs, README examples, and tests together.

DON'T:

- Don't change parameter meaning while keeping the same name.
- Don't change endpoint names, connection property names, environment variable names, or generated method names casually.
- Don't use `MethodName` or export ID changes that break generated SDK callers unless that is the explicit migration.

## API baselines

Generated API baseline files under `*/api/*.cs` track shipped public surface.

DO:

- Leave API baseline files untouched during ordinary implementation work unless explicitly regenerating baselines.
- Review new public APIs for naming, versionability, nullability, experimental status, and polyglot projection.

DON'T:

- Don't manually edit API baseline files.
- Don't use baseline absence as proof that a public API is private or safe to change.
