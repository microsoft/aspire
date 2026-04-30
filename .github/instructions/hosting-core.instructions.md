---
applyTo: "src/Aspire.Hosting/**/*.cs"
---

# Hosting Core Review Patterns

- Callbacks that read runtime-only values must branch on `context.ExecutionContext.IsPublishMode`; in Publish mode emit references/placeholders instead of resolving concrete values.
- If an annotation caches callback results, provide and use an invalidation path on restart/retry; only keep faulted tasks cached when the inputs cannot change.
- Do not introduce circular constructor injection between hosting services; extract a small interface/service boundary (for example, `IDcpObjectFactory`) to break the cycle.
- Exceptions that should surface from pipeline steps without extra wrapping should derive from `DistributedApplicationException`.
- Treat null `ExitCode` as unknown, not success; DCP can report `Exited` before the real exit code arrives.
