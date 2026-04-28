// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting;

/// <summary>
/// Defines an interface for managing user secrets with support for read and write operations.
/// </summary>
[AspireExport(ExposeProperties = true, ExposeMethods = true)]
[Experimental("ASPIREUSERSECRETS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IUserSecretsManager
{
    /// <summary>
    /// Gets a value indicating whether user secrets are available.
    /// </summary>
    /// <remarks>
    /// Returns <c>true</c> if the project has a user secrets ID configured; otherwise, <c>false</c>.
    /// </remarks>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets the path to the user secrets file.
    /// </summary>
    string FilePath { get; }

    /// <summary>
    /// Attempts to set a user secret value synchronously.
    /// </summary>
    /// <param name="name">The name of the secret.</param>
    /// <param name="value">The value of the secret.</param>
    /// <returns>True if the secret was set successfully; otherwise, false.</returns>
    [AspireExport(Description = "Attempts to set a user secret value")]
    bool TrySetSecret(string name, string value);

    /// <summary>
    /// Attempts to delete a user secret value synchronously.
    /// </summary>
    /// <param name="name">The name of the secret.</param>
    /// <returns>True if the secret was deleted successfully; otherwise, false.</returns>
    /// <remarks>
    /// The default implementation returns <see langword="false"/> so existing implementations remain compatible.
    /// </remarks>
    [AspireExport(Description = "Attempts to delete a user secret value")]
    bool TryDeleteSecret(string name)
    {
        return false;
    }

    /// <summary>
    /// Gets a secret value if it exists in configuration, or sets it using the value generator if it doesn't.
    /// </summary>
    /// <param name="configuration">The configuration manager to check and update.</param>
    /// <param name="name">The name of the secret.</param>
    /// <param name="valueGenerator">Function to generate the value if it doesn't exist.</param>
    [AspireExportIgnore(Reason = "IConfigurationManager and Func<string> are not ATS-compatible. Use the ATS helper overload that accepts a resource builder and a string value.")]
    void GetOrSetSecret(IConfigurationManager configuration, string name, Func<string> valueGenerator);

    /// <summary>
    /// Saves state to user secrets asynchronously (for deployment state manager).
    /// If multiple callers save state concurrently, the last write wins.
    /// </summary>
    /// <param name="state">The state to save as a JSON object.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [AspireExportIgnore(Reason = "JsonObject is not ATS-compatible. Use the ATS helper overload that accepts a JSON string.")]
    Task SaveStateAsync(JsonObject state, CancellationToken cancellationToken = default);
}
