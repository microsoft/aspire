// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides build-only environment variables for a .NET program.
/// </summary>
/// <remarks>
/// Values supplied through this contract affect MSBuild evaluation and may appear in build diagnostics. They are
/// not a secret transport. Implementations are evaluated in registration order for each publish build.
/// </remarks>
[Experimental("ASPIREPROJECTS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IDotnetProgramBuildEnvironmentProvider : IResourceAnnotation
{
    /// <summary>
    /// Applies build-only environment variables to the supplied context.
    /// </summary>
    /// <param name="context">The build environment callback context.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task ApplyAsync(EnvironmentCallbackContext context);
}
