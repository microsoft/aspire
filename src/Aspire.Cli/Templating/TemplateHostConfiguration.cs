// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Resources;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Utils;
using NuGet.Packaging;
using NuGet.Versioning;

namespace Aspire.Cli.Templating;

internal sealed class TemplateHostConfiguration
{
    private readonly Dictionary<string, SymbolInfo> _symbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NuGetPackageVersionValueSource> _valueSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _defaultValueSources = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, NuGetPackageVersionValueSource> ValueSources => _valueSources;
    public IReadOnlyDictionary<string, string> DefaultValueSources => _defaultValueSources;

    /// <summary>
    /// Gets whether this template opts into creating a missing output-root NuGet configuration
    /// when the CLI identity's package sources require one. Defaults to false.
    /// </summary>
    /// <remarks>
    /// This is an Aspire host setting, not a template parameter or a language inference.
    /// Version substitution alone does not request NuGet policy: polyglot AppHosts can use
    /// the CLI SDK version while their integration restore manages package sources separately.
    /// An opt-in never permits overwriting a template-owned configuration.
    /// </remarks>
    public bool CreateNuGetConfig { get; private set; }

    public bool IsHidden(string name) => _symbols.TryGetValue(name, out var symbol) && symbol.IsHidden;

    public string? GetDisplayName(string name) => _symbols.TryGetValue(name, out var symbol) ? symbol.DisplayName : null;

    public static async Task<TemplateHostConfiguration> LoadAsync(
        IEngineEnvironmentSettings settings, ITemplateInfo template, CancellationToken cancellationToken)
    {
        var configuration = new TemplateHostConfiguration();
        if (string.IsNullOrEmpty(template.HostConfigPlace))
        {
            return configuration;
        }

        // The engine selects one host file: aspirecli.host.json, falling back to
        // dotnetcli.host.json. Read that file from the template's mount, never merge them.
        if (!settings.TryGetMountPoint(template.MountPointUri, out var mount) || mount is null)
        {
            throw new FileNotFoundException(null, template.MountPointUri);
        }

        using (mount)
        {
            var file = mount.FileInfo(template.HostConfigPlace)
                ?? throw new FileNotFoundException(null, template.HostConfigPlace);
            try
            {
                using var stream = file.OpenRead();
                using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                }, cancellationToken);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException(TemplatingStrings.InvalidTemplateHostConfigurationFormat);
                }
                // Template-level settings share the selected host file with prompt metadata:
                //   {"createNuGetConfig":true,"symbolInfo":{"AspireVersion":{"isHidden":true}}}
                // Read the opt-in even when symbolInfo is absent. Only JSON Booleans are valid;
                // an omitted setting remains false, including in an authoritative empty host file.
                if (root.TryGetProperty("createNuGetConfig", out var createNuGetConfig))
                {
                    configuration.CreateNuGetConfig = createNuGetConfig.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => throw new JsonException(TemplatingStrings.InvalidTemplateHostConfigurationFormat)
                    };
                }
                if (!root.TryGetProperty("symbolInfo", out var symbols))
                {
                    return configuration;
                }
                if (symbols.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException(TemplatingStrings.InvalidTemplateHostConfigurationFormat);
                }

                // CLI host files use {"symbolInfo":{"Port":{"isHidden":true}}}.
                // The .NET CLI schema also permits the strings "true" and "false".
                foreach (var symbol in symbols.EnumerateObject())
                {
                    if (symbol.Value.ValueKind != JsonValueKind.Object)
                    {
                        throw new JsonException(TemplatingStrings.InvalidTemplateHostConfigurationFormat);
                    }

                    var hidden = false;
                    if (symbol.Value.TryGetProperty("isHidden", out var isHidden))
                    {
                        hidden = isHidden.ValueKind switch
                        {
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.String when isHidden.GetString() == "true" => true,
                            JsonValueKind.String when isHidden.GetString() == "false" => false,
                            _ => throw new JsonException(TemplatingStrings.InvalidTemplateHostConfigurationFormat)
                        };
                    }

                    string? displayName = null;
                    if (symbol.Value.TryGetProperty("displayName", out var label))
                    {
                        if (label.ValueKind != JsonValueKind.String)
                        {
                            throw new JsonException(TemplatingStrings.InvalidTemplateHostConfigurationFormat);
                        }
                        displayName = label.GetString();
                    }
                    configuration._symbols[symbol.Name] = new SymbolInfo(hidden, displayName);
                    if (symbol.Value.TryGetProperty("valueSource", out var valueSource))
                    {
                        configuration._valueSources.Add(symbol.Name, ReadValueSource(valueSource, template, symbol.Name));
                    }
                    if (symbol.Value.TryGetProperty("defaultValueSource", out var defaultValueSource))
                    {
                        if (defaultValueSource.ValueKind != JsonValueKind.String ||
                            string.IsNullOrWhiteSpace(defaultValueSource.GetString()) ||
                            symbol.Value.TryGetProperty("valueSource", out _))
                        {
                            throw new JsonException(string.Format(CultureInfo.CurrentCulture,
                                TemplatingStrings.InvalidTemplatePromptDefault, symbol.Name));
                        }

                        configuration._defaultValueSources.Add(symbol.Name, defaultValueSource.GetString()!);
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand,
                    string.Format(CultureInfo.CurrentCulture, TemplatingStrings.InvalidTemplateHostConfiguration,
                        file.GetDisplayPath(), ex.Message));
            }
        }

        return configuration;
    }

    private static NuGetPackageVersionValueSource ReadValueSource(JsonElement source, ITemplateInfo template, string name)
    {
        // Literal host metadata, not an engine expression:
        // {"kind":"nugetPackageVersion","packageId":"Example","versionRange":"[1.0,2.0)"}
        // Only ordinary string parameters can opt in; SDK identity and source projection
        // keep their separate contracts rather than becoming package-version lookups.
        var parameter = template.ParameterDefinitions.FirstOrDefault(parameter => parameter.Name == name);
        if (parameter is null || parameter.IsName || parameter.Type != "parameter" ||
            !parameter.DataType.Equals("string", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AspireVersion", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AspireNuGetSource", StringComparison.OrdinalIgnoreCase) ||
            source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String ||
            kind.GetString() != "nugetPackageVersion" ||
            !source.TryGetProperty("packageId", out var id) || id.ValueKind != JsonValueKind.String ||
            !PackageIdValidator.IsValidPackageId(id.GetString()!) ||
            !source.TryGetProperty("versionRange", out var versionRange) || versionRange.ValueKind != JsonValueKind.String ||
            !VersionRange.TryParse(versionRange.GetString()!, out var range) ||
            (source.TryGetProperty("allowPrerelease", out var prerelease) &&
                prerelease.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) ||
            (source.TryGetProperty("workingDirectory", out var directory) &&
                (directory.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(directory.GetString()))))
        {
            throw new JsonException(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.InvalidTemplateValueSource, name));
        }

        return new NuGetPackageVersionValueSource(
            id.GetString()!, range,
            source.TryGetProperty("allowPrerelease", out prerelease) && prerelease.ValueKind == JsonValueKind.True,
            source.TryGetProperty("workingDirectory", out directory) ? directory.GetString()! : ".");
    }

    private sealed record SymbolInfo(bool IsHidden, string? DisplayName);
}

internal sealed record NuGetPackageVersionValueSource(
    string PackageId, VersionRange VersionRange, bool AllowPrerelease, string WorkingDirectory);
