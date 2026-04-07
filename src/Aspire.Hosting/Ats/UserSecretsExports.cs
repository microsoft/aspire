#pragma warning disable ASPIREUSERSECRETS001

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Ats;

/// <summary>
/// ATS exports for user secrets operations that require ATS-friendly payloads.
/// </summary>
internal static class UserSecretsExports
{
    /// <summary>
    /// Gets the user secrets manager from the service provider.
    /// </summary>
    /// <param name="serviceProvider">The service provider handle.</param>
    /// <returns>A user secrets manager handle.</returns>
    [AspireExport(Description = "Gets the user secrets manager from the service provider")]
    public static IUserSecretsManager GetUserSecretsManager(this IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return serviceProvider.GetRequiredService<IUserSecretsManager>();
    }

    /// <summary>
    /// Saves state to user secrets from a JSON string.
    /// </summary>
    /// <param name="userSecretsManager">The user secrets manager handle.</param>
    /// <param name="json">The JSON object payload to persist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the state is saved.</returns>
    [AspireExport(Description = "Saves state to user secrets from a JSON string")]
    public static Task SaveStateJson(this IUserSecretsManager userSecretsManager, string json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userSecretsManager);
        ArgumentNullException.ThrowIfNull(json);

        var state = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("The JSON payload must be a JSON object.");

        return userSecretsManager.SaveStateAsync(state, cancellationToken);
    }

    /// <summary>
    /// Gets a secret value if it exists in configuration, or sets it to the provided value if it does not.
    /// </summary>
    /// <param name="userSecretsManager">The user secrets manager handle.</param>
    /// <param name="resourceBuilder">A resource builder from the target application.</param>
    /// <param name="name">The secret name.</param>
    /// <param name="value">The value to persist when the secret is missing.</param>
    [AspireExport(Description = "Gets a secret value if it exists, or sets it to the provided value if it does not")]
    public static void GetOrSetSecret<T>(this IUserSecretsManager userSecretsManager, IResourceBuilder<T> resourceBuilder, string name, string value)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(userSecretsManager);
        ArgumentNullException.ThrowIfNull(resourceBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        userSecretsManager.GetOrSetSecret(resourceBuilder.ApplicationBuilder.Configuration, name, () => value);
    }
}
