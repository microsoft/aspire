// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Configuration;

internal interface IConfigurationService
{
    Task SetConfigurationAsync(string key, string value, bool isGlobal = false, CancellationToken cancellationToken = default);
    Task<bool> DeleteConfigurationAsync(string key, bool isGlobal = false, CancellationToken cancellationToken = default);
    Task<Dictionary<string, string>> GetAllConfigurationAsync(CancellationToken cancellationToken = default);
    Task<Dictionary<string, string>> GetLocalConfigurationAsync(CancellationToken cancellationToken = default);
    Task<Dictionary<string, string>> GetGlobalConfigurationAsync(CancellationToken cancellationToken = default);
    Task<string?> GetConfigurationAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a configuration value scoped to a specific directory rather than the
    /// process-wide working directory. The lookup walks upward from
    /// <paramref name="startDirectory"/> for the nearest <c>aspire.config.json</c>
    /// (or legacy <c>.aspire/settings.json</c>); if the key is not present in that file,
    /// falls back to the global settings file. The process-wide
    /// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> (which is rooted at
    /// the working directory the CLI was launched from) is intentionally NOT consulted,
    /// so commands like <c>aspire update --apphost &lt;path&gt;</c> can resolve config
    /// from the project's directory tree instead of the caller's cwd.
    /// </summary>
    /// <remarks>
    /// Throws <see cref="System.InvalidOperationException"/> if a settings file is found
    /// but cannot be parsed as JSON, matching the behavior of startup-time settings load.
    /// </remarks>
    Task<string?> GetConfigurationFromDirectoryAsync(string key, DirectoryInfo startDirectory, CancellationToken cancellationToken = default);
    string GetSettingsFilePath(bool isGlobal);
}
