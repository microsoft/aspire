# Aspire Deployment Roadmap

> **Status**: Draft — exploring the problem space and sharing ideas.
> This is an aspirational document. It does not represent commitments or timelines. Items here will eventually be broken into GitHub issues for prioritization and tracking.

## Current State (as of Aspire 13.2)

Aspire's deployment story has matured significantly. Today we ship:

- **Azure Container Apps** (`Aspire.Hosting.Azure.AppContainers`) — our most mature compute target with Bicep generation, managed identity, and deep Azure integration.
- **Azure App Service** (`Aspire.Hosting.Azure.AppService`) — GA support for deploying .NET applications to App Service.
- **Docker Compose** (`Aspire.Hosting.Docker`) — GA'd in 13.2. Generates `docker-compose.yml` files for local and self-hosted deployment scenarios.
- **Kubernetes** (`Aspire.Hosting.Kubernetes`) — manifest generation and Helm chart support exist, but the developer experience has significant gaps.
- **22+ Azure resource packages** — from Cosmos DB and Service Bus to Key Vault and Virtual Networks.
- **Pipeline execution framework** — internal infrastructure for orchestrating build, publish, and deploy steps.
- **CLI commands** — `aspire publish` and `aspire deploy`.

What follows are the four pillars of investment we believe will take the deployment experience from good to transformative.

## Pillar 1: Compute Environment Completeness

### The Challenge

We have four compute environment targets at varying levels of maturity. Azure Container Apps and App Service are in a good place. Docker Compose GA'd in 13.2 and is in a fairly good state. Kubernetes support, however, has fundamental gaps that prevent it from being a first-class deployment target.

### Kubernetes

Kubernetes is the most requested deployment target and the one with the most work remaining.

#### `aspire deploy` for Kubernetes

Today, `aspire publish` can generate Kubernetes manifests and Helm charts, but there is no `aspire deploy` support for Kubernetes. Developers have to take the generated artifacts and apply them manually. We need to close this gap so that the experience is comparable to Azure Container Apps — `aspire deploy` should be able to deploy directly to a Kubernetes cluster.

#### Helm vs. YAML vs. Kustomize

We currently generate plain Kubernetes YAML manifests and have Helm chart support. The community has strong opinions about deployment tooling:

- **Helm** — the most popular package manager for Kubernetes. Many teams standardize on Helm charts.
- **Plain YAML + `kubectl apply`** — simple and transparent, preferred by teams that want to understand exactly what's being deployed.
- **Kustomize** — built into `kubectl`, allows environment-specific overlays without templating. Popular for teams managing multiple environments (dev, staging, production).

We need to decide whether to support all three or pick a subset. Helm is likely the highest priority given its ecosystem prevalence, but Kustomize support would be valuable for teams that use environment overlays. Plain YAML is already supported.

**Open question**: Should we support all three, or focus on Helm + plain YAML and let the community contribute Kustomize support?

#### Additional Resource Ergonomics

It is currently quite awkward to specify additional Kubernetes resources that don't map directly to Aspire's resource model. For example:

- **PersistentVolumeClaims** — specifying storage for stateful workloads.
- **Ingress rules** — complex routing, TLS configuration, annotations for cloud-specific ingress controllers.
- **Custom resources (CRDs)** — operators like cert-manager, external-secrets, etc.
- **Network policies** — restricting traffic between pods.
- **RBAC** — service accounts, roles, and bindings beyond what Aspire generates by default.

We need a better story for letting developers express these resources in the app model or attach them to existing resources without dropping down to raw YAML editing after generation.

#### AKS-Specific Support

Azure Kubernetes Service has features that don't exist in vanilla Kubernetes — managed identities, Azure CNI, virtual node scaling, and integration with Azure services like Key Vault via CSI drivers. We should consider a separate `Aspire.Hosting.Azure.AKS` package that layers AKS-specific capabilities on top of the generic Kubernetes support, keeping `Aspire.Hosting.Kubernetes` cloud-agnostic.

#### Community Research

We need to do more research with the community on what is missing from the Kubernetes experience. Specific areas to investigate:

- What Kubernetes deployment patterns are people using today?
- What are the biggest pain points when deploying Aspire apps to Kubernetes?
- How do teams handle secrets management in Kubernetes deployments?
- What ingress controllers and service meshes are most commonly used?
- How do teams manage multi-environment deployments (dev, staging, production)?

### Docker Compose

Docker Compose GA'd in 13.2 and is in good shape. Ongoing work includes:

- Ensuring parity with new Aspire features as they're added.
- Improving the experience for Docker Swarm deployments.
- Better integration with container registries for production-like local development.

### Azure Container Apps & App Service

These targets are mature. Ongoing work is largely maintenance and feature parity:

- Keep up with new Azure Container Apps features (e.g., dynamic sessions, GPU workloads).
- Ensure new Aspire resource types get proper ACA and App Service mappings.
- Address papercuts reported by the community.

## Pillar 2: Pipeline Generation and Maintenance

### The Vision

Today, Aspire can publish and deploy your application, but it doesn't help you set up the CI/CD pipeline that orchestrates those operations. Developers still have to write GitHub Actions workflows, Azure DevOps pipelines, or other CI/CD configurations by hand. This is one of the most tedious and error-prone parts of setting up a new project.

The vision is that pipeline configuration becomes part of the app model. Just as you add a compute environment to tell Aspire *where* to deploy, you add a pipeline environment to tell Aspire *how* to deploy — and Aspire generates the CI/CD configuration for you.

### Conceptual Model

Pipelines are conceptually a resource that influences the deployment of resources within the same app model — just like compute environments. A pipeline environment is an ambient context that pipeline steps integrate with.

Different kinds of pipeline environments exist:

- **GitHub Actions** — our first target, and the one we'll use to validate the abstraction.
- **Azure DevOps (Azure Pipelines)** — second priority, validates that the abstraction generalizes.
- **Community-contributed** — GitLab CI, Jenkins, CircleCI, etc. If we get the abstraction right for GitHub Actions and Azure DevOps, the community should be able to contribute integrations for other systems.

### Programming Model

The programming model mirrors how compute environments work. You add a pipeline environment to the builder, then define workflows, stages, and jobs within it:

```csharp
var ghActions = builder.AddGitHubActions("ci-cd");

var workflow = ghActions.AddWorkflow("deploy");
var buildStage = workflow.AddStage("build");
var buildJob = buildStage.AddJob("build-and-test");
var deployStage = workflow.AddStage("deploy");
var deployJob = deployStage.AddJob("deploy-to-aca");
```

Pipeline steps are then scheduled by referencing the job they should run in:

```csharp
builder.Pipeline.AddStep("build-containers", scheduledBy: buildJob);
builder.Pipeline.AddStep("push-images", scheduledBy: buildJob);
builder.Pipeline.AddStep("deploy-resources", scheduledBy: deployJob);
```

The key insight is that pipeline step APIs don't know anything about jobs specifically. The `buildJob` object implements an interface (e.g., `IPipelineScheduler`) that the pipeline step infrastructure uses to understand how things hang together. This keeps the step infrastructure agnostic of the specific pipeline system.

This also enables cycle detection at the pipeline emission level — if steps have dependencies that form a cycle across jobs, we can detect and report this before generating any YAML.

#### Bringing Your Own Pipeline

Many teams already have existing CI/CD configurations. We need to support a model where developers can tell Aspire about their existing pipeline structure:

```csharp
var existing = builder.AddExistingGitHubActions("my-pipeline")
    .AddExistingWorkflow(".github/workflows/deploy.yml")
    .AddExistingStage("build")
    .AddExistingJob("build-and-test");

builder.Pipeline.AddStep("aspire-publish", scheduledBy: existing.GetJob("build-and-test"));
```

In this model, you're telling Aspire where specific steps are going to run. When executing, the pipeline environment detects the context it's in (which workflow, which stage, which job) and uses that to schedule steps appropriately. Rather than generating a new workflow, Aspire would generate fragments or instructions for what to add to the existing pipeline.

### Split Build / Publish Pipelines

Many DevOps teams enforce a strict separation between build and release:

1. **Build phase** — runs in a sealed environment with no network access. Build the application, build all container images, run tests, and produce artifacts. This eliminates supply chain risk.
2. **Artifact staging** — ship build outputs to a temporary storage area (e.g., pipeline artifacts, a container registry, or a staging blob store).
3. **Release phase(s)** — consume the staged artifacts and deploy them to target environments (dev → staging → production), potentially with approval gates between stages.

For Aspire to support this pattern, we need to:

- **Build the AppHost** as part of the sealed build phase, alongside all container images.
- **Export pipeline state** — the AppHost's understanding of what needs to be deployed, resource configurations, image tags, etc. — into the artifact store.
- **Rehydrate state in later stages** — the deploy steps in the release phase need to reconstruct enough context from the exported artifacts to perform the deployment without re-running the AppHost build.

This is a significant architectural challenge. It means the pipeline execution model needs a concept of "checkpointing" state — snapshotting the app model after the build phase and restoring it in the deploy phase.

#### Tension with Build-Time Configuration

Some frameworks and tools create tension with the sealed build model. For example, Next.js bakes in API URLs at build time when the frontend and backend are hosted at different origins. If you don't know the deployment URL until the release phase, you have a chicken-and-egg problem.

This isn't unique to Aspire — it's a fundamental tension in the sealed build approach — but we should acknowledge it and explore whether Aspire can help mitigate it (e.g., by deferring URL injection to deployment time via environment variables or runtime configuration).

### Output Modes

We envision two types of pipeline output:

#### Batteries-Included Workflows

A complete, ready-to-use workflow file that developers can drop into their repository and have a working CI/CD pipeline immediately:

```yaml
# .github/workflows/aspire-deploy.yml
# Generated by Aspire - edit with caution
name: Deploy Aspire Application
on:
  push:
    branches: [main]
# ... complete workflow definition
```

This is the "just works" experience for new projects and teams that don't have strong opinions about their CI/CD setup.

#### Component Pipeline Fragments

For teams with existing, carefully crafted pipelines, we generate reusable fragments that DevOps engineers can compose into their own workflows:

```yaml
# .github/actions/aspire-build/action.yml
# Generated by Aspire
name: Aspire Build
description: Build and publish Aspire application artifacts
inputs:
  environment:
    description: Target environment
    required: true
# ... reusable action definition
```

This respects the reality that many organizations have "artisanal" pipelines with specific requirements around security scanning, approval gates, compliance steps, etc.

### Pre/Post Hooks

Even with batteries-included workflows, teams often need to inject environment-specific logic:

- **Package repository authentication** — configuring private NuGet feeds, npm registries, etc.
- **Security scanning** — running vulnerability scanners before deployment.
- **Compliance checks** — policy gates required by organizational governance.
- **Notification** — Slack messages, Teams notifications, PagerDuty alerts.

We should support callouts to user-defined pipeline fragments — pre/post hooks — that let teams inject this logic without forking the generated workflow:

```csharp
workflow.AddPreStep("security-scan", new ExistingAction(".github/actions/security-scan"));
workflow.AddPostStep("notify-team", new ExistingAction(".github/actions/notify-slack"));
```

If a hook fragment exists, it's called. If not, it's skipped. This gives teams an escape hatch without requiring them to abandon the generated pipeline entirely.

### Composability and Enterprise Orchestration

In enterprise environments, pipeline systems are often layered. A common pattern within Microsoft:

- **Azure Pipelines** orchestrates the overall CI/CD flow.
- **OneBranch/1ESPT** provides the build infrastructure and compliance framework.
- **Ev2 (Express v2)** handles the actual deployment rollout to Azure regions.

Each layer has its own configuration format, constraints, and capabilities. We would never ship an Ev2 or OneBranch integration out of the box, but whatever abstraction we build must allow these to be layered on top. An enterprise team should be able to build an `Aspire.Hosting.OneBranch` or `Aspire.Hosting.Ev2` package that composes with the base pipeline abstractions.

This means the pipeline environment abstraction needs to be:

- **Extensible** — third parties can create new pipeline environment types.
- **Composable** — a pipeline environment can delegate to or wrap another pipeline environment.
- **Layered** — enterprise-specific concerns can be added without modifying the core abstractions.

### Incremental Approach

This is a large body of work. A possible progression:

1. **Pipeline environment abstraction** — define the core interfaces and programming model.
2. **GitHub Actions: batteries-included output** — generate a complete workflow for a simple deploy-to-ACA scenario.
3. **Split build/deploy support** — artifact export and state rehydration.
4. **GitHub Actions: fragment output** — generate reusable actions for composition.
5. **Pre/post hooks** — support for injection points.
6. **Azure DevOps support** — validate the abstraction generalizes.
7. **Bring-your-own-pipeline** — support for existing pipeline structures.

### Pipeline CLI Commands

We propose two new CLI commands for managing pipeline definitions:

#### `aspire pipeline save`

Writes out the pipeline structure as defined in the app model. This is analogous to `aspire publish` for compute environments — it materializes the pipeline definition (e.g., a GitHub Actions workflow YAML) to disk so it can be committed to the repository.

#### `aspire pipeline verify`

Validates that the pipeline artifacts on disk match what the app model defines. The primary purpose is to fail early in CI — if someone modifies the generated workflow by hand, or if the app model changes but the pipeline files haven't been regenerated, `aspire pipeline verify` fails the pipeline before any real work happens. This is the same pattern as `dotnet format --verify-no-changes` — a cheap check at the start of the pipeline that catches drift before you waste time building and deploying against a stale configuration.

**Bootstrapping challenge**: There's a chicken-and-egg problem with PR merges. If a PR changes the app model in a way that affects the pipeline definition, the verify step in the *existing* pipeline would fail — but the updated pipeline files are part of the same PR. Options to explore:

- Require that `aspire pipeline save` is run locally before committing (like `dotnet format`), so the PR always includes both the app model change and the updated pipeline files.
- Have a separate "pipeline sync check" that runs as a GitHub status check or bot comment rather than as a step inside the pipeline itself.
- Accept that the verify step runs in the pipeline defined by the *base branch*, so it validates the previous contract, and only enforce strict verification on the default branch after merge.

Worth noting: in both GitHub Actions and Azure DevOps, by the time any job agent is running, the pipeline definition is fully materialized — template expressions have been evaluated and the workflow structure is concrete. This means `aspire pipeline verify` running *inside* the pipeline has access to the fully resolved definition, which simplifies validation considerably compared to trying to statically parse YAML with unresolved expressions from disk.

#### Validating Existing Pipeline References

When using the bring-your-own-pipeline model:

```csharp
builder.AddGitHubActions("ci")
    .AddExistingWorkflow(".github/workflows/deploy.yml")
    .AddExistingStage("deploy")
    .AddExistingJob("deploy-to-aca")
    .AddExistingStep("run-aspire-deploy");
```

`aspire pipeline verify` could parse the referenced workflow file and validate that the declared stages, jobs, and steps actually exist. This gives developers a compile-time-like check that their app model references match reality.

**Caveat**: GitHub Actions workflow files can contain template expression syntax (`${{ }}`) that makes static parsing unreliable — a job name might be dynamically constructed, or a step might be conditionally included. We should handle this gracefully: validate what we can, and warn (rather than error) when we encounter expressions that prevent full validation. It may also be worth considering whether there is a subset of validation that is always safe (e.g., file exists, top-level job names are present) vs. deeper structural validation that is best-effort.

## Pillar 3: Make Azure Awesome

Aspire is cloud-agnostic, but the Azure integrations live in this repository and it's our job to maintain them. As the maintainers of Azure functionality for Aspire, we have a dual responsibility: get the abstractions right so that other cloud providers can integrate with Aspire, and take full advantage of Azure's capabilities. In many ways, solving the problems for Azure helps clear the path for others to follow.

### Custom Domain Support

Custom domain support for Azure Container Apps needs to be finalized. Developers should be able to declaratively specify custom domains in the app model and have Aspire handle the DNS validation, certificate provisioning, and binding configuration through the deployment pipeline.

### Per-Region Deployment Resilience

When deploying Azure resources, not all regions have availability for all SKUs and resource types. Today, if a deployment fails because a specific resource isn't available in the target region, the developer has to manually figure out an alternative.

We should explore:

- **Fallback region configuration** — can we specify fallback regions for specific resources and automatically switch when deployment to the primary region fails?
- **Resource-level region overrides** — some resources (e.g., a Cognitive Services model) might need to be in a different region than the rest of the application.
- **Region availability awareness** — can we pre-check region availability before starting deployment to avoid mid-deployment failures?

### AZD Migration and Coexistence

Azure Developer CLI (AZD) and Aspire's deployment capabilities are neither a strict superset nor subset of each other. Many developers deployed their Aspire applications using AZD and now want to take advantage of Aspire's more advanced deployment features.

We need to address:

- **Migration path** — how does a developer move from AZD-based deployment to `aspire deploy`? What breaks? What needs to be reconfigured?
- **Coexistence** — can AZD and Aspire deploy work side by side? Can AZD consume Aspire-generated artifacts? Can Aspire augment an AZD-managed deployment?
- **Feature gap analysis** — what does AZD do that Aspire doesn't, and vice versa? Where should we invest to close gaps vs. where should we recommend AZD?
- **Communication** — clear documentation and guidance for developers on when to use which tool.

### Radius Integration

Radius support is progressing in the background. Radius provides a cloud-agnostic application platform that aligns well with Aspire's model. As this integration matures, we need to ensure it composes well with the rest of the deployment story — particularly compute environments and pipeline generation.

### Azure-Specific Papercuts

There are numerous Azure-specific issues and improvements tracked in the GitHub issue backlog. These range from small UX improvements to missing resource type support. We should systematically triage and address these as part of ongoing maintenance.

## Pillar 4: Agentic Post-Deployment Operations

### The Challenge

Today, after deployment completes, the agent's visibility is limited to build and deployment logs. If something goes wrong in a deployed environment, the developer has to leave the Aspire/agent context and manually investigate using Azure Portal, `kubectl`, or other tools. This breaks the inner loop → deploy → debug cycle that Aspire is trying to streamline.

At the same time, we've all heard the horror stories of agents going rogue — deleting databases, modifying production configurations, scaling down critical services. We must not create a paved path to that situation.

### Safe Inspection Without Destructive Access

The core question is: **can we give agents visibility into deployed environments in a way that is safe by default?**

The operations mechanisms available to an agent are highly dependent on the target compute environment:

- **Azure Container Apps** — we already deploy Log Analytics workspaces, and if App Insights is configured, we could leverage Azure SRE Agent for streamlined operations.
- **Kubernetes** — `kubectl logs`, `kubectl describe`, and read-only access to cluster state.
- **Docker Compose** — `docker compose logs`, `docker inspect`, container health status.

But just deploying these observability tools doesn't immediately help the developer who just approved and deployed their app and needs to understand why it's not working. The agent needs to:

1. **Look at the source code** and understand what operations capabilities are available for the deployed environment.
2. **Discover the pipeline** that deployed the current environment.
3. **Use the operations capabilities** declared in the app model — with those capabilities being what constrain the agent's access.

### Operations in the App Model

We could extend the app model to let developers declare what operations are available for a deployed environment:

```csharp
var app = builder.AddProject<Projects.WebApi>("api")
    .PublishTo(aca)
    .WithOperations(ops =>
    {
        ops.AllowLogStreaming();
        ops.AllowHealthCheck();
        ops.AllowMetricsQuery();
        // Explicitly NOT allowing: restart, scale, delete, config changes
    });
```

The operations declaration serves two purposes:

1. **For the agent** — it defines the boundary of what the agent can do. The agent can query logs and metrics, but it cannot restart the service or modify its configuration.
2. **For the deployment** — it ensures the right infrastructure is provisioned. If you allow log streaming, the deployment provisions the necessary Log Analytics configuration.

### Privilege Levels

Deployment is inherently an elevated action — it creates and modifies infrastructure. But operations (inspecting state, reading logs, querying metrics) isn't necessarily a high-privilege operation. The split looks something like:

- **Read-only operations** (low privilege) — log streaming, metrics queries, health checks, resource state inspection, configuration viewing (secrets redacted). These should be available to agents by default.
- **Mutating operations** (elevated) — restart, scale, configuration changes, secret rotation. These should require explicit JIT (just-in-time) elevation with human approval.
- **Destructive operations** (highest privilege) — delete resources, drop databases, tear down environments. These should never be available to agents through Aspire's operations model.

### Bridging Deploy to Debug

The gap between "deployment completed successfully" and "agent can help debug an issue" needs to be as small as possible. After a deployment completes, the agent should be able to:

- Stream logs from the deployed services.
- Query metrics and traces from the observability infrastructure.
- Check health endpoint status.
- Inspect resource state (running, failed, pending, resource utilization).
- Compare the deployed state against the intended state from the app model.
- Correlate deployment pipeline outputs with runtime behavior.

This requires the deployment process to output enough metadata for the agent to connect back to the deployed environment — endpoint URLs, resource identifiers, credentials for read-only access, etc.

## Cross-Cutting Concerns

### Extensibility and Community

Every pillar has an extensibility dimension. The abstractions we build need to be good enough that:

- Cloud providers can build compute environment packages for their platforms.
- CI/CD vendors can build pipeline environment packages for their systems.
- Enterprise teams can layer their specific requirements on top.
- The community can contribute and maintain integrations without core team involvement.

### Testing Deployment Configurations

As deployment configurations become more complex (pipelines, multi-stage, multi-region), we need a story for testing them. Can we "dry run" a pipeline generation? Can we validate generated Kubernetes manifests against a cluster's API? Can we test that generated GitHub Actions workflows are syntactically valid?

### Documentation and Guidance

Each pillar introduces new concepts and capabilities. We need to invest in documentation that explains not just the API, but the *mental model* — how pipeline environments relate to compute environments, how operations fit into the deployment lifecycle, when to use which output mode, etc.

### Progressive Disclosure

Not every developer needs every feature. A developer deploying a simple app to ACA should have a simple experience. The complexity of pipeline generation, split builds, multi-region deployment, and agentic operations should be available but not required. The app model should support progressive disclosure — start simple, add complexity as needed.

## Open Questions

1. **Kubernetes deployment tooling**: Should we support Helm + plain YAML + Kustomize, or focus on a subset?
2. **Pipeline abstraction boundary**: Where exactly is the line between what the pipeline environment abstraction handles vs. what's delegated to environment-specific packages?
3. **State serialization format**: For split build/deploy, what format do we use to serialize and restore pipeline state?
4. **AZD transition**: Is it a clean migration, long-term coexistence, or eventual convergence?
5. **Agent safety model**: How prescriptive should the default operations policy be? Opt-in to everything, or safe defaults with opt-out?
6. **Pipeline fragment format**: For the component output mode, what's the right granularity? One action per step? Per stage? Per resource?
7. **Existing pipeline detection**: Can Aspire detect an existing pipeline in the repository and automatically switch to fragment/augmentation mode?
8. **Operations discovery**: How does the agent discover operations capabilities at runtime? MCP tools? A well-known metadata endpoint?
9. **Multi-cloud pipeline**: How do we handle pipelines that deploy to multiple cloud providers simultaneously?
10. **Pipeline maintenance**: When the app model changes, how do we update the generated pipeline? Full regeneration? Incremental patches?
