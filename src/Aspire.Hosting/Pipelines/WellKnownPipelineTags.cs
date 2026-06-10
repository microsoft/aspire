// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Defines well-known pipeline tags used to categorize pipeline steps.
/// </summary>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class WellKnownPipelineTags
{
    /// <summary>
    /// Tag for steps that provision infrastructure resources.
    /// </summary>
    [AspireValue("WellKnownPipelineTags")]
    public const string ProvisionInfrastructure = "provision-infra";

    /// <summary>
    /// Tag for steps that build compute resources.
    /// </summary>
    [AspireValue("WellKnownPipelineTags")]
    public const string BuildCompute = "build-compute";

    /// <summary>
    /// Tag for steps that push container images to a registry.
    /// </summary>
    [AspireValue("WellKnownPipelineTags")]
    public const string PushContainerImage = "push-container-image";

    /// <summary>
    /// Tag for steps that deploy to compute infrastructure.
    /// </summary>
    [AspireValue("WellKnownPipelineTags")]
    public const string DeployCompute = "deploy-compute";
}
