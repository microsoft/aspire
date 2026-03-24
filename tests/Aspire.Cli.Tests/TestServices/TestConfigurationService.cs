// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// Test implementation of IConfigurationService that tracks SetConfigurationAsync, DeleteConfigurationAsync, and GetConfigurationAsync calls.
/// </summary>
public sealed class TestConfigurationService : IConfigurationService
{
    public Action<string, string, bool>? OnSetConfiguration { get; set; }
    public Action<string, bool>? OnDeleteConfiguration { get; set; }
    public Func<string, string?>? OnGetConfiguration { get; set; }

    public Task SetConfigurationAsync(string key, string value, bool isGlobal = false, CancellationToken cancellationToken = default)
    {
        OnSetConfiguration?.Invoke(key, value, isGlobal);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteConfigurationAsync(string key, bool isGlobal = false, CancellationToken cancellationToken = default)
    {
        OnDeleteConfiguration?.Invoke(key, isGlobal);
        return Task.FromResult(true);
    }

    public Task<Dictionary<string, string>> GetAllConfigurationAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Dictionary<string, string>());
    }

    public Task<Dictionary<string, string>> GetLocalConfigurationAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Dictionary<string, string>());
    }

    public Task<Dictionary<string, string>> GetGlobalConfigurationAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Dictionary<string, string>());
    }

    public Task<string?> GetConfigurationAsync(string key, CancellationToken cancellationToken = default)
    {
        var result = OnGetConfiguration?.Invoke(key);
        return Task.FromResult(result);
    }

    public string SettingsFilePath { get; set; } = string.Empty;

    public string GetSettingsFilePath(bool isGlobal)
    {
        return SettingsFilePath;
    }
}
