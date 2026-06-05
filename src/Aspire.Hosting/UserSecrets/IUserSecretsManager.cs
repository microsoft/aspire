// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting;

/// <summary>
/// Defines an interface for managing user secrets with support for read and write operations.
/// </summary>
[Experimental("ASPIREUSERSECRETS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IUserSecretsManager
{
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
    bool TrySetSecret(string name, string value);

    /// <summary>
    /// Gets a secret value if it exists in configuration, or sets it using the value generator if it doesn't.
    /// </summary>
    /// <param name="configuration">The configuration manager to check and update.</param>
    /// <param name="name">The name of the secret.</param>
    /// <param name="valueGenerator">Function to generate the value if it doesn't exist.</param>
    void GetOrSetSecret(IConfigurationManager configuration, string name, Func<string> valueGenerator);

    /// <summary>
    /// Saves state to user secrets asynchronously (for deployment state manager).
    /// If multiple callers save state concurrently, the last write wins.
    /// </summary>
    /// <param name="state">The state to save as a JSON object.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveStateAsync(JsonObject state, CancellationToken cancellationToken = default);
}
