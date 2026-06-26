# Package and discoverability lifecycle

Hosting integrations are discovered, installed, documented, and eventually deprecated as packages. API quality is not enough; users must be able to find and understand the integration.

## Package shape

DO:

- Follow existing `Aspire.Hosting.{Technology}` package naming.
- Keep hosting integration packages focused on AppHost resource modeling.
- Keep consuming-app client setup in client integration packages or consuming-app docs, not hosting package docs.
- Add package metadata, description, icons, README, and tags consistent with nearby integrations.
- Keep container image tag constants and package metadata easy for automation to update.
- For a new first-party packable integration that has not shipped a baseline package yet, set the existing repo baseline-validation opt-out used for new packages (for example `DisablePackageBaselineValidation`) until the first release establishes a baseline.

DON'T:

- Don't put unrelated client/runtime code into a hosting integration package.
- Don't add external package feeds or change shared package configuration unless explicitly requested.
- Don't fix missing baseline-package restore errors by editing NuGet feeds or package source mappings; use the repo's new-package baseline opt-out instead.
- Don't make new packages stable by default without checking release posture.

## Preview, stable, and experimental posture

New integrations and emerging deployment/language features commonly need preview or experimental treatment before stable release.

DO:

- Mark unstable APIs with `[Experimental]`.
- Keep new packages preview until the team intentionally stabilizes them.
- Document known limitations in XML docs and README.
- Avoid obsolete compatibility shims for APIs that have not shipped stable.

DON'T:

- Don't add `[Obsolete]` churn in a preview-only package when the API can still be corrected directly.
- Don't ship broad public APIs as stable without enough bake time.

## `aspire add` discoverability

DO:

- Ensure the package can be added by exact integration ID, for example `aspire add Aspire.Hosting.PostgreSQL`.
- Keep package names and README examples aligned with the integration ID.
- Hide packages that should not be user-discoverable, such as deprecated, internal, or support-only packages.
- Include TypeScript usage examples when the package exports TypeScript-compatible APIs.

DON'T:

- Don't rely on fuzzy search names in docs.
- Don't make deprecated integrations prominent in discovery.

## Gallery and docs presence

DO:

- Provide a concise hosting README.
- Link to the integration gallery and relevant official service docs.
- Include prerequisites such as Azure subscription, Docker, language toolchains, or external service accounts.
- Include trademark notices when required.

DON'T:

- Don't make the hosting README a full tutorial for the underlying technology.
- Don't document consuming-app dependency injection in the hosting README.

## Automation and maintenance

DO:

- Keep container image registry/image/tag constants centralized.
- Make image tags easy for automated update tooling to find.
- Keep generated code or API baseline expectations clear.
- Include tests that fail when discovery-critical metadata drifts.

DON'T:

- Don't scatter image names or versions across extension methods, tests, and docs.
- Don't manually edit generated API baseline files unless explicitly regenerating API baselines.
