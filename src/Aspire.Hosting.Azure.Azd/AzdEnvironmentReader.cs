// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Hosting.Azure.Azd;

/// <summary>
/// Reads azd environment state from a project's <c>.azure</c> directory.
/// </summary>
/// <remarks>
/// The on-disk layout written by azd is:
/// <list type="bullet">
/// <item><c>.azure/config.json</c> &#8212; selects the default environment via a <c>defaultEnvironment</c> field.</item>
/// <item><c>.azure/&lt;name&gt;/.env</c> &#8212; a dotenv file holding that environment's values.</item>
/// </list>
/// </remarks>
internal static class AzdEnvironmentReader
{
    /// <summary>
    /// Reads an azd environment from the project's <c>.azure</c> directory.
    /// </summary>
    /// <param name="projectDirectory">The directory that contains <c>azure.yaml</c> (and the <c>.azure</c> folder).</param>
    /// <param name="environmentName">
    /// The environment to load. When <see langword="null"/>, the default environment from
    /// <c>.azure/config.json</c> is used.
    /// </param>
    /// <returns>The loaded <see cref="AzdEnvironment"/>, or <see langword="null"/> when no environment exists.</returns>
    public static AzdEnvironment? Read(string projectDirectory, string? environmentName = null)
    {
        var azureDir = Path.Combine(projectDirectory, ".azure");
        if (!Directory.Exists(azureDir))
        {
            return null;
        }

        var name = environmentName ?? ReadDefaultEnvironmentName(azureDir);
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var envFile = Path.Combine(azureDir, name, ".env");
        var values = File.Exists(envFile)
            ? ParseDotEnv(File.ReadAllLines(envFile))
            : new Dictionary<string, string>();

        return new AzdEnvironment(name, values);
    }

    private static string? ReadDefaultEnvironmentName(string azureDir)
    {
        var configFile = Path.Combine(azureDir, "config.json");
        if (!File.Exists(configFile))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configFile));
            return document.RootElement.TryGetProperty("defaultEnvironment", out var value)
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            // A malformed config.json should not prevent import; the caller can still proceed
            // without environment values.
            return null;
        }
    }

    /// <summary>
    /// Parses a dotenv file as written by azd.
    /// </summary>
    /// <remarks>
    /// azd writes values that are JSON-quoted, for example:
    /// <code>
    /// AZURE_ENV_NAME="dev"
    /// AZURE_LOCATION="eastus2"
    /// # provisioning outputs
    /// SERVICE_WEB_ENDPOINT_URL="https://web.example.com"
    /// </code>
    /// Blank lines and lines beginning with <c>#</c> are ignored. The first <c>=</c> separates key and
    /// value, so values may themselves contain <c>=</c>. Surrounding single or double quotes are removed.
    /// </remarks>
    private static Dictionary<string, string> ParseDotEnv(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            // azd writes values with godotenv, which double-quotes and escapes them much like JSON
            // (\n, \r, \t, \", \\, \uXXXX). Unescape double-quoted values via the JSON string reader so
            // connection strings or values containing quotes/newlines round-trip correctly. Fall back to
            // a plain trim for single-quoted or hand-edited values that are not valid JSON strings.
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                try
                {
                    value = JsonSerializer.Deserialize<string>(value) ?? value;
                }
                catch (JsonException)
                {
                    value = value[1..^1];
                }
            }
            else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            {
                value = value[1..^1];
            }

            values[key] = value;
        }

        return values;
    }
}
