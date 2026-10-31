// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Testing;

internal sealed class TestRunReporter
{
    private const UnixFileMode FileModeBits = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly CliExecutionContext _executionContext;
    private readonly IInteractionService _interaction;
    private readonly bool _interactive;
    private readonly OutputFormat _format;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Dictionary<string, TestResourceState> _resources = new(StringComparers.ResourceName);
    private readonly List<PublishedTestResult> _results = [];
    private readonly Dictionary<string, TestCollectionState> _collections = new(StringComparers.ResourceName);
    private readonly DateTimeOffset _startedAt;
    private readonly object _gate = new();
    private bool _completed;
    private bool _summaryDisplayed;
    private DateTimeOffset? _completedAt;
    private TestRunSummary? _lastSummary;
    public bool JsonDisplayed { get; private set; }

    public string OutputPath { get; }
    public string SummaryPath => Path.Combine(OutputPath, "summary.json");

    private TestRunReporter(CliExecutionContext executionContext, IInteractionService interaction,
        bool interactive, OutputFormat format, TimeProvider timeProvider, ILogger logger, string outputPath)
    {
        _executionContext = executionContext;
        _interaction = interaction;
        _interactive = interactive && format != OutputFormat.Json;
        _format = format;
        _timeProvider = timeProvider;
        _logger = logger;
        _startedAt = timeProvider.GetUtcNow();
        OutputPath = outputPath;
    }

    public static TestRunReporter Create(CliExecutionContext executionContext, IInteractionService interaction,
        bool interactive, OutputFormat format, TimeProvider timeProvider, ILogger logger)
    {
        var parent = Path.Combine(executionContext.AspireHomeDirectory.FullName, "testruns");
        ValidateAncestors(parent, executionContext);
        CreatePrivateDirectory(parent, repairExisting: true, executionContext);

        // Reserve the name exclusively before creating its directory. Other invocations follow
        // the same protocol; an existing directory is never adopted as a new test run.
        var id = $"{timeProvider.GetUtcNow():yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        var reservation = Path.Combine(parent, $".{id}.reserve");
        var outputPath = Path.Combine(parent, id);
        var reserved = CreateFile(reservation);
        try
        {
            if (Directory.Exists(outputPath) || new DirectoryInfo(outputPath).LinkTarget is not null)
            {
                throw new IOException($"The test output directory '{outputPath}' already exists.");
            }
            CreatePrivateDirectory(outputPath, repairExisting: false, executionContext);
        }
        finally
        {
            reserved.Dispose();
            File.Delete(reservation);
        }

        var reporter = new TestRunReporter(executionContext, interaction, interactive, format, timeProvider, logger, outputPath);
        reporter.Persist("running", exitCode: null, error: null);
        if (format != OutputFormat.Json)
        {
            interaction.DisplayMessage(KnownEmojis.Microscope, RunCommandStrings.TestRunningHeader);
            interaction.DisplaySubtleMessage(string.Format(CultureInfo.CurrentCulture,
                RunCommandStrings.TestOutputDirectory, MarkupHelpers.SafeFileLink(interaction, outputPath)), allowMarkup: true);
        }

        return reporter;
    }

    public async Task<bool> WaitForCompletionAsync(IAppHostCliBackchannel backchannel, CancellationToken cancellationToken)
    {
        bool? passed = null;
        await foreach (var item in backchannel.WatchTestRunAsync(cancellationToken).ConfigureAwait(false))
        {
            if (passed is not null)
            {
                throw new InvalidOperationException("The test stream continued after aggregate completion.");
            }

            if ((item.Resource is null ? 0 : 1) + (item.Result is null ? 0 : 1) +
                (item.Collection is null ? 0 : 1) + (item.Passed is null ? 0 : 1) != 1)
            {
                throw new InvalidOperationException("The AppHost sent an invalid test resource event.");
            }
            if (item.Resource is { } resource)
            {
                Update(resource);
            }
            else if (item.Result is { } testResult)
            {
                UpdateResult(testResult);
            }
            else if (item.Collection is { } collection)
            {
                UpdateCollection(collection);
            }
            else if (item.Passed is { } result)
            {
                passed = result;
            }
            else
            {
                throw new InvalidOperationException("The AppHost sent an invalid test resource event.");
            }
        }

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (passed is true && (_resources.Count == 0 || _resources.Values.Any(r => r.Outcome != "passed") ||
                _results.Any(r => r.Outcome == "failed") ||
                _collections.Values.Any(c => c.Status is not ("completed" or "notConfigured")) ||
                _resources.Values.GroupBy(r => r.ResourceName, StringComparers.ResourceName)
                    .Any(group => group.Count() != group.First().ExpectedReplicas ||
                        group.Any(r => r.ExpectedReplicas != group.First().ExpectedReplicas))))
            {
                throw new InvalidOperationException("The AppHost reported success without complete passing test resource outcomes.");
            }

            return passed ?? throw new InvalidOperationException("The test stream ended without an aggregate outcome.");
        }
    }

    private void Update(TestResourceState resource)
    {
        lock (_gate)
        {
            // Cancellation/early AppHost exit may race an in-flight RPC frame. Freeze the report
            // before finalization so late updates cannot overwrite a cancelled/failed summary.
            if (_completed)
            {
                _logger.LogDebug("Ignoring test resource update after finalizing the run.");
                return;
            }
            UpdateCore(resource);
        }
    }

    private void UpdateResult(PublishedTestResult result)
    {
        lock (_gate)
        {
            if (_completed)
            {
                _logger.LogDebug("Ignoring test result after finalizing the run.");
                return;
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(result.Name);
            if (result.Outcome is not ("passed" or "failed" or "skipped") ||
                result.DurationMilliseconds is { } duration && (!double.IsFinite(duration) || duration < 0) ||
                !_resources.Values.Any(r => StringComparers.ResourceName.Equals(r.ResourceName, result.ResourceName)))
            {
                throw new InvalidOperationException("The AppHost sent an invalid published test result.");
            }
            _results.Add(result);
            Persist("running", exitCode: null, error: null);
            if (_interactive)
            {
                _interaction.DisplayMessage(KnownEmojis.Microscope, string.Format(CultureInfo.CurrentCulture,
                    RunCommandStrings.TestPublishedResult, result.ResourceName, result.Name, DisplayOutcome(result.Outcome)));
            }
        }
    }

    private void UpdateCollection(TestCollectionState collection)
    {
        lock (_gate)
        {
            if (_completed)
            {
                _logger.LogDebug("Ignoring test collection update after finalizing the run.");
                return;
            }
            if (collection.CollectorCount < 0 ||
                collection.Status is not ("notConfigured" or "running" or "completed" or "failed" or "cancelled") ||
                collection.Status == "notConfigured" && collection.CollectorCount != 0 ||
                !_resources.Values.Any(r => StringComparers.ResourceName.Equals(r.ResourceName, collection.ResourceName)))
            {
                throw new InvalidOperationException("The AppHost sent an invalid test collection state.");
            }
            _collections[collection.ResourceName] = collection;
            Persist("running", exitCode: null, error: null);
            if (_interactive && collection.Error is { } error)
            {
                _interaction.DisplayError(error);
            }
        }
    }

    private void UpdateCore(TestResourceState resource)
    {
        ArgumentException.ThrowIfNullOrEmpty(resource.ResourceName);
        if (resource.ExpectedReplicas < 1 || resource.Outcome is not ("pending" or "passed" or "failed") ||
            resource.Outcome != "pending" && string.IsNullOrEmpty(resource.ResourceId) ||
            resource.Outcome == "passed" && (resource.ExitCode != 0 ||
                !StringComparers.ResourceState.Equals(resource.State, "Finished") &&
                !StringComparers.ResourceState.Equals(resource.State, "Exited")))
        {
            throw new InvalidOperationException("The AppHost sent an invalid test resource outcome.");
        }
        if (resource.ResourceId is null && _resources.Values.Any(r =>
            StringComparers.ResourceName.Equals(r.ResourceName, resource.ResourceName) && r.ResourceId is not null))
        {
            // A late model placeholder must not reappear as an unfinished extra replica.
            return;
        }

        var key = Key(resource.ResourceName, resource.ResourceId);
        if (_resources.TryGetValue(key, out var previous))
        {
            // A final outcome is authoritative even if the independent observation stream
            // queues an older/pending snapshot after the completion waiter publishes it.
            if (previous == resource || previous.Outcome != "pending" && resource.Outcome == "pending")
            {
                return;
            }
            if (previous.Outcome != "pending" && resource.Outcome != "pending")
            {
                throw new InvalidOperationException("The AppHost sent conflicting final test resource outcomes.");
            }
        }

        if (resource.ResourceId is not null)
        {
            _resources.Remove(Key(resource.ResourceName, resourceId: null));
        }
        _resources[key] = resource;
        Persist("running", exitCode: null, error: null);
        if (_interactive)
        {
            _interaction.DisplayMessage(KnownEmojis.Microscope, string.Format(CultureInfo.CurrentCulture,
                RunCommandStrings.TestResourceStatus, resource.ResourceId ?? resource.ResourceName,
                resource.State ?? "-", DisplayOutcome(resource.Outcome), resource.ExitCode?.ToString(CultureInfo.CurrentCulture) ?? "-"));
        }
    }

    public void Complete(CommandResult result)
    {
        lock (_gate)
        {
            _completed = true;
            _completedAt ??= _timeProvider.GetUtcNow();
            CompleteCore(result);
        }
    }

    private void CompleteCore(CommandResult result)
    {
        var status = result.ExitCode switch
        {
            CliExitCodes.Success => "passed",
            CliExitCodes.Cancelled => "cancelled",
            _ => "failed"
        };
        foreach (var (key, resource) in _resources.ToArray())
        {
            if (resource.Outcome == "pending")
            {
                _resources[key] = resource with { Outcome = "incomplete" };
            }
        }
        foreach (var (key, collection) in _collections.ToArray())
        {
            if (collection.Status == "running")
            {
                _collections[key] = collection with { Status = "incomplete" };
            }
        }
        Persist(status, result.ExitCode, result.ErrorMessage);
        DisplaySummary(status);
    }

    public void ReportCompletedResources(bool passed)
    {
        lock (_gate)
        {
            _completed = true;
            _completedAt ??= _timeProvider.GetUtcNow();
            // Resources and collectors are complete, but a keep-alive CLI has not exited yet.
            Persist(passed ? "passed" : "failed", exitCode: null, error: null);
            DisplaySummary(passed ? "passed" : "failed");
        }
    }

    private void DisplaySummary(string status)
    {
        if (_summaryDisplayed)
        {
            return;
        }
        if (_format == OutputFormat.Json)
        {
            WriteJson(_lastSummary ?? throw new InvalidOperationException("The test summary has not been persisted."));
            _summaryDisplayed = true;
            return;
        }
        if (_interactive)
        {
            _interaction.DisplayMessage(KnownEmojis.Microscope, string.Format(CultureInfo.CurrentCulture,
                RunCommandStrings.TestSummaryHeader, DisplayOutcome(status)));
            var table = new Table();
            table.AddBoldColumn(RunCommandStrings.Resource);
            table.AddBoldColumn(RunCommandStrings.State);
            table.AddBoldColumn(RunCommandStrings.TestOutcome);
            table.AddBoldColumn(RunCommandStrings.TestExitCode);
            foreach (var resource in OrderedResources())
            {
                table.AddRow((resource.ResourceId ?? resource.ResourceName).EscapeMarkup(),
                    (resource.State ?? "-").EscapeMarkup(), DisplayOutcome(resource.Outcome).EscapeMarkup(),
                    resource.ExitCode?.ToString(CultureInfo.CurrentCulture) ?? "-");
            }
            _interaction.DisplayRenderable(table);
            if (_results.Count > 0)
            {
                var totals = ResultSummary();
                _interaction.DisplayMessage(KnownEmojis.Microscope, string.Format(CultureInfo.CurrentCulture,
                    RunCommandStrings.TestPublishedSummary, totals.Passed, totals.Failed, totals.Skipped));
            }
        }

        _interaction.DisplaySubtleMessage(string.Format(CultureInfo.CurrentCulture,
            RunCommandStrings.TestSummaryFile, MarkupHelpers.SafeFileLink(_interaction, SummaryPath)), allowMarkup: true);
        _summaryDisplayed = true;
    }

    private void Persist(string status, int? exitCode, string? error)
    {
        ValidateAncestors(OutputPath, _executionContext);
        CreatePrivateDirectory(OutputPath, repairExisting: false, _executionContext);
        if (new FileInfo(SummaryPath).LinkTarget is not null)
        {
            throw new IOException($"The test summary '{SummaryPath}' must not be a symbolic link.");
        }

        var summary = CreateSummary(status, exitCode, error);
        var temporaryPath = Path.Combine(OutputPath, $".summary-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = CreateFile(temporaryPath))
            {
                JsonSerializer.Serialize(stream, summary, TestRunJsonContext.Default.TestRunSummary);
                stream.Flush(flushToDisk: true);
            }
            // Replace the directory entry, never truncate an existing file or follow its link.
            // Readers see either the previous complete summary or this complete summary.
            File.Move(temporaryPath, SummaryPath, overwrite: true);
            _lastSummary = summary;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not clean up temporary test summary {Path}.", temporaryPath);
            }
        }
    }

    public void WriteJsonResult(CommandResult result)
    {
        lock (_gate)
        {
            _completedAt ??= _timeProvider.GetUtcNow();
            WriteJson(CreateSummary(result.ExitCode == CliExitCodes.Cancelled ? "cancelled" : "failed",
                result.ExitCode, result.ErrorMessage));
        }
    }

    private void WriteJson(TestRunSummary summary)
    {
        if (JsonDisplayed)
        {
            return;
        }
        _interaction.DisplayRawText(JsonSerializer.Serialize(summary, TestRunJsonContext.Default.TestRunSummary), ConsoleOutput.Standard);
        JsonDisplayed = true;
    }

    private TestRunSummary CreateSummary(string status, int? exitCode, string? error) => new()
    {
        RunId = Path.GetFileName(OutputPath),
        OutputPath = OutputPath,
        SummaryPath = SummaryPath,
        StartedAt = _startedAt,
        CompletedAt = _completedAt,
        Status = status,
        CliExitCode = exitCode,
        Error = error,
        Resources = OrderedResources(),
        TestResults = _results.OrderBy(r => r.ResourceName, StringComparers.ResourceName).ToArray(),
        Collections = _collections.Values.OrderBy(c => c.ResourceName, StringComparers.ResourceName).ToArray(),
        PublishedResults = ResultSummary()
    };

    private PublishedResultSummary ResultSummary() => new()
    {
        Passed = _results.Count(r => r.Outcome == "passed"),
        Failed = _results.Count(r => r.Outcome == "failed"),
        Skipped = _results.Count(r => r.Outcome == "skipped")
    };

    private TestResourceState[] OrderedResources() => _resources.Values
        .OrderBy(r => r.ResourceName, StringComparers.ResourceName)
        .ThenBy(r => r.ResourceId, StringComparers.ResourceName)
        .ToArray();

    private static string Key(string resourceName, string? resourceId) => $"{resourceName}\0{resourceId}";

    private static string DisplayOutcome(string outcome) => outcome switch
    {
        "passed" => RunCommandStrings.TestPassed,
        "failed" => RunCommandStrings.TestFailed,
        "cancelled" => RunCommandStrings.TestCancelled,
        "incomplete" => RunCommandStrings.TestIncomplete,
        "skipped" => RunCommandStrings.TestSkipped,
        _ => RunCommandStrings.TestPending
    };

    private static FileStream CreateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileModeBits;
        }

        var stream = new FileStream(path, options);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // A restrictive umask may remove owner bits at creation. Restore only the owner's
                // access through the open handle; the file is never exposed with broader access.
                File.SetUnixFileMode(stream.SafeFileHandle, FileModeBits);
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void CreatePrivateDirectory(string path, bool repairExisting, CliExecutionContext executionContext)
    {
        // Artifacts need the same directory boundary as filesystem-backed IPC: Unix 0700,
        // protected owner-only Windows ACLs supplied at creation, and no symlink traversal.
        SocketPermissionHelper.CreateDirectory(path, repairExisting,
            executionContext.WorkingDirectory.FullName, executionContext.HomeDirectory.FullName, Path.GetTempPath());
    }

    private static void ValidateAncestors(string path, CliExecutionContext executionContext)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var current = new DirectoryInfo(path).Parent; current is not null; current = current.Parent)
        {
            if (string.Equals(current.FullName, executionContext.HomeDirectory.FullName, comparison) ||
                string.Equals(current.FullName, Path.TrimEndingDirectorySeparator(Path.GetTempPath()), comparison))
            {
                break;
            }
            if (current.LinkTarget is not null || current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"The test output path must not traverse a symbolic link or reparse point: '{current.FullName}'.");
            }
            if (!OperatingSystem.IsWindows() && current.Exists &&
                (File.GetUnixFileMode(current.FullName) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            {
                throw new IOException($"The test output parent '{current.FullName}' must not be writable by other users.");
            }
        }
    }
}

internal sealed class TestRunSummary
{
    public int SchemaVersion { get; init; } = 1;
    public string? RunId { get; init; }
    public string? OutputPath { get; init; }
    public string? SummaryPath { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required string Status { get; init; }
    public int? CliExitCode { get; init; }
    public string? Error { get; init; }
    public TestResourceState[] Resources { get; init; } = [];
    public PublishedTestResult[] TestResults { get; init; } = [];
    public TestCollectionState[] Collections { get; init; } = [];
    public PublishedResultSummary PublishedResults { get; init; } = new();
}

internal sealed class PublishedResultSummary
{
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(TestRunSummary))]
internal sealed partial class TestRunJsonContext : JsonSerializerContext;
