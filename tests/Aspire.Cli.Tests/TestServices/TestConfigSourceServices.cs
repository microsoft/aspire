// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using System.Text.Json;
using Aspire.Cli.Configuration;
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

    public static ConfigSourceService Create(CliExecutionContext context, IConfigSourceGitClient? git = null,
        TimeProvider? clock = null, IEnvironment? environment = null, TestConfigSourceDetector? detector = null,
        TestInteractionService? interaction = null, bool interactive = false, FileInfo? globalSettingsFile = null)
    {
        var configurationService = new TestConfigurationService
        {
            SettingsFilePath = globalSettingsFile?.FullName ?? Path.Combine(context.WorkingDirectory.FullName, ".aspire", "settings.global.json")
        };
        return new ConfigSourceService(context, configurationService, environment ?? new TestEnvironment(), detector ?? new TestConfigSourceDetector(),
            git ?? new TestConfigSourceGitClient(), clock ?? TimeProvider.System, interaction ?? new TestInteractionService(),
            interactive ? TestHelpers.CreateInteractiveHostEnvironment() : TestHelpers.CreateNonInteractiveHostEnvironment(),
            NullLogger<ConfigSourceService>.Instance);
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

internal sealed class TestConfigSourceGitClient : IConfigSourceGitClient
{
    private int _calls;
    private int _checkCalls;
    public int Calls => _calls;
    public int CheckCalls => _checkCalls;
    public Func<ConfigSource, CancellationToken, Task<(string Commit, string Version, JsonObject Configuration)>> Acquire { get; set; }
        = (_, _) => Task.FromResult(("commit-1", "version-1", new JsonObject()));
    public Func<ConfigSource, CancellationToken, Task<(string Commit, string Version)>> Check { get; set; }
        = (_, _) => Task.FromResult(("commit-1", "version-1"));

    public Task<(string Commit, string Version, JsonObject Configuration)> AcquireAsync(ConfigSource source, string directory, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Acquire(source, cancellationToken);
    }

    public Task<(string Commit, string Version)> CheckAsync(ConfigSource source, string directory, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _checkCalls);
        return Check(source, cancellationToken);
    }
}
