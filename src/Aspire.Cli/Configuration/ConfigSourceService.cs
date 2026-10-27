// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Commands;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Aspire.Shared.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Configuration;

internal sealed class ConfigSourceService(
    CliExecutionContext executionContext,
    IConfigurationService configurationService,
    IEnvironment environment,
    IInternalMicrosoftDetector detector,
    IGitClient git,
    TimeProvider timeProvider,
    IInteractionService interaction,
    ICliHostEnvironment hostEnvironment,
    ILogger<ConfigSourceService> logger) : IDisposable
{
    internal const string MicrosoftRepository = "https://msft.ghe.com/coreai/aspire-1p-init";
    private static readonly string[][] s_allowedSettings = [["docs", "llmsTxtUrl"], ["docs", "api", "sitemapUrl"]];
    private static readonly TimeSpan s_gitTimeout = TimeSpan.FromSeconds(30);
    private readonly Dictionary<ConfigSourceIdentity, ConfigSourceSnapshot> _snapshots = [];
    private string? _projectDirectory;
    private bool _projectChecked;
    private readonly List<(string Message, string? GitOutput)> _notifications = [];
    private readonly Lock _checksLock = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _check;

    internal static readonly Option<bool?> s_useSourceOption = new("--use-config-source")
    {
        Description = ConfigSourceStrings.UseSourceOptionDescription,
        Recursive = true,
        Arity = ArgumentArity.ExactlyOne
    };

    /// <summary>
    /// Checks saved sources in the nearest local and global configuration files without applying changes.
    /// Runs in the background and defers advisory messages until command exit.
    /// </summary>
    /// <param name="projectDirectory">The directory of the project already resolved by the caller.</param>
    /// <param name="cancellationToken">A token to cancel the source update check.</param>
    /// <exception cref="InvalidOperationException">The service has already been used for a different project.</exception>
    public void CheckForConfigSourceUpdates(DirectoryInfo projectDirectory, CancellationToken cancellationToken)
    {
        lock (_checksLock)
        {
            EnsureSameProject(projectDirectory);
            // Project selection can reach this through multiple paths. Schedule at most one
            // advisory task per command, even after it completes. `update --config-source`
            // performs its own forced refresh and must not start a competing advisory check.
            if (_check is not null || _stopping.IsCancellationRequested || executionContext.Command is UpdateCommand { IsConfigSourceRefresh: true })
            {
                return;
            }

            _check = Task.Run(async () =>
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
                try
                {
                    await CheckProjectAsync(projectDirectory, force: false, cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    logger.LogTrace("Configuration source check cancelled because the CLI is shutting down.");
                }
                catch (Exception ex)
                {
                    // Advisory work must never fail the command. Log only the type: source
                    // failures can contain private repository URLs or credentials.
                    logger.LogDebug("Non-fatal configuration source check failure ({FailureType}).", ex.GetType().Name);
                }
            }, CancellationToken.None);
        }
    }

    public async Task StopChecksAsync()
    {
        lock (_checksLock)
        {
            _stopping.Cancel();
        }

        // Cancel and join, rather than abandoning Git or credential-helper child processes.
        await WaitForChecksAsync();
    }

    internal Task WaitForChecksAsync()
    {
        lock (_checksLock)
        {
            return _check ?? Task.CompletedTask;
        }
    }

    public void NotifyIfUpdateAvailable()
    {
        (string Message, string? GitOutput)[] notifications;
        lock (_checksLock)
        {
            notifications = _notifications.ToArray();
            _notifications.Clear();
        }
        foreach (var pending in notifications)
        {
            interaction.DisplayMessage(KnownEmojis.Warning, pending.Message, consoleOverride: ConsoleOutput.Error);
            DisplayGitTimeoutOutput(interaction, pending.GitOutput);
        }
    }

    internal static void DisplayGitTimeoutOutput(IInteractionService interaction, string? output)
    {
        const int outputLimit = 64 * 1024;
        if (output is { Length: > 0 })
        {
            interaction.DisplayRawText(output.Length > outputLimit ? output[^outputLimit..] : output, ConsoleOutput.Error);
        }
    }

    public void Dispose()
    {
        StopChecksAsync().GetAwaiter().GetResult();
        _stopping.Dispose();
    }

    /// <summary>
    /// Resolves a candidate configuration source from the environment or employee detection,
    /// obtains consent, and acquires the accepted source.
    /// </summary>
    public async Task<ConfigSource?> ResolveAndAcquireConfigSourceAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var repository = environment.GetEnvironmentVariable(CliConfigNames.InitSourceUrl);
        var explicitSource = repository is not null;
        if (!explicitSource)
        {
            var detection = await detector.IsInternalMicrosoftMachineAsync(cancellationToken);
            if (!detection.IsInternalMicrosoft || detection.Outcome != InternalMicrosoftDetectorOutcome.Detected)
            {
                if (parseResult.GetValue(s_useSourceOption) is true)
                {
                    throw new ConfigSourceException(string.Format(CultureInfo.CurrentCulture,
                        ConfigSourceStrings.NoCandidate, CliConfigNames.InitSourceUrl));
                }
                return null;
            }
            repository = MicrosoftRepository;
        }

        var source = new ConfigSource
        {
            Repository = repository!,
            Path = explicitSource
                ? environment.GetEnvironmentVariable(CliConfigNames.InitSourcePath) ?? AspireConfigFile.FileName
                : AspireConfigFile.FileName,
            Ref = explicitSource ? environment.GetEnvironmentVariable(CliConfigNames.InitSourceRef) : null
        };
        source.Validate();
        interaction.DisplayMessage(KnownEmojis.Information, string.Format(CultureInfo.CurrentCulture, ConfigSourceStrings.Candidate,
            source.Repository, source.Path, source.Ref ?? ConfigSourceStrings.DefaultBranch,
            explicitSource ? CliConfigNames.InitSourceUrl : ConfigSourceStrings.EmployeeOrigin));

        var accepted = parseResult.GetValue(s_useSourceOption);
        if (accepted is null)
        {
            if (!hostEnvironment.SupportsInteractiveInput)
            {
                throw new ConfigSourceException(ConfigSourceStrings.ConsentRequired);
            }
            // Boolean choices let the prompt select default(bool), highlighting decline
            // even when accept is first. String choices preserve the first-item default.
            var selection = await interaction.PromptForSelectionAsync(ConfigSourceStrings.UseSourcePrompt,
                new[] { ConfigSourceStrings.Accept, ConfigSourceStrings.Decline }, choice => choice,
                cancellationToken: cancellationToken);
            accepted = selection == ConfigSourceStrings.Accept;
        }
        if (!accepted.Value)
        {
            return null;
        }

        await AcquireAsync(source, force: false, cancellationToken);
        return source;
    }

    public async Task<ConfigSource?> SelectForInitializationAsync(
        DirectoryInfo projectDirectory, ParseResult parseResult, CancellationToken cancellationToken)
    {
        EnsureSameProject(projectDirectory);
        var path = Path.Combine(projectDirectory.FullName, AspireConfigFile.FileName);
        var configuration = await ReadProjectConfigurationAsync(path, cancellationToken);
        if (configuration is not null && TryGetValue(configuration, [ConfigSource.SectionName], out _))
        {
            CheckForConfigSourceUpdates(projectDirectory, cancellationToken);
            return null;
        }

        return await ResolveAndAcquireConfigSourceAsync(parseResult, cancellationToken);
    }

    public async Task MaterializeAsync(ConfigSource source, DirectoryInfo projectDirectory, CancellationToken cancellationToken)
    {
        EnsureSameProject(projectDirectory);
        var snapshot = await AcquireAsync(source, force: false, cancellationToken);
        await ApplySnapshotAsync(source, snapshot, Path.Combine(projectDirectory.FullName, AspireConfigFile.FileName), cancellationToken);
    }

    private async Task ApplySnapshotAsync(ConfigSource source, ConfigSourceSnapshot snapshot, string path, CancellationToken cancellationToken)
    {
        using var configLock = await ConfigurationHelper.TryAcquireWorkspaceConfigLockAsync(
            executionContext, new FileInfo(path), logger, cancellationToken)
            ?? throw new ConfigSourceException(ConfigSourceStrings.ConfigLockFailed);
        // Re-read after fetching and under the workspace-writer lock, so intervening
        // changes to unrelated settings survive this read/merge/write operation.
        // Users should not edit aspire.config.json while the command is running.
        // Concurrent edits can cause unexpected behavior, such as restoring a
        // configSource binding removed while fetching.
        var configuration = await ReadConfigurationFileAsync(path, cancellationToken) ?? new JsonObject();
        if (ReadBinding(configuration) is { } current && !ConfigSource.HasSameIdentity(current, source))
        {
            throw new ConfigSourceException(ConfigSourceStrings.BindingChanged);
        }
        var previous = configuration.ToJsonString();
        ConfigurationHelper.NormalizeSettings(configuration);
        foreach (var setting in s_allowedSettings)
        {
            if (TryGetValue(snapshot.Configuration, setting, out var value))
            {
                SetValue(configuration, setting, value);
            }
        }
        var binding = new ConfigSource
        {
            Repository = source.Repository,
            Path = source.Path,
            Ref = source.Ref,
            AppliedVersion = snapshot.Metadata.Version
        };
        configuration[GetKey(configuration, ConfigSource.SectionName)] = JsonSerializer.SerializeToNode(binding, JsonSourceGenerationContext.Default.ConfigSource);
        var changed = previous != configuration.ToJsonString();
        if (changed)
        {
            try
            {
                // A preserved nested literal key can collide with a newly supplied leaf:
                // {"docs":{"api:sitemapUrl":"local","api":{"sitemapUrl":"remote"}}}.
                // Reject the merge rather than persisting settings startup cannot load.
                using var parsed = new ConfigurationManager();
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(configuration.ToJsonString()));
                parsed.AddJsonStream(stream);
            }
            catch (FormatException ex)
            {
                throw new ConfigSourceException(string.Format(CultureInfo.CurrentCulture, ErrorStrings.InvalidJsonInConfigFile, path, ex.Message));
            }
            await ConfigurationHelper.WriteSettingsFileAtomicallyAsync(path, configuration, cancellationToken);
        }
        lock (_checksLock)
        {
            _projectChecked = true;
        }
        interaction.DisplayMessage(KnownEmojis.Information, string.Format(CultureInfo.CurrentCulture, ConfigSourceStrings.UseNotice,
            source.Repository, source.Path, snapshot.Metadata.Commit,
            changed ? ConfigSourceStrings.Refreshed : ConfigSourceStrings.RefreshedUnchanged, path), consoleOverride: ConsoleOutput.Error);
    }

    public async Task CheckProjectAsync(DirectoryInfo directory, bool force, CancellationToken cancellationToken)
    {
        EnsureSameProject(directory);
        if (!force && executionContext.Command is UpdateCommand { IsConfigSourceRefresh: true })
        {
            // Project selection must not perform an automatic fetch immediately before
            // refresh-only mode makes its one unconditional fetch.
            return;
        }
        try
        {
            lock (_checksLock)
            {
                if (!force && _projectChecked)
                {
                    return;
                }
                _projectChecked = true;
            }
            var foundBinding = false;
            var refreshedSources = new HashSet<ConfigSourceIdentity>();
            var sources = new List<ConfigSource>();
            var configPaths = new[]
            {
                ConfigurationHelper.FindNearestConfigFilePath(directory),
                configurationService.GetSettingsFilePath(isGlobal: true)
            }.OfType<string>().Distinct(StringComparers.FileSystemPath);
            // Use the same local/global files as settings lookup, even if the nearest
            // file has no binding. Await each source so Git terminal input cannot compete.
            foreach (var configPath in configPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = await ReadBindingAsync(configPath, cancellationToken);
                if (source is null)
                {
                    continue;
                }
                foundBinding = true;
                var identity = new ConfigSourceIdentity(source.Repository, source.Path, source.Ref);
                if (force)
                {
                    var snapshot = await AcquireAsync(source, force: refreshedSources.Add(identity), cancellationToken);
                    await ApplySnapshotAsync(source, snapshot, configPath, cancellationToken);
                }
                else
                {
                    sources.Add(source);
                }
            }
            foreach (var group in sources.GroupBy(source => new ConfigSourceIdentity(source.Repository, source.Path, source.Ref)))
            {
                if (group.Any(source => string.IsNullOrEmpty(source.AppliedVersion)))
                {
                    ReportOnce(ConfigSourceStrings.AppliedVersionMissing);
                }
                var versionedSources = group.Where(source => !string.IsNullOrEmpty(source.AppliedVersion)).ToArray();
                if (versionedSources.Length > 0)
                {
                    var observed = await CheckVersionAsync(versionedSources, cancellationToken);
                    if (versionedSources.Any(source => observed.Version != source.AppliedVersion))
                    {
                        ReportOnce(ConfigSourceStrings.UpdateAvailable);
                    }
                }
            }
            if (force && !foundBinding)
            {
                interaction.DisplayMessage(KnownEmojis.Information, ConfigSourceStrings.NoBinding, consoleOverride: ConsoleOutput.Error);
            }
        }
        catch (Exception ex) when (!force && ex is ConfigSourceException or JsonException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            ReportCheckFailure(ex);
        }
    }

    private void ReportCheckFailure(Exception exception)
    {
        logger.LogDebug("Configuration source version check failed ({FailureType}).", exception.GetType().Name);
        var message = exception is ConfigSourceException
            ? string.Format(CultureInfo.CurrentCulture, ConfigSourceStrings.CheckFailed, exception.Message)
            : ConfigSourceStrings.CheckFailedWithoutDetails;
        ReportOnce(message, (exception as ConfigSourceException)?.GitOutput);
    }

    internal async Task<ConfigSource?> ReadBindingAsync(string configPath, CancellationToken cancellationToken)
    {
        return ReadBinding(await ReadProjectConfigurationAsync(configPath, cancellationToken));
    }

    private async Task<JsonObject?> ReadProjectConfigurationAsync(string configPath, CancellationToken cancellationToken)
    {
        using var configLock = await ConfigurationHelper.TryAcquireWorkspaceConfigLockAsync(
            executionContext, new FileInfo(configPath), logger, cancellationToken);
        return await ReadConfigurationFileAsync(configPath, cancellationToken);
    }

    private static async Task<JsonObject?> ReadConfigurationFileAsync(string path, CancellationToken cancellationToken)
    {
        return File.Exists(path)
            ? JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken),
                documentOptions: ConfigurationHelper.ParseOptions) as JsonObject
                ?? throw new ConfigSourceException(string.Format(CultureInfo.CurrentCulture, ErrorStrings.ConfigurationFileMustBeJsonObject, path))
            : null;
    }

    private static ConfigSource? ReadBinding(JsonObject? config)
    {
        if (config is null)
        {
            return null;
        }
        var sourceProperty = config.FirstOrDefault(p => p.Key.Equals(ConfigSource.SectionName, StringComparison.OrdinalIgnoreCase));
        if (sourceProperty.Key is null)
        {
            return null;
        }
        try
        {
            var source = sourceProperty.Value?.Deserialize(JsonSourceGenerationContext.Default.ConfigSource)
                ?? throw new ConfigSourceException(ConfigSourceStrings.InvalidBinding);
            if (source.Repository is null || source.Path is null)
            {
                throw new ConfigSourceException(ConfigSourceStrings.InvalidBinding);
            }
            source.Validate();
            return source;
        }
        catch (JsonException)
        {
            throw new ConfigSourceException(ConfigSourceStrings.InvalidBinding);
        }
    }

    internal string GetCacheDirectory(ConfigSource source)
    {
        var key = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes($"{source.Repository}\n{source.Path}\n{source.Ref ?? ""}")).ToString("x16", CultureInfo.InvariantCulture);
        return Path.Combine(executionContext.AspireHomeDirectory.FullName, "configsources", key);
    }

    internal async Task<ConfigSourceSnapshot> AcquireAsync(ConfigSource source, bool force, CancellationToken cancellationToken)
    {
        source.Validate();
        var key = new ConfigSourceIdentity(source.Repository, source.Path, source.Ref);
        lock (_checksLock)
        {
            if (!force && _snapshots.TryGetValue(key, out var existing))
            {
                return existing;
            }
        }

        var cacheDirectory = GetCacheDirectory(source);
        DirectoryHelper.CreateWithOwnerOnlyPermissions(Path.GetDirectoryName(cacheDirectory)!);
        DirectoryHelper.CreateWithOwnerOnlyPermissions(cacheDirectory);
        using var cacheLock = await FileLock.AcquireAsync(Path.Combine(cacheDirectory, "cache.lock"), cancellationToken);
        var pointer = Path.Combine(cacheDirectory, "current");
        var snapshotName = Guid.NewGuid().ToString("N");
        var snapshotDirectory = Path.Combine(cacheDirectory, snapshotName);
        var temporaryPointer = Path.Combine(cacheDirectory, $"{snapshotName}.tmp");
        DirectoryHelper.CreateWithOwnerOnlyPermissions(snapshotDirectory);
        var published = false;
        try
        {
            var identity = new ConfigSource
            {
                Repository = source.Repository,
                Path = source.Path,
                Ref = source.Ref
            };
            var (commit, version, configuration) = await AcquireRepositoryAsync(identity, snapshotDirectory, cancellationToken);
            ConfigurationHelper.NormalizeSettings(configuration);
            var now = timeProvider.GetUtcNow();
            var snapshot = new ConfigSourceSnapshot(new(identity, commit, version, now), configuration);
            await File.WriteAllTextAsync(Path.Combine(snapshotDirectory, AspireConfigFile.FileName), configuration.ToJsonString(), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(snapshotDirectory, "metadata.json"),
                JsonSerializer.Serialize(snapshot.Metadata, JsonSourceGenerationContext.Default.ConfigSourceMetadata), cancellationToken);
            await File.WriteAllTextAsync(temporaryPointer, snapshotName, cancellationToken);
            await WriteCheckMetadataAsync(cacheDirectory,
                new(identity, commit, version, now, source.AppliedVersion is null ? [] : [source.AppliedVersion]), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // The immutable snapshot contains both files. Only this pointer is replaced, so
            // failures or cancellation cannot publish half a configuration/metadata pair.
            File.Move(temporaryPointer, pointer, overwrite: true);
            published = true;
            lock (_checksLock)
            {
                _snapshots[key] = snapshot;
            }
            // Callers retain configuration in memory. After publication, older snapshots
            // can be removed while the cache lock excludes other acquisitions.
            try
            {
                foreach (var directory in Directory.GetDirectories(cacheDirectory))
                {
                    if (directory != snapshotDirectory && Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
                    {
                        DeleteAcquisitionDirectory(directory);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug("Configuration source snapshot cleanup failed ({FailureType}).", ex.GetType().Name);
            }
            return snapshot;
        }
        finally
        {
            File.Delete(temporaryPointer);
            if (!published)
            {
                DeleteAcquisitionDirectory(snapshotDirectory);
            }
        }
    }

    private async Task<ConfigSourceCheckMetadata> CheckVersionAsync(IReadOnlyList<ConfigSource> sources, CancellationToken cancellationToken)
    {
        var source = sources[0];
        var cacheDirectory = GetCacheDirectory(source);
        DirectoryHelper.CreateWithOwnerOnlyPermissions(Path.GetDirectoryName(cacheDirectory)!);
        DirectoryHelper.CreateWithOwnerOnlyPermissions(cacheDirectory);
        using var cacheLock = await FileLock.AcquireAsync(Path.Combine(cacheDirectory, "cache.lock"), cancellationToken);
        var path = Path.Combine(cacheDirectory, "check.json");
        if (File.Exists(path))
        {
            ConfigSourceCheckMetadata? previous;
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            try
            {
                previous = JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.ConfigSourceCheckMetadata);
            }
            catch (JsonException)
            {
                throw new ConfigSourceException(ConfigSourceStrings.InvalidCache);
            }
            if (previous is null || previous.Source is null || !ConfigSource.HasSameIdentity(previous.Source, source)
                || string.IsNullOrEmpty(previous.Commit) || string.IsNullOrEmpty(previous.Version))
            {
                throw new ConfigSourceException(ConfigSourceStrings.InvalidCache);
            }
            var age = timeProvider.GetUtcNow() - previous.CheckedAt;
            // A different app can have applied a version newer than this shared observation.
            // Reuse a mismatch only when that applied version was actually checked against it.
            // Older metadata without the version set is revalidated once.
            if (age >= TimeSpan.Zero && age < TimeSpan.FromHours(24)
                && sources.All(binding => previous.Version == binding.AppliedVersion
                    || previous.ComparedAppliedVersions?.Contains(binding.AppliedVersion, StringComparer.Ordinal) is true))
            {
                return previous;
            }
        }
        var temporaryDirectory = Directory.CreateTempSubdirectory("aspire-config-source-check-");
        try
        {
            var identity = new ConfigSource
            {
                Repository = source.Repository,
                Path = source.Path,
                Ref = source.Ref
            };
            var (commit, version) = await CheckRepositoryAsync(identity, temporaryDirectory.FullName, cancellationToken);
            var comparedAppliedVersions = sources.Select(binding => binding.AppliedVersion).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            var metadata = new ConfigSourceCheckMetadata(identity, commit, version, timeProvider.GetUtcNow(), comparedAppliedVersions);
            await WriteCheckMetadataAsync(cacheDirectory, metadata, cancellationToken);
            return metadata;
        }
        finally
        {
            DeleteAcquisitionDirectory(temporaryDirectory.FullName);
        }
    }

    private async Task<(string Commit, string Version, JsonObject Configuration)> AcquireRepositoryAsync(ConfigSource source, string directory, CancellationToken cancellationToken)
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

    private async Task<(string Commit, string Version)> CheckRepositoryAsync(ConfigSource source, string directory, CancellationToken cancellationToken)
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
        var workingDirectory = new DirectoryInfo(directory);
        var mode = allowInteractive ? GitCommandMode.Interactive : GitCommandMode.Unattended;
        string resolvedCommit;
        try
        {
            resolvedCommit = await git.ResolveReferenceAsync(source.Repository, source.GetGitRef(), workingDirectory, mode, cancellationToken);
        }
        catch (GitReferenceException)
        {
            throw new ConfigSourceException(ConfigSourceStrings.RepositoryUnavailable);
        }
        // Git cannot fetch between SHA-1 and SHA-256 repositories. Resolve the selector
        // before initialization so branch/default selectors also choose the source format.
        // https://git-scm.com/docs/git-init#Documentation/git-init.txt---object-formatltformatgt
        var repositoryDirectory = new GitRepositoryLocation(new DirectoryInfo(Path.Combine(directory, "repository.git")), IsBare: true);
        await git.InitializeAsync(workingDirectory, repositoryDirectory,
            resolvedCommit.Length == 64 ? "sha256" : "sha1", GitCommandMode.Interactive, cancellationToken);
        // Fetch accepts both a fully qualified branch ref and a full commit ID. Unlike
        // clone --branch, this also acquires commits outside the default branch's shallow history.
        // https://git-scm.com/docs/git-fetch
        await git.FetchAsync(repositoryDirectory, source.Repository, source.GetGitRef(),
            allowInteractive ? GitCommandMode.Interactive : GitCommandMode.Unattended, cancellationToken);
        var commit = await git.GetCommitAsync(repositoryDirectory, "FETCH_HEAD^{commit}", GitCommandMode.Interactive, cancellationToken);
        var entry = await git.GetFileAsync(repositoryDirectory, commit, source.Path, GitCommandMode.Interactive, cancellationToken);
        if (entry is not { Mode: GitTreeEntry.RegularFileMode or GitTreeEntry.ExecutableFileMode, Type: "blob" })
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

    private static Task WriteCheckMetadataAsync(string directory, ConfigSourceCheckMetadata metadata, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToNode(metadata, JsonSourceGenerationContext.Default.ConfigSourceCheckMetadata)!.AsObject();
        return ConfigurationHelper.WriteSettingsFileAtomicallyAsync(Path.Combine(directory, "check.json"), json, cancellationToken);
    }

    private void ReportOnce(string message, string? gitOutput = null)
    {
        lock (_checksLock)
        {
            if (!_notifications.Contains((message, gitOutput)))
            {
                _notifications.Add((message, gitOutput));
            }
        }
    }

    private void EnsureSameProject(DirectoryInfo directory)
    {
        var path = Path.TrimEndingDirectorySeparator(directory.FullName);
        lock (_checksLock)
        {
            if (_projectDirectory is not null && !StringComparers.FileSystemPath.Equals(_projectDirectory, path))
            {
                throw new InvalidOperationException("A configuration source service instance cannot be used for multiple projects.");
            }
            _projectDirectory = path;
        }
    }

    private void DeleteAcquisitionDirectory(string directory)
    {
        try
        {
            // Git object files can be read-only on Windows, even after a failed fetch.
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("Configuration source directory cleanup failed ({FailureType}).", ex.GetType().Name);
        }
    }

    private static string GetKey(JsonObject configuration, string key)
        => configuration.Select(p => p.Key).FirstOrDefault(p => p.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;

    private static bool TryGetValue(JsonObject configuration, string[] path, out JsonNode? value)
    {
        if (FindPropertyParent(configuration, path) is { } parent)
        {
            value = parent[GetKey(parent, path[^1])];
            return true;
        }

        value = null;
        return false;
    }

    private static JsonObject? FindPropertyParent(JsonObject configuration, ReadOnlySpan<string> path)
    {
        if (path.Length == 1)
        {
            return configuration.ContainsKey(GetKey(configuration, path[0])) ? configuration : null;
        }

        // IConfiguration combines sections such as {"DOCS":{"llmsTxtUrl":"a"},"docs":{"api":{"sitemapUrl":"b"}}}.
        // Search every matching section so an earlier partial section cannot hide a later leaf.
        foreach (var (key, value) in configuration)
        {
            if (key.Equals(path[0], StringComparison.OrdinalIgnoreCase)
                && value is JsonObject section
                && FindPropertyParent(section, path[1..]) is { } parent)
            {
                return parent;
            }
        }

        return null;
    }

    private static void SetValue(JsonObject configuration, string[] path, JsonNode? value)
    {
        if (FindPropertyParent(configuration, path) is { } parent)
        {
            parent[GetKey(parent, path[^1])] = value?.DeepClone();
            return;
        }

        var current = configuration;
        foreach (var segment in path[..^1])
        {
            var key = GetKey(current, segment);
            if (current[key] is not JsonObject nested)
            {
                nested = new JsonObject();
                current[key] = nested;
            }
            current = nested;
        }
        current[GetKey(current, path[^1])] = value?.DeepClone();
    }

    private readonly record struct ConfigSourceIdentity(string Repository, string Path, string? Ref);
}
