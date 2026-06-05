// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// Test implementation of IConfigurationService that tracks SetConfigurationAsync and GetConfigurationAsync calls.
/// </summary>
public sealed class TrackingConfigurationService : IConfigurationService
{
    public Action<string, string, bool>? OnSetConfiguration { get; set; }
    public Func<string, string?>? OnGetConfiguration { get; set; }

    public Task SetConfigurationAsync(string key, string value, bool isGlobal = false, CancellationToken cancellationToken = default)
    {
        OnSetConfiguration?.Invoke(key, value, isGlobal);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteConfigurationAsync(string key, bool isGlobal = false, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }

    public Task<Dictionary<string, string>> GetAllConfigurationAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Dictionary<string, string>());
    }

    public Task<string?> GetConfigurationAsync(string key, CancellationToken cancellationToken = default)
    {
        var result = OnGetConfiguration?.Invoke(key);
        return Task.FromResult(result);
    }

    public string GetSettingsFilePath(bool isGlobal)
    {
        return string.Empty;
    }
}
