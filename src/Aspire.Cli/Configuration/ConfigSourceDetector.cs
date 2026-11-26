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
    CliExecutionContext executionContext,
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

        try
        {
            var configuration = await File.ReadAllTextAsync(GetHostsFilePath(), cancellationToken);
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

    private string GetHostsFilePath()
    {
        // Match gh's config-directory precedence without expanding literal '~' values.
        // https://github.com/cli/go-gh/blob/v2.17.0/pkg/config/config.go
        string directory;
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
        else
        {
            var home = environment.GetEnvironmentVariable(environment.IsWindows() ? "USERPROFILE" : "HOME")
                is { Length: > 0 } configuredHome ? configuredHome : executionContext.HomeDirectory.FullName;
            directory = Path.Combine(home, ".config", "gh");
        }

        return Path.GetFullPath(Path.Combine(directory, "hosts.yml"), executionContext.WorkingDirectory.FullName);
    }
}
