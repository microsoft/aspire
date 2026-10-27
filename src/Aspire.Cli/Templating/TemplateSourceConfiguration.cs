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

        foreach (var property in value.EnumerateObject())
        {
            if (property.Name is not ("repository" or "ref" or "path" or "href"))
            {
                throw new TemplateSourceConfigurationException($"{settingPath}.{property.Name}", NewCommandStrings.TemplateSourceUnknownField);
            }
            ReadString(property.Value, $"{settingPath}.{property.Name}");
        }

        if (value.TryGetProperty("href", out var hrefElement))
        {
            foreach (var field in new[] { "repository", "ref", "path" })
            {
                if (value.TryGetProperty(field, out _))
                {
                    throw new TemplateSourceConfigurationException($"{settingPath}.{field}",
                        string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateSourceFieldConflict, $"{settingPath}.href"));
                }
            }

            var href = ReadString(hrefElement, $"{settingPath}.href");
            if (!IsCatalogUrl(href))
            {
                throw new TemplateSourceConfigurationException($"{settingPath}.href", NewCommandStrings.TemplateSourceInvalidHref);
            }
            return new(null, href);
        }

        if (!value.TryGetProperty("repository", out var repositoryElement))
        {
            if (value.TryGetProperty("path", out var localPath))
            {
                var directory = ReadString(localPath, $"{settingPath}.path");
                if (!value.TryGetProperty("ref", out _) && Path.IsPathFullyQualified(directory))
                {
                    return new(new TemplateSource(directory, null, null), null);
                }
                ValidateRepositoryPath(directory, $"{settingPath}.path");
            }
            if (requireComplete)
            {
                throw new TemplateSourceConfigurationException(settingPath, NewCommandStrings.TemplateSourceMissingLocation);
            }
            return new(null, null);
        }

        var repository = ReadString(repositoryElement, $"{settingPath}.repository");
        ValidateRepository(repository, $"{settingPath}.repository");
        var path = value.TryGetProperty("path", out var pathElement) ? ReadString(pathElement, $"{settingPath}.path") : ".";
        ValidateRepositoryPath(path, $"{settingPath}.path");
        var reference = value.TryGetProperty("ref", out var refElement) ? ReadString(refElement, $"{settingPath}.ref") : null;
        return new(new TemplateSource(path, repository, reference), null);
    }

    public static string ReadString(JsonElement value, string settingPath)
    {
        if (value.ValueKind is not JsonValueKind.String ||
            value.GetString() is not { } text ||
            string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl))
        {
            throw new TemplateSourceConfigurationException(settingPath, NewCommandStrings.TemplateSourceStringRequired);
        }
        return text;
    }

    public static string ReadRequiredString(JsonElement value, string field, string settingPath) =>
        value.TryGetProperty(field, out var property)
            ? ReadString(property, $"{settingPath}.{field}")
            : throw new TemplateSourceConfigurationException($"{settingPath}.{field}", NewCommandStrings.TemplateSourceStringRequired);

    public static void ValidateRepository(string repository, string settingPath)
    {
        if (!GitRepositoryUrl.IsValid(repository))
        {
            throw new TemplateSourceConfigurationException(settingPath, NewCommandStrings.TemplateSourceInvalidRepository);
        }
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) || name.Contains('.') || name.Contains(':'))
        {
            throw new TemplateSourceConfigurationException("templates", NewCommandStrings.TemplateSourceNameRequired);
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

    public static string GetPropertyName(JsonObject settings, string key) =>
        settings.Select(property => property.Key).FirstOrDefault(name => name.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;

    public static string ResolveEditKey(JsonObject settings, string key)
    {
        var parts = key.Replace(':', '.').Split('.');
        if (!parts[0].Equals("templates", StringComparison.OrdinalIgnoreCase))
        {
            return key;
        }
        parts[0] = GetPropertyName(settings, "templates");
        if (parts.Length > 1 && settings[parts[0]] is JsonObject sources)
        {
            parts[1] = GetPropertyName(sources, parts[1]);
        }

        return string.Join('.', parts);
    }

    public static string ValidateEditKey(JsonObject settings, string key)
    {
        key = ResolveEditKey(settings, key);
        var parts = key.Replace(':', '.').Split('.');
        if (!parts[0].Equals("templates", StringComparison.OrdinalIgnoreCase))
        {
            return key;
        }
        if (parts.Length < 2)
        {
            throw new TemplateSourceConfigurationException("templates", NewCommandStrings.TemplateSourcesMustBeObject);
        }
        ValidateName(parts[1]);
        if (parts.Length > 3 || (parts.Length == 3 && parts[2] is not ("repository" or "ref" or "path" or "href")))
        {
            throw new TemplateSourceConfigurationException(key.Replace(':', '.'), NewCommandStrings.TemplateSourceUnknownField);
        }
        if (settings[parts[0]] is { } templates && templates is not JsonObject)
        {
            throw new TemplateSourceConfigurationException("templates", NewCommandStrings.TemplateSourcesMustBeObject);
        }
        if (parts.Length == 3 && settings[parts[0]]?[parts[1]] is JsonValue)
        {
            // The general config setter replaces scalar parents with objects. Do not silently
            // discard an existing directory/repository string when adding source fields.
            throw new TemplateSourceConfigurationException($"templates.{parts[1]}",
                string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateSourceCannotExtendString, $"templates.{parts[1]}"));
        }

        return key;
    }

    public static void ValidateEditedSource(JsonObject settings, string key)
    {
        var parts = key.Replace(':', '.').Split('.');
        if (parts[0].Equals("templates", StringComparison.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(settings[parts[0]]![parts[1]]!.ToJsonString());
            Parse(document.RootElement, $"templates.{parts[1]}", requireComplete: false);
        }
    }

    private static bool IsCatalogUrl(string href)
    {
        var queryStart = href.IndexOf('?');
        var address = queryStart < 0 ? href : href[..queryStart];
        var expanded = href.Replace("{search}", "", StringComparison.Ordinal)
            .Replace("{cliVersion}", "", StringComparison.Ordinal);
        // Placeholders are query-only: https://host/index?q={search}&cliVersion={cliVersion}.
        // URL-escaping a search must not let it choose a different server or resource path.
        return !href.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            && !href.Contains('\\') && !address.Contains('{') && !address.Contains('}')
            && !expanded.Contains('{') && !expanded.Contains('}')
            && Uri.TryCreate(expanded, UriKind.Absolute, out var uri)
            && uri.Scheme == "https" && !string.IsNullOrEmpty(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);
    }
}

internal sealed record ConfiguredTemplateSource(TemplateSource? Template, string? Href);

internal sealed class TemplateSourceConfigurationException(string settingPath, string reason)
    : Exception(string.Format(CultureInfo.CurrentCulture, NewCommandStrings.TemplateSourceSettingInvalid, settingPath, reason));
