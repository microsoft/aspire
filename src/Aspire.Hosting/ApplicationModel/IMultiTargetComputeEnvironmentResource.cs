// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Identifies a compute environment that can deploy the same logical compute resource alongside
/// other compute environments.
/// </summary>
/// <remarks>
/// Implementations must create an environment-specific deployment target and resolve endpoint
/// references in the context of that environment.
/// </remarks>
[Experimental("ASPIRECOMPUTE004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IMultiTargetComputeEnvironmentResource : IComputeEnvironmentResource
{
}
