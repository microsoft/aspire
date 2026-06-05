// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREUSERSECRETS001

using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.UserSecrets;

/// <summary>
/// A no-op implementation of <see cref="IUserSecretsManager"/> used when user secrets are not configured for a project.
/// </summary>
internal sealed class NoopUserSecretsManager : IUserSecretsManager
{
    public static readonly NoopUserSecretsManager Instance = new();

    private NoopUserSecretsManager()
    {
    }

    public bool IsAvailable => false;

    public string FilePath => string.Empty;

    public bool TrySetSecret(string name, string value)
    {
        Debug.WriteLine($"User secrets are not enabled. Cannot set secret '{name}'.");
        return false;
    }

    public bool TryDeleteSecret(string name)
    {
        Debug.WriteLine($"User secrets are not enabled. Cannot delete secret '{name}'.");
        return false;
    }

    public void GetOrSetSecret(IConfigurationManager configuration, string name, Func<string> valueGenerator)
    {
        Debug.WriteLine($"User secrets are not enabled. Generating and adding secret '{name}' to configuration in-memory.");
        var value = valueGenerator();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [name] = value });
    }

    public Task SaveStateAsync(JsonObject state, CancellationToken cancellationToken = default)
    {
        Debug.WriteLine("User secrets are not enabled. Cannot save state.");
        return Task.CompletedTask;
    }
}
