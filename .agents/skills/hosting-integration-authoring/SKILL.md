---
name: hosting-integration-authoring
description: Guides authoring and reviewing Aspire.Hosting integration APIs. Classifies integration archetypes, then applies self-contained best practices for naming, resource shape, run/publish/deploy behavior, eventing, connection properties, security, endpoint semantics, polyglot exports, READMEs, and tests.
---

# Aspire hosting integration authoring

Use this skill when creating, modifying, or reviewing `Aspire.Hosting.*` integration APIs.

This skill is self-contained. The resource files in this skill are the authoritative guidance, and repository paths are examples of existing patterns only.

## First step: classify the integration

Read `resources/selector-matrix.md` first. Then read `resources/app-model-fundamentals.md` for the app model rules that apply to every hosting integration. Classify the work across all four axes:

1. Resource shape.
2. Lifecycle mode.
3. Integration role.
4. Structure.

Do not force the integration into a single bucket. Most integrations compose several patterns. For example, PostgreSQL is a container-backed service, has parent-child database resources, exposes connection properties, creates databases during resource readiness, and has admin UI companion helpers.

## Then read the matching resources

Always read the relevant archetype resource and the cross-cutting resources that apply to the change.

| If the integration involves | Read |
| --- | --- |
| Any resource, annotation, lifecycle event, structured value, endpoint, manifest, or app-model behavior | `resources/app-model-fundamentals.md` |
| Local container service, database, broker, cache, vector DB | `resources/archetype-container-backed-service.md` |
| Admin UI, inspector, dashboard, load/test tool, or standalone utility container | `resources/archetype-admin-and-tool-container.md` |
| Migration, package/tool install, model pull, schema deployment, DACPAC, or one-shot setup helper | `resources/archetype-setup-and-migration-helper.md` |
| Serialized controller, reconciler, command-driven resource operations, drift detection, or state-machine orchestration | `resources/archetype-controller-reconciler.md` |
| Sidecar, component registry, telemetry collector, service mesh, middleware, or app-wide annotation-driven infrastructure | `resources/archetype-sidecar-and-middleware.md` |
| Local tunnel, webhook forwarder, callback bridge, or CLI that exposes/forwards local endpoints | `resources/archetype-tunnel-and-webhook-bridge.md` |
| Secret manager, credential broker, external secret provider, or provider-backed managed secret child resources | `resources/archetype-secret-provider.md` |
| External/SaaS service reference with API key/endpoint, no local container/provisioning | `resources/archetype-external-cloud-reference.md` |
| Azure resource provisioning, Bicep, role assignments, existing Azure resources, emulators, local containers for Azure resources | `resources/archetype-azure-provisioning.md` |
| Docker Compose, Kubernetes, Azure Container Apps, or another deployment target/publisher | `resources/archetype-deployment-target-publisher.md` |
| Python, Go, JavaScript, Node, Vite, Next.js, or another language runtime/workload | `resources/archetype-language-executable-app.md` |
| Non-resource model/configuration overlay, e.g. Orleans-style APIs | `resources/archetype-overlay-configuration.md` |
| Public API names, overloads, return types, polyglot AppHost compatibility, annotations, experimental state | `resources/api-naming-and-shape.md` |
| Any run/publish/deploy branching | `resources/run-publish-deploy-modes.md` |
| Event subscriptions, initialization, generated files, health checks, pipeline steps | `resources/eventing-and-initialization.md` |
| Custom resource lifetime, synthetic/facade resources, manually allocated endpoints, or resource notification state machines | `resources/custom-lifecycle-and-facade-resources.md` |
| `IResourceWithConnectionString`, `WithReference`, environment variables, URI/JDBC properties | `resources/connection-properties.md` |
| Parent-child resources, companions, setup siblings, `WithReference`, waits, relationships | `resources/relationships-and-companions.md` |
| `[AspireExport]`, TypeScript/polyglot AppHosts, ATS metadata, analyzer diagnostics, DTOs, union parameters, value catalogs, callback contexts | `resources/polyglot-exports.md` |
| Endpoint names, service discovery, external URLs, reference endpoints, endpoint environment variables | `resources/endpoints-and-service-discovery.md` |
| Secrets, parameters, credentials, managed identity, RBAC, private networking, generated artifacts containing sensitive data | `resources/security-secrets-and-identity.md` |
| Resource names, physical names, annotations, constructors, mutability, model invariants | `resources/resource-model-invariants.md` |
| Package metadata, preview/stable posture, `aspire add` discovery, icons, gallery/README visibility | `resources/package-and-discoverability.md` |
| Dashboard icons, URLs, commands, notifications, resource logs, admin companion UX | `resources/dashboard-ux.md` |
| Generated config files, `WithContainerFiles`, file permissions, temp/store usage, build dependencies | `resources/generated-files-and-container-files.md` |
| Toolchain detection, path handling, Windows/macOS/Linux behavior, shell quoting, executable permissions | `resources/cross-platform-tooling.md` |
| Experimental/obsolete/deprecation lifecycle, compatibility shims, migration guidance | `resources/compatibility-and-deprecation.md` |
| README or tests | `resources/testing-and-readmes.md` |

## Authoring workflow

1. State the classification briefly before changing or reviewing code.
2. Apply the archetype-specific DO/DON'T list.
3. Apply every relevant cross-cutting checklist.
4. Validate both run-mode and publish/deploy behavior when the integration changes those surfaces.
5. Keep generated API baseline files under `*/api/*.cs` untouched unless the task explicitly asks for API baseline regeneration.

## Review workflow

When reviewing an integration PR, look for concrete violations that can cause wrong runtime behavior, wrong generated manifests, broken deployment output, bad public API, missing connection properties, or bad polyglot projection. Do not spend review budget on style-only comments.
