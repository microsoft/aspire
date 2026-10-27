// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Templating;

internal sealed class TemplateSourceStore(
    IConfigurationService configurationService,
    CliExecutionContext executionContext,
    ILogger<TemplateSourceStore> logger)
{
    public async Task AddAsync(string name, JsonObject source, CancellationToken cancellationToken)
    {
        TemplateSourceConfiguration.ValidateName(name);
        using var document = JsonDocument.Parse(source.ToJsonString());
        TemplateSourceConfiguration.Parse(document.RootElement, $"templates.{name}", requireComplete: true);

        var path = configurationService.GetSettingsFilePath(isGlobal: true);
        using var configLock = await ConfigurationHelper.TryAcquireWorkspaceConfigLockAsync(
            executionContext, new FileInfo(path), logger, cancellationToken);
        var settings = await ReadAsync(path, cancellationToken);
        var sources = GetSources(settings);
        // IConfiguration keys are case-insensitive even though JSON properties are not.
        // Do not create a global file that the configuration provider cannot load.
        if (sources.Any(entry => entry.Key.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new TemplateSourceConfigurationException($"templates.{name}", NewCommandStrings.TemplateSourceAlreadyExists);
        }
        sources[name] = source.DeepClone();
        await ConfigurationHelper.WriteSettingsFileAtomicallyAsync(path, settings, cancellationToken);
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        TemplateSourceConfiguration.ValidateName(name);
        var path = configurationService.GetSettingsFilePath(isGlobal: true);
        using var configLock = await ConfigurationHelper.TryAcquireWorkspaceConfigLockAsync(
            executionContext, new FileInfo(path), logger, cancellationToken);
        var settings = await ReadAsync(path, cancellationToken);
        var sources = GetSources(settings);
        var existing = sources.Select(entry => entry.Key).FirstOrDefault(key => key.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return false;
        }
        sources.Remove(existing);
        if (sources.Count == 0)
        {
            settings.Remove("templates");
        }
        await ConfigurationHelper.WriteSettingsFileAtomicallyAsync(path, settings, cancellationToken);
        return true;
    }

    private static JsonObject GetSources(JsonObject settings)
    {
        if (settings.TryGetPropertyValue("templates", out var value))
        {
            return value as JsonObject
                ?? throw new TemplateSourceConfigurationException("templates", NewCommandStrings.TemplateSourcesMustBeObject);
        }
        var sources = new JsonObject();
        settings["templates"] = sources;
        return sources;
    }

    private static async Task<JsonObject> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonNode.Parse(content, nodeOptions: null, ConfigurationHelper.ParseOptions) as JsonObject
                ?? throw new TemplateSourceConfigurationException("templates", NewCommandStrings.TemplateSourcesMustBeObject);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new JsonObject();
        }
    }
}
