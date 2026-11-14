// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Identifies an executable resource as a tool.
/// </summary>
/// <remarks>
/// Implement this interface on an <see cref="ExecutableResource"/> to classify it as a tool in
/// resource snapshots and orchestration events. <c>ProjectResource</c>, <c>ContainerResource</c>,
/// and <c>ContainerExecutableResource</c> retain their existing resource kinds. For other executable
/// resources, this marker takes precedence over project metadata annotations.
/// It does not configure tool acquisition or change execution and publishing behavior.
/// </remarks>
[Experimental("ASPIRETOOLRESOURCE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IToolResource : IResource
{
}
