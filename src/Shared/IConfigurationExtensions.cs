// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;

namespace Aspire;

internal static class IConfigurationExtensions
{
#if !CLI
    public static T GetValue<T>(this IConfiguration configuration, string primaryKey, string secondaryKey, T defaultValue)
    {
        var primaryValue = configuration.GetValue(typeof(T), primaryKey, null);
        if (primaryValue is not null)
        {
            return (T)primaryValue;
        }

        var secondaryValue = configuration.GetValue(typeof(T), secondaryKey, null);
        if (secondaryValue is not null)
        {
            return (T)secondaryValue;
        }

        return defaultValue;
    }
#endif

    public static bool? GetBool(this IConfiguration configuration, string primaryKey, string secondaryKey)
    {
        var value = configuration.GetBool(primaryKey) ?? configuration.GetBool(secondaryKey);
        return value;
    }

    public static string? GetString(this IConfiguration configuration, string primaryKey, string secondaryKey, bool fallbackOnEmpty = false)
    {
        var primaryValue = configuration[primaryKey];
        if (primaryValue is not null && !fallbackOnEmpty || primaryValue is { Length: > 0 })
        {
            return primaryValue;
        }

        var secondaryValue = configuration[secondaryKey];
        if (secondaryValue is not null && !fallbackOnEmpty || secondaryValue is { Length: > 0 })
        {
            return secondaryValue;
        }

        return null;
    }

    /// <summary>
    /// Gets the named configuration value as a boolean.
    /// </summary>
    /// <remarks>
    /// Parses <c>true</c> and <c>false</c>, along with integer values (where non-zero is <see langword="true"/>).
    /// </remarks>
    /// <param name="configuration">The <see cref="IConfiguration"/> this method extends.</param>
    /// <param name="key">The configuration key.</param>
    /// <returns>The parsed value, or <see langword="null"/> if no value exists or it couldn't be parsed.</returns>
    public static bool? GetBool(this IConfiguration configuration, string key)
    {
        var value = configuration[key];

        if (value is null or [])
        {
            return null;
        }
        else if (bool.TryParse(value, out var b))
        {
            return b;
        }
        else if (int.TryParse(value, out var i))
        {
            return i != 0;
        }

        return null;
    }

    /// <summary>
    /// Gets the named configuration value as a boolean.
    /// </summary>
    /// <remarks>
    /// Parses <c>true</c> and <c>false</c>, along with <c>1</c> and <c>0</c>.
    /// </remarks>
    /// <param name="configuration">The <see cref="IConfiguration"/> this method extends.</param>
    /// <param name="key">The configuration key.</param>
    /// <param name="defaultValue">A default value, for when the configuration value is unspecified or white space.</param>
    /// <returns></returns>
    public static bool GetBool(this IConfiguration configuration, string key, bool defaultValue)
    {
        return configuration.GetBool(key) ?? defaultValue;
    }

    /// <summary>
    /// Parses a configuration value into a <see cref="Uri"/> object.
    /// </summary>
    /// <param name="configuration">The <see cref="IConfiguration"/> this method extends.</param>
    /// <param name="key">The configuration key.</param>
    /// <param name="defaultValue">A default value, for when the configuration value is unspecified or white space. May be <see langword="null"/>.</param>
    /// <returns>The parsed value, or the default value if specified and parsing failed. Returns <see langword="null"/> if <paramref name="defaultValue"/> is <see langword="null"/> and parsing failed.</returns>
    /// <exception cref="InvalidOperationException">The configuration value could not be accessed, or contained incorrectly formatted data.</exception>
    [return: NotNullIfNotNull(nameof(defaultValue))]
    public static Uri? GetUri(this IConfiguration configuration, string key, Uri? defaultValue = null)
    {
        try
        {
            var uri = configuration[key];

            if (string.IsNullOrWhiteSpace(uri))
            {
                return defaultValue;
            }
            else
            {
                return new Uri(uri, UriKind.Absolute);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Error parsing URIs from configuration value '{key}'.", ex);
        }
    }

    /// <summary>
    /// Parses a configuration value's semicolon-delimited value into an array of <see cref="Uri"/> objects.
    /// </summary>
    /// <param name="configuration">The <see cref="IConfiguration"/> this method extends.</param>
    /// <param name="key">The configuration key.</param>
    /// <param name="defaultValue">A default value, for when the configuration value is unspecified or white space. May be <see langword="null"/>.</param>
    /// <returns>The parsed values, or the default value if specified and parsing failed. Returns <see langword="null"/> if <paramref name="defaultValue"/> is <see langword="null"/> and parsing failed.</returns>
    /// <exception cref="InvalidOperationException">The configuration value could not be accessed, or contained incorrectly formatted data.</exception>
    [return: NotNullIfNotNull(nameof(defaultValue))]
    public static Uri[]? GetUris(this IConfiguration configuration, string key, Uri? defaultValue = null)
    {
        try
        {
            var uris = configuration[key];

            if (string.IsNullOrWhiteSpace(uris))
            {
                return defaultValue switch
                {
                    not null => [defaultValue],
                    null => null
                };
            }
            else
            {
                return uris
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(url => new Uri(url, UriKind.Absolute))
                    .ToArray();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Error parsing URIs from configuration value '{key}'.", ex);
        }
    }

    /// <summary>
    /// Gets the named configuration value as a member of an enum, or <paramref name="defaultValue"/> if the value was null or empty.
    /// </summary>
    /// <remarks>
    /// Parsing is case-insensitive.
    /// </remarks>
    /// <param name="configuration">The <see cref="IConfiguration"/> this method extends.</param>
    /// <param name="key">The configuration key.</param>
    /// <param name="defaultValue">A default value, for when the configuration value is unable to be parsed.</param>
    /// <exception cref="InvalidOperationException">The configuration value is not a valid member of the enum.</exception>
    /// <returns>The parsed enum member, or <paramref name="defaultValue"/> the configuration value was null or empty.</returns>
    [return: NotNullIfNotNull(nameof(defaultValue))]
    public static T? GetEnum<T>(this IConfiguration configuration, string key, T? defaultValue = default)
        where T : struct
    {
        var value = configuration[key];

        if (value is null or [])
        {
            return defaultValue;
        }
        else if (Enum.TryParse<T>(value, ignoreCase: true, out var e))
        {
            return e;
        }

        throw new InvalidOperationException($"Unknown {typeof(T).Name} value \"{value}\". Valid values are {string.Join(", ", Enum.GetNames(typeof(T)))}.");
    }

    /// <summary>
    /// Gets the specified required configuration value as a member of an enum.
    /// </summary>
    /// <remarks>
    /// Parsing is case-insensitive.
    /// </remarks>
    /// <param name="configuration">The <see cref="IConfiguration"/> this method extends.</param>
    /// <param name="key">The configuration key.</param>
    /// <exception cref="InvalidOperationException">The configuration value is empty or not a valid member of the enum.</exception>
    /// <returns>The parsed enum member.</returns>
    public static T GetEnum<T>(this IConfiguration configuration, string key)
        where T : struct
    {
        var value = configuration.GetEnum<T>(key, defaultValue: null);

        if (value is null)
        {
            throw new InvalidOperationException($"Missing required configuration for {key}. Valid values are {string.Join(", ", Enum.GetNames(typeof(T)))}.");
        }

        return value.Value;
    }

    /// <summary>
    /// Gets a configuration value with support for dash-to-underscore normalization.
    /// First tries the exact configuration key, then tries with dashes replaced by underscores.
    /// </summary>
    /// <remarks>
    /// This supports command-line arguments and environment variables where dashes are replaced with underscores.
    /// For example, a parameter named "my-param" can be resolved from configuration key "my_param".
    /// </remarks>
    /// <param name="configuration">The <see cref="IConfiguration"/> this method extends.</param>
    /// <param name="configKey">The configuration key to look up.</param>
    /// <returns>The configuration value, or <see langword="null"/> if not found.</returns>
    public static string? GetValueWithNormalizedKey(this IConfiguration configuration, string configKey)
    {
        // First try to get the value with the exact configuration key
        var value = configuration[configKey];

        // If not found, try with underscores as a fallback
        if (string.IsNullOrEmpty(value))
        {
            var normalizedKey = configKey.Replace("-", "_", StringComparison.Ordinal);
            value = configuration[normalizedKey];
        }

        return value;
    }
}
