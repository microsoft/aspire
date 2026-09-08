// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Defines well-known pipeline step names used in the deployment pipeline.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class WellKnownPipelineSteps
{
    /// <summary>
    /// The final aggregate step for the publish command.
    /// </summary>
    /// <remarks>
    /// Normal publish work should be required by <see cref="PublishFinalize"/>.
    /// Post-finalize hooks should depend on <see cref="PublishFinalize"/> and be required by this step.
    /// Existing integrations may continue to attach work directly to this step; those legacy attachments
    /// remain direct aggregate dependencies. This step completes after the finalizer, post-finalize hooks,
    /// and legacy direct attachments.
    /// </remarks>
    [AspireValue("WellKnownPipelineSteps")]
    public const string Publish = "publish";

    /// <summary>
    /// The prerequisite step that runs before any publish operations.
    /// </summary>
    [AspireValue("WellKnownPipelineSteps")]
    public const string PublishPrereq = "publish-prereq";

    /// <summary>
    /// The synchronization step that runs after the publish prerequisite and all normal publish operations.
    /// </summary>
    /// <remarks>
    /// Normal publish work should be required by this step. The <see cref="Publish"/> aggregate depends on
    /// this step so post-finalize hooks can run after normal work and before final command completion.
    /// </remarks>
    [AspireValue("WellKnownPipelineSteps")]
    public const string PublishFinalize = "publish-finalize";

    /// <summary>
    /// The final aggregate step for the deploy command.
    /// </summary>
    /// <remarks>
    /// Normal deploy work should be required by <see cref="DeployFinalize"/>.
    /// Post-finalize hooks should depend on <see cref="DeployFinalize"/> and be required by this step.
    /// Existing integrations may continue to attach work directly to this step; those legacy attachments
    /// remain direct aggregate dependencies. This step completes after the finalizer, post-finalize hooks,
    /// and legacy direct attachments.
    /// </remarks>
    [AspireValue("WellKnownPipelineSteps")]
    public const string Deploy = "deploy";

    /// <summary>
    /// The prerequisite step that runs before any deploy operations.
    /// </summary>
    [AspireValue("WellKnownPipelineSteps")]
    public const string DeployPrereq = "deploy-prereq";

    /// <summary>
    /// The synchronization step that runs after the deploy prerequisite and all normal deploy operations.
    /// </summary>
    /// <remarks>
    /// Normal deploy work should be required by this step. The <see cref="Deploy"/> aggregate depends on
    /// this step so post-finalize hooks can run after normal work and before final command completion.
    /// </remarks>
    [AspireValue("WellKnownPipelineSteps")]
    public const string DeployFinalize = "deploy-finalize";

    /// <summary>
    /// The step that prompts for parameter values before build, publish, or deployment operations.
    /// </summary>
    [AspireValue("WellKnownPipelineSteps")]
    public const string ProcessParameters = "process-parameters";

    /// <summary>
    /// The well-known step for building resources.
    /// </summary>
    [AspireValue("WellKnownPipelineSteps")]
    public const string Build = "build";

    /// <summary>
    /// The prerequisite step that runs before any build operations.
    /// </summary>
    [AspireValue("WellKnownPipelineSteps")]
    public const string BuildPrereq = "build-prereq";

    /// <summary>
    /// The meta-step that coordinates all push operations.
    /// All push steps should be required by this step.
    /// </summary>
    [AspireValue("WellKnownPipelineSteps")]
    public const string Push = "push";

    /// <summary>
    /// The prerequisite step that runs before any push operations.
    /// </summary>
    [AspireValue("WellKnownPipelineSteps")]
    public const string PushPrereq = "push-prereq";

    /// <summary>
    /// The diagnostic step that dumps dependency graph information for troubleshooting.
    /// </summary>
    [AspireValue("WellKnownPipelineSteps")]
    public const string Diagnostics = "diagnostics";

    /// <summary>
    /// The step that validates compute resources are assigned to unambiguous compute environments.
    /// </summary>
    [AspireValue("WellKnownPipelineSteps")]
    public const string ValidateComputeEnvironments = "validate-compute-environments";

    /// <summary>
    /// The step that runs before the application starts.
    /// </summary>
    [AspireValue("WellKnownPipelineSteps")]
    public const string BeforeStart = "before-start";

    /// <summary>
    /// The step that checks whether the container runtime (e.g., Docker or Podman) is running.
    /// Build steps that need a container runtime should depend on this step.
    /// </summary>
    [AspireValue("WellKnownPipelineSteps")]
    public const string CheckContainerRuntime = "check-container-runtime";

    /// <summary>
    /// Aggregation step for all destroy operations.
    /// All destroy steps should be required by this step.
    /// </summary>
    [AspireValue("WellKnownPipelineSteps")]
    public const string Destroy = "destroy";

    /// <summary>
    /// The prerequisite step that runs before any destroy operations.
    /// </summary>
    [AspireValue("WellKnownPipelineSteps")]
    public const string DestroyPrereq = "destroy-prereq";
}
