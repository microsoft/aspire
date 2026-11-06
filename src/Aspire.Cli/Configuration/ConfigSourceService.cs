// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Commands;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Cli.Templating;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Aspire.Shared.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Configuration;

internal sealed class ConfigSourceService(
    CliExecutionContext executionContext,
    IEnvironment environment,
    IInternalMicrosoftDetector detector,
    IGitClient git,
    TimeProvider timeProvider,
    IInteractionService interaction,
    ICliHostEnvironment hostEnvironment,
    ILogger<ConfigSourceService> logger) : IDisposable
{
    internal const string MicrosoftRepository = "https://msft.ghe.com/coreai/aspire-1p-init";
    internal const string MicrosoftReadmeUrl = $"{MicrosoftRepository}/blob/HEAD/README.md";
    private static readonly string[] s_allowedSettings = [SkillSource.SectionName, IntegrationSourceConfiguration.SectionName];
    private static readonly TimeSpan s_gitTimeout = TimeSpan.FromSeconds(30);
    private readonly Dictionary<ConfigSourceIdentity, ConfigSourceSnapshot> _snapshots = [];
    private string? _projectDirectory;
    private bool _projectChecked;
    private readonly List<(string Message, string? GitOutput)> _notifications = [];
    private readonly Lock _checksLock = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _check;
    private bool _sourceDeclined;

    internal static readonly Option<bool?> s_useSourceOption = new("--use-config-source")
    {
        Description = ConfigSourceStrings.UseSourceOptionDescription,
        Recursive = true,
        Arity = ArgumentArity.ZeroOrOne
    };

    /// <summary>
    /// Checks the saved source in the nearest local configuration file without applying changes.
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
            interaction.DisplayMessage(KnownEmojis.PoliceCarLight, pending.Message, consoleOverride: ConsoleOutput.Error);
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
    /// Resolves an optional configuration source from the environment or employee detection,
    /// obtains consent, and acquires the accepted source.
    /// </summary>
    public Task<ConfigSource?> ResolveAndAcquireConfigSourceAsync(ParseResult parseResult, CancellationToken cancellationToken) =>
        ResolveAndAcquireConfigSourceAsync(parseResult, allowInteractiveInput: true, cancellationToken);

    internal async Task<ConfigSource?> ResolveAndAcquireConfigSourceAsync(
        ParseResult parseResult, bool allowInteractiveInput, CancellationToken cancellationToken)
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
            Path = GetConfigSourcePath(explicitSource),
            Ref = explicitSource ? environment.GetEnvironmentVariable(CliConfigNames.InitSourceRef) : null
        };
        source.Validate();
        var readmeUrl = environment.GetEnvironmentVariable(CliConfigNames.InitSourceReadmeUrl)
            ?? (source.Repository == MicrosoftRepository ? MicrosoftReadmeUrl : null);
        if (readmeUrl is not null && (!UrlHelper.IsHttpUrl(readmeUrl) || readmeUrl.Any(char.IsControl)))
        {
            throw new ConfigSourceException(string.Format(CultureInfo.CurrentCulture,
                ConfigSourceStrings.InvalidReadmeUrl, CliConfigNames.InitSourceReadmeUrl));
        }

        interaction.DisplayMessage(KnownEmojis.Information, ConfigSourceStrings.Explanation);
        List<(string Label, string Value)> details =
        [
            (ConfigSourceStrings.ConfigurationSource, source.Repository)
        ];
        if (source.Path != AspireConfigFile.FileName)
        {
            details.Add((ConfigSourceStrings.ConfigurationFile, source.Path));
        }
        if (source.Ref is not null)
        {
            details.Add((ConfigSourceStrings.ConfigurationRef, source.Ref));
        }
        details.Add((ConfigSourceStrings.SourceOrigin, explicitSource ? CliConfigNames.InitSourceUrl : ConfigSourceStrings.EmployeeOrigin));

        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.Columns[0].Width = details.Max(detail => detail.Label.Length) + 1;
        foreach (var (label, value) in details)
        {
            grid.AddRow(
                new Align(new Markup($"[bold green]{label.EscapeMarkup()}[/]:"), HorizontalAlignment.Right),
                new Text(value));
        }
        interaction.DisplayEmptyLine();
        interaction.DisplayRenderable(new Padder(grid, new Padding(3, 0)));
        interaction.DisplayEmptyLine();
        if (readmeUrl is not null)
        {
            interaction.DisplayMessage(KnownEmojis.Information,
                MarkupHelpers.SafeLink(interaction, readmeUrl, ConfigSourceStrings.ReadmeLink), allowMarkup: true);
        }
        interaction.DisplaySourceTrustWarning(executionContext);

        // Machine-configured or detected sources suggest useful defaults for new apps;
        // they do not enforce organizational policy or establish a security boundary.
        // Declining the source is supported, not a security bypass.
        var accepted = parseResult.GetValue(s_useSourceOption);
        if (accepted is null)
        {
            if (!allowInteractiveInput || !hostEnvironment.SupportsInteractiveInput)
            {
                throw new ConfigSourceException(ConfigSourceStrings.ConsentRequired);
            }
            accepted = await interaction.PromptConfirmAsync(ConfigSourceStrings.UseSourcePrompt,
                binding: PromptBinding.CreateDefault(true),
                cancellationToken: cancellationToken);
        }
        _sourceDeclined = !accepted.Value;
        if (_sourceDeclined)
        {
            return new ConfigSource { OptOut = true };
        }

        await AcquireAsync(source, force: false, allowInteractiveInput, cancellationToken);
        return source;
    }

    public async Task<ConfigSource?> SelectForInitializationAsync(
        DirectoryInfo projectDirectory, ParseResult parseResult, CancellationToken cancellationToken)
    {
        EnsureSameProject(projectDirectory);
        var path = Path.Combine(projectDirectory.FullName, AspireConfigFile.FileName);
        var configuration = await ReadProjectConfigurationAsync(path, cancellationToken);
        if (configuration is not null && configuration.ContainsKey(GetKey(configuration, ConfigSource.SectionName)))
        {
            CheckForConfigSourceUpdates(projectDirectory, cancellationToken);
            return null;
        }

        return await ResolveAndAcquireConfigSourceAsync(parseResult, cancellationToken);
    }

    public async Task MaterializeAsync(ConfigSource source, DirectoryInfo projectDirectory, CancellationToken cancellationToken)
    {
        EnsureSameProject(projectDirectory);
        await ApplySourceAsync(source, Path.Combine(projectDirectory.FullName, AspireConfigFile.FileName), cancellationToken);
    }

    public async Task SelectForUpdateAsync(DirectoryInfo projectDirectory, ParseResult parseResult, CancellationToken cancellationToken)
    {
        EnsureSameProject(projectDirectory);
        var path = ConfigurationHelper.FindNearestConfigFilePath(projectDirectory)
            ?? Path.Combine(projectDirectory.FullName, AspireConfigFile.FileName);
        var configuration = await ReadProjectConfigurationAsync(path, cancellationToken);
        if (configuration is not null && configuration.ContainsKey(GetKey(configuration, ConfigSource.SectionName)))
        {
            return;
        }

        // Updating packages must not introduce a new consent requirement for unattended runs.
        // --yes authorizes package updates, not adoption of organization configuration.
        if (!hostEnvironment.SupportsInteractiveInput && parseResult.GetValue(s_useSourceOption) is null)
        {
            return;
        }

        var source = await ResolveAndAcquireConfigSourceAsync(parseResult, cancellationToken);
        if (source is not null)
        {
            await ApplySourceAsync(source, path, cancellationToken);
        }
    }

    private async Task ApplySourceAsync(ConfigSource source, string path, CancellationToken cancellationToken)
    {
        if (source.OptOut)
        {
            using var configLock = await ConfigurationHelper.TryAcquireWorkspaceConfigLockAsync(
                executionContext, new FileInfo(path), logger, cancellationToken)
                ?? throw new ConfigSourceException(ConfigSourceStrings.ConfigLockFailed);
            var configuration = await ReadConfigurationFileAsync(path, cancellationToken) ?? new JsonObject();
            if (ReadBinding(configuration) is { OptOut: false })
            {
                throw new ConfigSourceException(ConfigSourceStrings.BindingChanged);
            }
            configuration[GetKey(configuration, ConfigSource.SectionName)] = new JsonObject { ["optOut"] = true };
            await ConfigurationHelper.WriteSettingsFileAtomicallyAsync(path, configuration, cancellationToken);
            return;
        }

        var snapshot = await AcquireAsync(source, force: false, cancellationToken);
        await ApplySnapshotAsync(source, snapshot, path, cancellationToken);
    }

    internal async Task<JsonObject> GetTemplateSourcesAsync(CancellationToken cancellationToken)
    {
        // Discovery runs after creation consent and must not re-enable a declined source.
        if (_sourceDeclined)
        {
            return new JsonObject();
        }

        // An explicitly configured URL opts into optional runtime template-source inheritance,
        // not project-file materialization. Employee detection alone does not opt in.
        var repository = environment.GetEnvironmentVariable(CliConfigNames.InitSourceUrl);
        if (repository is null)
        {
            return new JsonObject();
        }

        var source = new ConfigSource
        {
            Repository = repository,
            Path = GetConfigSourcePath(explicitSource: true),
            Ref = environment.GetEnvironmentVariable(CliConfigNames.InitSourceRef)
        };
        return await GetTemplateSourcesAsync(source, cancellationToken);
    }

    internal async Task<JsonObject> GetTemplateSourcesAsync(ConfigSource source, CancellationToken cancellationToken)
    {
        if (source.OptOut)
        {
            return new JsonObject();
        }

        var snapshot = await AcquireAsync(source, force: false, cancellationToken);
        var sources = new JsonObject();
        foreach (var (key, value) in snapshot.Configuration)
        {
            if (!key.Equals(AspireConfigFile.TemplateSourcesSectionName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (value is not JsonObject section)
            {
                throw new TemplateSourceConfigurationException(AspireConfigFile.TemplateSourcesSectionName, NewCommandStrings.TemplateSourcesMustBeObject);
            }
            foreach (var (name, definition) in section)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TemplateSourceConfiguration.ValidateName(name);
                using var document = JsonDocument.Parse(definition?.ToJsonString() ?? "null");
                TemplateSourceConfiguration.Parse(document.RootElement, $"{AspireConfigFile.TemplateSourcesSectionName}.{name}", requireComplete: true);
                sources.Add(name, definition?.DeepClone());
            }
        }

        return sources;
    }

    private string GetConfigSourcePath(bool explicitSource) =>
        explicitSource
            ? environment.GetEnvironmentVariable(CliConfigNames.InitSourcePath) ?? AspireConfigFile.FileName
            : AspireConfigFile.FileName;

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
        foreach (var setting in s_allowedSettings)
        {
            var supplied = snapshot.Configuration
                .Where(property => property.Key.Equals(setting, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (supplied.Length == 0)
            {
                continue;
            }

            // Replace all case-equivalent local sections, preserving every source section
            // that IConfiguration combines (for example {"SKILLSOURCES":{...},"skillSources":{...}}).
            // Keep the original position so an unchanged section does not reorder the file
            // and make an otherwise identical refresh report changes.
            var insertionIndex = configuration.Count;
            foreach (var key in configuration.Select(property => property.Key)
                .Where(key => key.Equals(setting, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                insertionIndex = Math.Min(insertionIndex, configuration.IndexOf(key));
                configuration.Remove(key);
            }
            foreach (var (key, value) in supplied)
            {
                configuration.Insert(insertionIndex++, key, value?.DeepClone());
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
            cancellationToken.ThrowIfCancellationRequested();
            var configPath = ConfigurationHelper.FindNearestConfigFilePath(directory);
            var source = configPath is null ? null : await ReadBindingAsync(configPath, cancellationToken);
            if (configPath is null || source is null || source.OptOut)
            {
                if (force)
                {
                    interaction.DisplayMessage(KnownEmojis.Information, ConfigSourceStrings.NoBinding, consoleOverride: ConsoleOutput.Error);
                }
                return;
            }
            if (force)
            {
                var snapshot = await AcquireAsync(source, force: true, cancellationToken);
                await ApplySnapshotAsync(source, snapshot, configPath, cancellationToken);
            }
            else if (string.IsNullOrEmpty(source.AppliedVersion))
            {
                ReportOnce(ConfigSourceStrings.AppliedVersionMissing);
            }
            else
            {
                var observed = await CheckVersionAsync(source, cancellationToken);
                if (observed.Version != source.AppliedVersion)
                {
                    ReportOnce(ConfigSourceStrings.UpdateAvailable);
                }
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
        if (!File.Exists(path))
        {
            return null;
        }

        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken),
            documentOptions: ConfigurationHelper.ParseOptions) as JsonObject
            ?? throw new ConfigSourceException(string.Format(CultureInfo.CurrentCulture, ErrorStrings.ConfigurationFileMustBeJsonObject, path));
        // Normalize before binding lookups so legacy keys such as {"configSource:optOut":true}
        // are honored by both read-only checks and write-time binding guards.
        ConfigurationHelper.NormalizeSettings(configuration);

        return configuration;
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
            if (source.OptOut)
            {
                return source;
            }
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

    internal Task<ConfigSourceSnapshot> AcquireAsync(ConfigSource source, bool force, CancellationToken cancellationToken) =>
        AcquireAsync(source, force, allowInteractiveInput: true, cancellationToken);

    private async Task<ConfigSourceSnapshot> AcquireAsync(
        ConfigSource source, bool force, bool allowInteractiveInput, CancellationToken cancellationToken)
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

        interaction.DisplaySourceTrustWarning(executionContext);
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
            interaction.DisplayMessage(KnownEmojis.DownArrow,
                allowInteractiveInput && hostEnvironment.SupportsInteractiveInput
                    ? ConfigSourceStrings.FetchingSourceWithAuthentication
                    : ConfigSourceStrings.FetchingSource,
                consoleOverride: ConsoleOutput.Error);
            var (commit, version, configuration) = await AcquireRepositoryAsync(identity, snapshotDirectory, allowInteractiveInput, cancellationToken);
            ConfigurationHelper.NormalizeSettings(configuration);
            var now = timeProvider.GetUtcNow();
            var snapshot = new ConfigSourceSnapshot(new(identity, commit, version, now), configuration);
            await File.WriteAllTextAsync(Path.Combine(snapshotDirectory, AspireConfigFile.FileName), configuration.ToJsonString(), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(snapshotDirectory, "metadata.json"),
                JsonSerializer.Serialize(snapshot.Metadata, JsonSourceGenerationContext.Default.ConfigSourceMetadata), cancellationToken);
            await File.WriteAllTextAsync(temporaryPointer, snapshotName, cancellationToken);
            await WriteCheckMetadataAsync(cacheDirectory,
                new(identity, commit, version, now, source.AppliedVersion), cancellationToken);
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

    private async Task<ConfigSourceCheckMetadata> CheckVersionAsync(ConfigSource source, CancellationToken cancellationToken)
    {
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
            // Metadata without the compared applied version is revalidated once.
            if (age >= TimeSpan.Zero && age < TimeSpan.FromHours(24)
                && (previous.Version == source.AppliedVersion
                    || previous.ComparedAppliedVersion == source.AppliedVersion))
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
            var metadata = new ConfigSourceCheckMetadata(identity, commit, version, timeProvider.GetUtcNow(),
                source.AppliedVersion);
            await WriteCheckMetadataAsync(cacheDirectory, metadata, cancellationToken);
            return metadata;
        }
        finally
        {
            DeleteAcquisitionDirectory(temporaryDirectory.FullName);
        }
    }

    private async Task<(string Commit, string Version, JsonObject Configuration)> AcquireRepositoryAsync(
        ConfigSource source, string directory, bool allowInteractiveInput, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(s_gitTimeout, timeProvider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var (repositoryDirectory, commit, version) = await FetchVersionAsync(source, directory, allowInteractiveInput, timeout.Token);
            var json = await git.ReadBlobAsync(repositoryDirectory, version, GitCommandMode.Interactive, timeout.Token);
            try
            {
                // These checks ensure configuration can be consumed correctly; they do not
                // enforce a security boundary or police the contents of a trusted source.
                var configuration = JsonNode.Parse(json, documentOptions: ConfigurationHelper.ParseOptions) as JsonObject
                    ?? throw new ConfigSourceException(ConfigSourceStrings.InvalidConfiguration);
                // Reserve recursive sources for future support, so a later implementation
                // cannot unexpectedly activate declarations that were previously ignored.
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
        // Read blob contents directly, without interpreting file modes or creating filesystem links.
        if (entry is not { Type: "blob" })
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
            GitFailure.UnattendedSshUnsupported => exception.Message,
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

    private readonly record struct ConfigSourceIdentity(string Repository, string Path, string? Ref);
}
