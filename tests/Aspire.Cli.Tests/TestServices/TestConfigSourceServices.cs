// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Text;
using Aspire.Cli.Configuration;
using Aspire.Cli.Git;
using Aspire.Cli.Utils;
using Aspire.Shared.Telemetry;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.TestServices;

internal static class TestConfigSourceServices
{
    public static async Task SaveBindingAsync(ConfigSource source, DirectoryInfo projectDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(projectDirectory.FullName, AspireConfigFile.FileName);
        var configuration = File.Exists(path)
            ? JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))!.AsObject()
            : new JsonObject();
        configuration["configSource"] = JsonSerializer.SerializeToNode(source, JsonSourceGenerationContext.Default.ConfigSource);
        await ConfigurationHelper.WriteSettingsFileAsync(path, configuration, cancellationToken);
    }

    public static ConfigSourceService Create(CliExecutionContext context, IGitClient? git = null,
        TimeProvider? clock = null, IEnvironment? environment = null, TestConfigSourceDetector? detector = null,
        TestInteractionService? interaction = null, bool interactive = false, FileInfo? globalSettingsFile = null)
    {
        var configurationService = new TestConfigurationService
        {
            SettingsFilePath = globalSettingsFile?.FullName ?? Path.Combine(context.WorkingDirectory.FullName, ".aspire", "settings.global.json")
        };
        return new ConfigSourceService(context, configurationService, environment ?? new TestEnvironment(), detector ?? new TestConfigSourceDetector(),
            git ?? new TestConfigSourceRepository(), clock ?? TimeProvider.System, interaction ?? new TestInteractionService(),
            interactive ? TestHelpers.CreateInteractiveHostEnvironment() : TestHelpers.CreateNonInteractiveHostEnvironment(),
            NullLogger<ConfigSourceService>.Instance);
    }

    public static async Task<(string Commit, string Version, JsonObject Configuration)> AcquireAsync(
        this IGitClient git, ConfigSource source, string directory, CancellationToken cancellationToken, TimeProvider? clock = null)
    {
        using var service = Create(TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(directory)), git: git, clock: clock);
        var snapshot = await service.AcquireAsync(source, force: true, cancellationToken);
        return (snapshot.Metadata.Commit, snapshot.Metadata.Version, snapshot.Configuration);
    }

    public static async Task<(string Commit, string Version)> CheckAsync(
        this IGitClient git, ConfigSource source, string directory, CancellationToken cancellationToken, TimeProvider? clock = null)
    {
        var project = new DirectoryInfo(directory);
        await SaveBindingAsync(new ConfigSource
        {
            Repository = source.Repository,
            Path = source.Path,
            Ref = source.Ref,
            AppliedVersion = "previous-version"
        }, project, cancellationToken);
        var interaction = new TestInteractionService();
        using var service = Create(TestExecutionContextHelper.CreateExecutionContext(project), git: git, clock: clock, interaction: interaction);
        await service.CheckProjectAsync(project, force: false, cancellationToken);
        var path = Path.Combine(service.GetCacheDirectory(source), "check.json");
        Assert.True(File.Exists(path), "The service did not persist the Git version check.");
        var metadata = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, cancellationToken),
            JsonSourceGenerationContext.Default.ConfigSourceCheckMetadata)!;
        return (metadata.Commit, metadata.Version);
    }
}

internal sealed class TestConfigSourceDetector : IInternalMicrosoftDetector
{
    public int Calls { get; private set; }
    public bool IsInternal { get; set; }
    public string? Outcome { get; set; }

    public Task<InternalMicrosoftDetectionResult> IsInternalMicrosoftMachineAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new InternalMicrosoftDetectionResult(IsInternal, null, null, null,
            Outcome ?? (IsInternal ? InternalMicrosoftDetectorOutcome.Detected : InternalMicrosoftDetectorOutcome.NotDetected),
            InternalMicrosoftDetectorCacheStatus.Miss, TimeSpan.Zero, []));
    }
}

internal sealed class TestConfigSourceRepository : IGitClient
{
    private readonly ConcurrentDictionary<string, RepositoryState> _repositories = new();
    private int _calls;
    private int _checkCalls;
    public string Commit { get; set; } = "commit-1";
    public int Calls => _calls;
    public int CheckCalls => _checkCalls;
    public Func<ConfigSource, CancellationToken, Task<(string Version, JsonObject Configuration)>> ReadConfiguration { get; set; }
        = (_, _) => Task.FromResult(("version-1", new JsonObject()));
    public Func<ConfigSource, CancellationToken, Task<string>> InspectConfiguration { get; set; }
        = (_, _) => Task.FromResult("version-1");

    public Task InitializeAsync(DirectoryInfo workingDirectory, GitRepositoryLocation repository, string? objectFormat,
        GitCommandMode mode, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task FetchAsync(GitRepositoryLocation destination, string repository, string reference, GitCommandMode mode, CancellationToken cancellationToken)
    {
        _repositories[destination.Directory.FullName] = new RepositoryState(repository, reference, mode);
        return Task.CompletedTask;
    }

    public Task<string> GetCommitAsync(GitRepositoryLocation repository, string reference, GitCommandMode mode, CancellationToken cancellationToken) =>
        Task.FromResult(Commit);

    public async Task<GitTreeEntry?> GetFileAsync(GitRepositoryLocation repository, string commit, string path, GitCommandMode mode, CancellationToken cancellationToken)
    {
        var state = _repositories[repository.Directory.FullName];
        var source = new ConfigSource
        {
            Repository = state.Repository,
            Path = path,
            Ref = state.Reference == "HEAD" ? null : state.Reference.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? state.Reference["refs/heads/".Length..] : state.Reference
        };
        string version;
        if (state.Mode == GitCommandMode.Unattended)
        {
            Interlocked.Increment(ref _checkCalls);
            version = await InspectConfiguration(source, cancellationToken);
        }
        else
        {
            Interlocked.Increment(ref _calls);
            var configuration = await ReadConfiguration(source, cancellationToken);
            version = configuration.Version;
            state.Content = configuration.Configuration.ToJsonString();
        }
        return new GitTreeEntry(GitTreeEntry.RegularFileMode, "blob", version);
    }

    public Task<long?> GetObjectSizeAsync(GitRepositoryLocation repository, string objectId, GitCommandMode mode, CancellationToken cancellationToken) =>
        Task.FromResult<long?>(Encoding.UTF8.GetByteCount(_repositories[repository.Directory.FullName].Content));

    public Task<string> ReadBlobAsync(GitRepositoryLocation repository, string objectId, GitCommandMode mode, CancellationToken cancellationToken) =>
        Task.FromResult(_repositories[repository.Directory.FullName].Content);

    public Task<string> ResolveReferenceAsync(string repository, string? reference, DirectoryInfo workingDirectory, GitCommandMode mode, CancellationToken cancellationToken) =>
        Task.FromResult(Commit);

    public Task CheckoutAsync(GitRepositoryLocation repository, GitCommandMode mode, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Configuration sources must not check out a worktree.");

    private sealed class RepositoryState(string repository, string reference, GitCommandMode mode)
    {
        public string Repository { get; } = repository;
        public string Reference { get; } = reference;
        public GitCommandMode Mode { get; } = mode;
        public string Content { get; set; } = string.Empty;
    }
}
