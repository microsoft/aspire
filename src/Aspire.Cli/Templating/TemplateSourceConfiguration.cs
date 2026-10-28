// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Git;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Templating;

internal static class TemplateSourceConfiguration
{
    public static ConfiguredTemplateSource Parse(JsonElement value, string settingPath, bool requireComplete)
    {
        if (value.ValueKind is not JsonValueKind.Object)
        {
            throw new TemplateSourceConfigurationException(settingPath, NewCommandStrings.InvalidConfiguredTemplateSource);
        }

        string? path = null;
        string? repository = null;
        string? reference = null;
        foreach (var property in value.EnumerateObject())
        {
            var field = property.Name.ToLowerInvariant();
            if (field is not ("repository" or "ref" or "path"))
            {
                // Ignore fields from newer CLIs, matching typed configuration deserialization.
                continue;
            }
            var text = property.Value.ValueKind is JsonValueKind.Null
                ? null
                : ReadString(property.Value, $"{settingPath}.{property.Name}");
            switch (field)
            {
                case "path":
                    path = text;
                    break;
                case "repository":
                    repository = text;
                    break;
                case "ref":
                    reference = text;
                    break;
            }
        }

        return Parse(path, repository, reference, settingPath, requireComplete);
    }

    public static ConfiguredTemplateSource Parse(
        string? path, string? repository, string? reference, string settingPath, bool requireComplete)
    {
        foreach (var (field, value) in new[] { ("path", path), ("repository", repository), ("ref", reference) })
        {
            if (value is not null)
            {
                ValidateString(value, $"{settingPath}.{field}");
            }
        }

        if (repository is null)
        {
            if (path is not null)
            {
                if (reference is null && Path.IsPathFullyQualified(path))
                {
                    return new(new TemplateSource(path, null, null));
                }
                ValidateRepositoryPath(path, $"{settingPath}.path");
            }
            if (requireComplete)
            {
                throw new TemplateSourceConfigurationException(settingPath, NewCommandStrings.TemplateSourceMissingLocation);
            }
            return new(null);
        }

        ValidateRepository(repository, $"{settingPath}.repository");
        path ??= ".";
        ValidateRepositoryPath(path, $"{settingPath}.path");
        return new(new TemplateSource(path, repository, reference));
    }

    public static string ReadString(JsonElement value, string settingPath)
    {
        if (value.ValueKind is not JsonValueKind.String ||
            value.GetString() is not { } text)
        {
            throw new TemplateSourceConfigurationException(settingPath, NewCommandStrings.TemplateSourceStringRequired);
        }
        ValidateString(text, settingPath);
        return text;
    }

    private static void ValidateString(string value, string settingPath)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            throw new TemplateSourceConfigurationException(settingPath, NewCommandStrings.TemplateSourceStringRequired);
        }
    }

    public static void ValidateRepository(string repository, string settingPath)
    {
        if (!GitHelpers.IsValidRepositoryUrl(repository))
        {
            throw new TemplateSourceConfigurationException(settingPath, NewCommandStrings.TemplateSourceInvalidRepository);
        }
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) || name.Contains('.') || name.Contains(':'))
        {
            throw new TemplateSourceConfigurationException("templateSources", NewCommandStrings.TemplateSourceNameRequired);
        }
    }

    public static void ValidateRepositoryPath(string path, string settingPath)
    {
        try
        {
            TemplateSource.GetRepositoryRelativePath(path, Path.GetFullPath("."));
        }
        catch (Exception ex) when (ex is TemplateDiscoveryException or ArgumentException or NotSupportedException)
        {
            throw new TemplateSourceConfigurationException(settingPath, NewCommandStrings.TemplateRepositoryPathInvalid);
        }
    }

    public static string GetPropertyName(JsonObject settings, string key, StringComparison comparison) =>
        settings.Select(property => property.Key).FirstOrDefault(name => name.Equals(key, comparison)) ?? key;

    public static string ResolveEditKey(JsonObject settings, string key)
    {
        var parts = key.Replace(':', '.').Split('.');
        if (!parts[0].Equals("templateSources", StringComparison.OrdinalIgnoreCase))
        {
            return key;
        }
        parts[0] = GetPropertyName(settings, "templateSources", StringComparison.OrdinalIgnoreCase);
        if (parts.Length > 1 && settings[parts[0]] is JsonObject sources)
        {
            parts[1] = GetPropertyName(sources, parts[1], StringComparisons.TemplateSourceName);
            if (parts.Length > 2 && sources[parts[1]] is JsonObject source)
            {
                parts[2] = GetPropertyName(source, parts[2], StringComparison.OrdinalIgnoreCase);
            }
        }

        return string.Join('.', parts);
    }

    public static string ValidateEditKey(JsonObject settings, string key)
    {
        key = ResolveEditKey(settings, key);
        var parts = key.Replace(':', '.').Split('.');
        if (!parts[0].Equals("templateSources", StringComparison.OrdinalIgnoreCase))
        {
            return key;
        }
        if (parts.Length < 2)
        {
            throw new TemplateSourceConfigurationException("templateSources", NewCommandStrings.TemplateSourcesMustBeObject);
        }
        ValidateName(parts[1]);
        if (parts.Length > 3 || (parts.Length == 3 && parts[2].ToLowerInvariant() is not ("repository" or "ref" or "path")))
        {
            throw new TemplateSourceConfigurationException(key.Replace(':', '.'), NewCommandStrings.TemplateSourceUnknownField);
        }
        if (settings[parts[0]] is { } templates && templates is not JsonObject)
        {
            throw new TemplateSourceConfigurationException("templateSources", NewCommandStrings.TemplateSourcesMustBeObject);
        }
        if (parts.Length == 3 && settings[parts[0]]?[parts[1]] is JsonValue)
        {
            // The general config setter replaces scalar parents with objects. Do not silently
            // discard an existing directory/repository string when adding source fields.
            throw new TemplateSourceConfigurationException($"templateSources.{parts[1]}",
                string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateSourceCannotExtendString, $"templateSources.{parts[1]}"));
        }

        return key;
    }

    public static void ValidateEditedSource(JsonObject settings, string key)
    {
        var parts = key.Replace(':', '.').Split('.');
        if (parts[0].Equals("templateSources", StringComparison.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(settings[parts[0]]![parts[1]]!.ToJsonString());
            Parse(document.RootElement, $"templateSources.{parts[1]}", requireComplete: false);
        }
    }
}

internal sealed record ConfiguredTemplateSource(TemplateSource? Template);

internal sealed class TemplateSourceConfigurationException(string settingPath, string reason)
    : Exception(string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateSourceSettingInvalid, settingPath, reason));
