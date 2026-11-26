// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Aspire.Cli.Configuration;

internal interface IConfigSourceDetector
{
    Task<bool> IsMicrosoftGitHubHostConfiguredAsync(CancellationToken cancellationToken);
}

internal sealed class ConfigSourceDetector(
    IEnvironment environment,
    ILogger<ConfigSourceDetector> logger) : IConfigSourceDetector
{
    private const string MicrosoftGitHubHost = "msft.ghe.com";

    public async Task<bool> IsMicrosoftGitHubHostConfiguredAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // gh includes GH_HOST in its known hosts even without a stored account or token.
        // Only host configuration is relevant here; Git still authenticates source access.
        // https://github.com/cli/go-gh/blob/v2.17.0/pkg/auth/auth.go
        if (string.Equals(environment.GetEnvironmentVariable("GH_HOST"), MicrosoftGitHubHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (GetHostsFilePath() is not { } hostsFilePath)
        {
            logger.LogDebug("GitHub CLI host configuration directory could not be resolved from the environment.");
            return false;
        }

        try
        {
            var configuration = await File.ReadAllTextAsync(hostsFilePath, cancellationToken);
            using var reader = new StringReader(configuration);
            var yaml = new YamlStream();
            yaml.Load(reader);

            // hosts.yml contains a top-level host map, including when tokens live in the keyring:
            //   msft.ghe.com:
            //     user: example
            //     users:
            //       example: {}
            // Inspect keys, not account values or tokens; nested names are not configured hosts.
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode hosts)
            {
                logger.LogDebug("GitHub CLI host configuration is not a single YAML mapping.");
                return false;
            }

            return hosts.Children.Keys.OfType<YamlScalarNode>()
                .Any(host => string.Equals(host.Value, MicrosoftGitHubHost, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            logger.LogDebug("GitHub CLI host configuration was not found.");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlException or ArgumentException or NotSupportedException)
        {
            // YAML errors can include configuration values, including plaintext tokens.
            logger.LogDebug("Could not read GitHub CLI host configuration ({FailureType}).", ex.GetType().Name);
            return false;
        }
    }

    private string? GetHostsFilePath()
    {
        // Follow gh's config-directory precedence, but only read from absolute directories
        // supplied by the environment. gh falls back to the working directory when no home
        // variable is set and resolves relative paths against it; reading a hosts.yml from an
        // arbitrary invocation directory could select the Microsoft source from untrusted
        // content, so those cases disable detection instead.
        // https://github.com/cli/go-gh/blob/v2.17.0/pkg/config/config.go
        string? directory;
        if (environment.GetEnvironmentVariable("GH_CONFIG_DIR") is { Length: > 0 } configDirectory)
        {
            directory = configDirectory;
        }
        else if (environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdgConfigHome)
        {
            directory = Path.Combine(xdgConfigHome, "gh");
        }
        else if (environment.IsWindows() && environment.GetEnvironmentVariable("AppData") is { Length: > 0 } appData)
        {
            directory = Path.Combine(appData, "GitHub CLI");
        }
        else if (environment.GetEnvironmentVariable(environment.IsWindows() ? "USERPROFILE" : "HOME") is { Length: > 0 } home)
        {
            directory = Path.Combine(home, ".config", "gh");
        }
        else
        {
            directory = null;
        }

        return directory is not null && Path.IsPathFullyQualified(directory)
            ? Path.Combine(directory, "hosts.yml")
            : null;
    }
}
