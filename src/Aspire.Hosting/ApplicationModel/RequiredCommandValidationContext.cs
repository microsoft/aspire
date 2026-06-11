// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides context for validating a required command.
/// </summary>
/// <param name="resolvedPath">The resolved full path to the command executable.</param>
/// <param name="services">The service provider for accessing application services.</param>
/// <param name="cancellationToken">A cancellation token that can be used to cancel the validation.</param>
[Experimental("ASPIRECOMMAND001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExport(ExposeProperties = true, ExposeMethods = true)]
public sealed class RequiredCommandValidationContext(string resolvedPath, IServiceProvider services, CancellationToken cancellationToken)
{
    /// <summary>
    /// Gets the resolved full path to the command executable.
    /// </summary>
    public string ResolvedPath { get; } = resolvedPath ?? throw new ArgumentNullException(nameof(resolvedPath));

    /// <summary>
    /// Gets the service provider for accessing application services.
    /// </summary>
    public IServiceProvider Services { get; } = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>
    /// Gets a cancellation token that can be used to cancel the validation.
    /// </summary>
    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    /// <returns>A <see cref="RequiredCommandValidationResult"/> indicating the command is valid.</returns>
    public RequiredCommandValidationResult Success() => RequiredCommandValidationResult.Success();

    /// <summary>
    /// Creates a failed validation result with the specified message.
    /// </summary>
    /// <param name="validationMessage">A message describing why validation failed.</param>
    /// <returns>A <see cref="RequiredCommandValidationResult"/> indicating the command is invalid.</returns>
    public RequiredCommandValidationResult Failure(string validationMessage) => RequiredCommandValidationResult.Failure(validationMessage);
}
