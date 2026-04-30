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
    /// Aggregation step for all publish operations.
    /// All publish steps should be required by this step.
    /// </summary>
    [AspireValue("WellKnownPipelineSteps")]
    public const string Publish = "publish";

    /// <summary>
    /// The prerequisite step that runs before any publish operations.
    /// </summary>
    [AspireValue("WellKnownPipelineSteps")]
    public const string PublishPrereq = "publish-prereq";

    /// <summary>
    /// Aggregation step for all deploy operations.
    /// All deploy steps should be required by this step.
    /// </summary>
    [AspireValue("WellKnownPipelineSteps")]
    public const string Deploy = "deploy";

    /// <summary>
    /// The prerequisite step that runs before any deploy operations.
    /// </summary>
    [AspireValue("WellKnownPipelineSteps")]
    public const string DeployPrereq = "deploy-prereq";

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
    public const string BeforeStart = "before-start";

    /// <summary>
    /// The step that checks whether the container runtime (e.g., Docker or Podman) is running.
    /// Build steps that need a container runtime should depend on this step.
    /// </summary>
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
