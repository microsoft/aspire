# Relationships, dependencies, and companions

Aspire relationships are semantic. Pick the relationship that matches the behavior.

## Parent-child resources

Use parent-child structure for domain subresources owned by a parent service.

DO:

- Implement `IResourceWithParent<TParent>` on child resource types.
- Expose child creation on the parent builder: `server.AddDatabase("db")`, `storage.AddBlobContainer("images")`.
- Use globally unique Aspire resource names and separate physical names.
- Keep child auth and endpoint logic delegated to the parent.

DON'T:

- Don't add top-level `builder.AddDatabase(...)` for a database that belongs to one server.
- Don't make the child re-implement parent connection, auth, emulator, or Azure branching.

## Resource references

Use `WithReference` when a consumer needs connection info, service discovery, or integration-provided environment variables.

For overlay/configuration objects, define clear projection APIs such as `AsClient()` or `WithReference<TOverlay>()` so users understand what gets wired.

## Parent and custom relationships

Use `WithParentRelationship` when a companion or child resource is visually/semantically attached to a parent in the app model.

Use `WithRelationship(parent, "Label")` for display or custom semantic relationships that are not necessarily ownership.

## Setup siblings

Run-mode setup resources prepare an app before it starts.

DO:

- Create setup siblings only in run mode.
- Name setup siblings predictably, for example `{resource}-mod-tidy`.
- Mark setup siblings `.ExcludeFromManifest()`.
- Make the main resource wait with `WaitForCompletion`.
- Handle ordering if setup helpers can be called in any sequence.

DON'T:

- Don't include setup siblings in publish manifests.
- Don't rely on user call order when setup resources have dependencies.

## Admin/dev companions

Admin UIs and development tools are companion resources, not deployment resources.

DO:

- Add companions with `With{Tool}` on the parent resource.
- Return the original parent builder from `With{Tool}`.
- Use a container resource for the tool.
- Configure image, registry, endpoint, environment, health check if appropriate.
- Add parent/custom relationships.
- Call `.ExcludeFromManifest()`.
- Avoid duplicates when the companion is singleton-style.

DON'T:

- Don't expose admin UIs to publish/deploy output by default.
- Don't make a companion a top-level required resource.
- Don't hardcode host ports; provide `WithHostPort(int? port)` when fixed ports are useful.
