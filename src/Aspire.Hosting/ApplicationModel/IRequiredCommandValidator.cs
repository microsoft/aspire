// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A service that validates required commands/executables are available on the local machine.
/// </summary>
/// <remarks>
/// This service coalesces validations so that the same command is only validated once,
/// even if multiple resources require it.
/// </remarks>
[Experimental("ASPIRECOMMAND001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IRequiredCommandValidator
{
    /// <summary>
    /// Validates that a required command is available and meets any custom validation requirements.
    /// </summary>
    /// <param name="resource">The resource that requires the command.</param>
    /// <param name="annotation">The annotation describing the required command.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="RequiredCommandValidationResult"/> indicating whether validation succeeded.</returns>
    /// <remarks>
    /// Validations are coalesced per command. If the same command has already been validated,
    /// the cached result is used. If validation fails, a warning is logged but the resource
    /// is allowed to attempt to start.
    /// </remarks>
    Task<RequiredCommandValidationResult> ValidateAsync(IResource resource, RequiredCommandAnnotation annotation, CancellationToken cancellationToken);
}
