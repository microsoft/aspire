// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Git;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Configuration;

internal interface IConfigSourceGitClient
{
    Task<(string Commit, string Version, JsonObject Configuration)> AcquireAsync(ConfigSource source, string directory, CancellationToken cancellationToken);
    Task<(string Commit, string Version)> CheckAsync(ConfigSource source, string directory, CancellationToken cancellationToken);
}

internal sealed class ConfigSourceGitClient(IGitClient git, TimeProvider timeProvider) : IConfigSourceGitClient
{
    private static readonly TimeSpan s_gitTimeout = TimeSpan.FromSeconds(30);

    public async Task<(string Commit, string Version, JsonObject Configuration)> AcquireAsync(ConfigSource source, string directory, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(s_gitTimeout, timeProvider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var (repositoryDirectory, commit, version) = await FetchVersionAsync(source, directory, allowInteractive: true, timeout.Token);
            var size = await git.GetObjectSizeAsync(repositoryDirectory, version, GitCommandMode.Interactive, timeout.Token);
            if (size is null or > 4 * 1024 * 1024)
            {
                throw new ConfigSourceException(ConfigSourceStrings.InvalidConfiguration);
            }

            var json = await git.ReadBlobAsync(repositoryDirectory, version, GitCommandMode.Interactive, timeout.Token);
            try
            {
                var configuration = JsonNode.Parse(json, documentOptions: ConfigurationHelper.ParseOptions) as JsonObject
                    ?? throw new ConfigSourceException(ConfigSourceStrings.InvalidConfiguration);
                // Validate the known settings as well as JSON syntax. Unknown content settings
                // are retained for future schemas, but a source cannot enable another source.
                if (configuration.Any(p => p.Key.Equals(ConfigSource.SectionName, StringComparison.OrdinalIgnoreCase)
                    || p.Key.StartsWith(ConfigSource.SectionName + ":", StringComparison.OrdinalIgnoreCase)
                    || p.Key.StartsWith(ConfigSource.SectionName + ".", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ConfigSourceException(ConfigSourceStrings.NestedSource);
                }
                // Use the same root-level flat-key compatibility as local settings before
                // validating duplicate paths and typed values such as "features:enabled".
                ConfigurationHelper.NormalizeSettings(configuration);
                using var parsed = new ConfigurationManager();
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(configuration.ToJsonString()));
                parsed.AddJsonStream(stream);
                _ = configuration.Deserialize(JsonSourceGenerationContext.Default.AspireConfigFile);
                return (commit, version, configuration);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
            {
                throw new ConfigSourceException(ConfigSourceStrings.InvalidConfiguration);
            }
        }
        catch (GitCommandException ex)
        {
            throw TranslateGitFailure(ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConfigSourceException(ConfigSourceStrings.GitTimeout)
            {
                GitOutput = (ex as GitProcessCanceledException)?.Output
            };
        }
    }

    public async Task<(string Commit, string Version)> CheckAsync(ConfigSource source, string directory, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(s_gitTimeout, timeProvider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var (_, commit, version) = await FetchVersionAsync(source, directory, allowInteractive: false, timeout.Token);
            return (commit, version);
        }
        catch (GitCommandException ex)
        {
            throw TranslateGitFailure(ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConfigSourceException(ConfigSourceStrings.GitTimeout)
            {
                GitOutput = (ex as GitProcessCanceledException)?.Output
            };
        }
    }

    private async Task<(GitRepositoryLocation Directory, string Commit, string Version)> FetchVersionAsync(
        ConfigSource source, string directory, bool allowInteractive, CancellationToken cancellationToken)
    {
        var repositoryDirectory = new GitRepositoryLocation(new DirectoryInfo(Path.Combine(directory, "repository.git")), IsBare: true);
        await git.InitializeAsync(new DirectoryInfo(directory), repositoryDirectory, objectFormat: null, GitCommandMode.Interactive, cancellationToken);
        // Fetch accepts both a fully qualified branch ref and a full commit ID. Unlike
        // clone --branch, this also acquires commits outside the default branch's shallow history.
        // https://git-scm.com/docs/git-fetch
        await git.FetchAsync(repositoryDirectory, source.Repository, source.GetGitRef(),
            allowInteractive ? GitCommandMode.Interactive : GitCommandMode.Unattended, cancellationToken);
        var commit = await git.GetCommitAsync(repositoryDirectory, "FETCH_HEAD^{commit}", GitCommandMode.Interactive, cancellationToken);
        var entry = await git.GetFileAsync(repositoryDirectory, commit, source.Path, GitCommandMode.Interactive, cancellationToken);
        if (entry is not { Mode: "100644" or "100755", Type: "blob" })
        {
            throw new ConfigSourceException(ConfigSourceStrings.MissingFile);
        }

        return (repositoryDirectory, commit, entry.ObjectId);
    }

    private static ConfigSourceException TranslateGitFailure(GitCommandException exception) =>
        new(exception.Failure switch
        {
            GitFailure.Unavailable => ConfigSourceStrings.GitNotFound,
            GitFailure.AuthenticationFailed => ConfigSourceStrings.AuthenticationFailed,
            _ => ConfigSourceStrings.RepositoryUnavailable
        });
}
