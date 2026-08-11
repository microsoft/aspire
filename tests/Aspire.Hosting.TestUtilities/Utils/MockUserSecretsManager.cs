// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREUSERSECRETS001

using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Tests.Utils;

internal sealed class MockUserSecretsManager : IUserSecretsManager
{
    private readonly bool _canSetSecret;

    public MockUserSecretsManager(bool canSetSecret = true)
    {
        _canSetSecret = canSetSecret;
    }

    public Dictionary<string, string> Secrets { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAvailable => _canSetSecret;

    public string FilePath => "/mock/path/secrets.json";

    public bool TrySetSecret(string name, string value)
    {
        // Simulate an environment where persistence is unavailable (e.g. user secrets are not
        // enabled): report failure without recording the value so callers exercise their
        // persistence-failure handling.
        if (!_canSetSecret)
        {
            return false;
        }

        Secrets[name] = value;
        return true;
    }

    public bool TryDeleteSecret(string name)
    {
        return Secrets.Remove(name);
    }

    public void GetOrSetSecret(IConfigurationManager configuration, string name, Func<string> valueGenerator)
    {
    }

    public Task SaveStateAsync(JsonObject state, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
