// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines.GitHubActions.Yaml;

namespace Aspire.Hosting.Pipelines.GitHubActions;

/// <summary>
/// Annotation that stores a callback for customizing the generated <see cref="WorkflowYaml"/>
/// before it is serialized to disk.
/// </summary>
internal sealed class WorkflowCustomizationAnnotation(Action<WorkflowYaml> callback) : IResourceAnnotation
{
    /// <summary>
    /// Gets the customization callback.
    /// </summary>
    public Action<WorkflowYaml> Callback { get; } = callback ?? throw new ArgumentNullException(nameof(callback));
}
