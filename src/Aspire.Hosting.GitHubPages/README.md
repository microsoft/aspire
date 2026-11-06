# GitHub Pages hosting integration

Use this integration to model a GitHub Pages static website environment and generate its complete website bundle in an Aspire solution.

**Spike:** This integration is experimental and unpublished. It generates files only; it does not upload artifacts or deploy to GitHub Pages. `aspire deploy` fails explicitly rather than reporting a skipped deployment as successful.

## Getting started

### Prerequisites

A Vite application with a static `build` script, and Docker or Podman with image-build support. GitHub credentials and a running frontend container are not required to generate a bundle.

### Add the integration

For this source-only spike, reference `Aspire.Hosting.GitHubPages.csproj` from a development AppHost. There is no published integration available through `aspire add` yet. Suppress `ASPIREGITHUBPAGES001` to evaluate the experimental API.

## Usage example

Then, in the AppHost, add a GitHub Pages environment and assign the frontend to it:

**C#**

```csharp
var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repository");

builder.AddViteApp("frontend", "../frontend")
       .WithComputeEnvironment(pages);
```

**TypeScript**

```typescript
const pages = await builder.addGitHubPagesEnvironment("pages", "owner/repository");

await builder.addViteApp("frontend", "../frontend")
    .withComputeEnvironment(pages);
```

The registration and custom-domain APIs are exported. Full generated-SDK validation remains a preview-readiness milestone.

During local development, the Vite dev server behaves normally; the Pages environment has no local runtime. During publish, Aspire builds the existing build-only Vite image and extracts its static files without starting or pushing that image. Pages controls Vite's `--base` argument.

## Generate a bundle without deployment

Publish the complete application:

```bash
aspire publish --output-path ./artifacts
```

Or select only the frontend bundle and its prerequisites:

```bash
aspire do bundle-frontend --output-path ./artifacts
```

The environment owns the generated `github-pages/pages/` directory:

```text
github-pages/
  pages/
    dist/
      index.html
      assets/
    site.json
```

`dist` contains the extracted website. `site.json` describes the destination repository, source resource, intended URL, browser origin, and base path. These are declared addresses, not verified deployment outputs. Existing output is replaced only after extraction, validation, and preparation of the new output succeed. Do not edit files in this generated directory.

Bundle generation requires a root `index.html` and rejects symbolic links. A repository receives one static website; assigning multiple workloads or declaring multiple environments for the same repository fails.

## Site addresses

| Configuration | Intended URL | Vite base path |
| --- | --- | --- |
| `owner/repository` | `https://owner.github.io/repository/` | `/repository/` |
| `owner/owner.github.io` | `https://owner.github.io/` | `/` |
| Explicit canonical domain | `https://app.example.com/` | `/` |

Declare an existing canonical domain:

```csharp
var pages = builder.AddGitHubPagesEnvironment("pages", "owner/repository")
                   .WithCustomDomain("app.example.com");
```

This does not change GitHub settings, DNS, certificates, redirect aliases, or access controls. Only repositories on github.com are modeled. The default address assumes public Pages hosting without an inherited account custom domain. Declare the actual serving hostname explicitly when that assumption does not hold, including an Enterprise private site's unique hostname. Provider discovery and verification are not implemented.

The browser origin excludes the path: a project site has origin `https://owner.github.io`, not a repository-scoped origin. Deployed HTTP endpoint references resolve to the intended HTTPS website URL regardless of the dev server's port or scheme. Backend CORS configuration is not implemented in this stage.

## Supported static sources and limitations

Vite's default build-only publishing mode is supported. Server/SSR publishing and automatic Vite base-path configuration for user-authored Dockerfiles are rejected. Remove explicit `--base` build arguments when Pages owns the Vite build.

Other container images can supply static content using exactly one `ContainerFilesSourceAnnotation` with an absolute directory path, together with `WithComputeEnvironment(pages)`. Their build must already produce content configured for the declared base path. A supplied image must exist locally; extraction does not pull it. Container-file producers use the same image-build pipeline and file-consumer annotations as other Aspire publishing integrations.

Aspire manifest publishing is not supported for GitHub Pages.

There is no Pages publisher, simulator, backend CORS wiring, late-bound backend build configuration, or provider authentication in this stage. No private-site visibility, DNS, HTTPS readiness, artifact size limits, SPA rewrites, or publication success is inferred from bundle generation.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://docs.github.com/en/pages/getting-started-with-github-pages/what-is-github-pages
* https://docs.github.com/en/pages/configuring-a-custom-domain-for-your-github-pages-site
* https://vite.dev/config/shared-options.html#base

## Feedback & contributing

https://github.com/microsoft/aspire
