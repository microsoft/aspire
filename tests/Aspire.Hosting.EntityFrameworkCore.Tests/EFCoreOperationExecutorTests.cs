// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETTOOL

using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.EntityFrameworkCore.Tests;

public class EFCoreOperationExecutorTests
{
    [Fact]
    public async Task CaptureLogsAsync_ErrorPrefixedLinesLoggedAsError()
    {
        var logger = new CapturingLogger();
        var dataBuilder = new StringBuilder();

        var logs = CreateLogEntries(new LogLine(1, "error:   Unhandled exception: Unable to load the service index for source", false));

        using var cts = new CancellationTokenSource();
        await EFCoreOperationExecutor.CaptureLogsAsync(logs, logger, dataBuilder, cts.Token);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("Unhandled exception", entry.Message);
        Assert.Empty(dataBuilder.ToString());
    }

    [Fact]
    public async Task CaptureLogsAsync_InfoPrefixedLinesLoggedAsInformation()
    {
        var logger = new CapturingLogger();
        var dataBuilder = new StringBuilder();

        var logs = CreateLogEntries(new LogLine(1, "info:    Migration applied successfully.", false));

        using var cts = new CancellationTokenSource();
        await EFCoreOperationExecutor.CaptureLogsAsync(logs, logger, dataBuilder, cts.Token);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("Migration applied successfully", entry.Message);
        Assert.Empty(dataBuilder.ToString());
    }

    [Fact]
    public async Task CaptureLogsAsync_DataPrefixedLinesGoToDataBuilder()
    {
        var logger = new CapturingLogger();
        var dataBuilder = new StringBuilder();

        var logs = CreateLogEntries(new LogLine(1, "data:    [{\"id\":\"20240101\",\"name\":\"Init\"}]", false));

        using var cts = new CancellationTokenSource();
        await EFCoreOperationExecutor.CaptureLogsAsync(logs, logger, dataBuilder, cts.Token);

        Assert.NotEmpty(dataBuilder.ToString());
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task CaptureLogsAsync_StderrLinesLoggedAsError()
    {
        var logger = new CapturingLogger();
        var dataBuilder = new StringBuilder();

        var logs = CreateLogEntries(new LogLine(1, "Something failed", true));

        using var cts = new CancellationTokenSource();
        await EFCoreOperationExecutor.CaptureLogsAsync(logs, logger, dataBuilder, cts.Token);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("Something failed", entry.Message);
    }

    [Fact]
    public async Task CaptureLogsAsync_MixedOutputRoutesCorrectly()
    {
        var logger = new CapturingLogger();
        var dataBuilder = new StringBuilder();

        var logs = CreateLogEntries(
            new LogLine(1, "info:    Starting migration...", false),
            new LogLine(2, "error:   NuGet restore failed", false),
            new LogLine(3, "data:    {}", false),
            new LogLine(4, "warn:    Deprecated warning", false));

        using var cts = new CancellationTokenSource();
        await EFCoreOperationExecutor.CaptureLogsAsync(logs, logger, dataBuilder, cts.Token);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Starting migration"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("NuGet restore failed"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Deprecated warning"));
        Assert.NotEmpty(dataBuilder.ToString());
    }

    [Fact]
    public async Task CaptureLogsAsync_VerbosePrefixedLinesLoggedAsDebug()
    {
        var logger = new CapturingLogger();
        var dataBuilder = new StringBuilder();

        var logs = CreateLogEntries(new LogLine(1, "verbose: Loaded assembly from cache", false));

        using var cts = new CancellationTokenSource();
        await EFCoreOperationExecutor.CaptureLogsAsync(logs, logger, dataBuilder, cts.Token);

        var entry = Assert.Single(logger.Entries);
        Assert.Contains("Loaded assembly from cache", entry.Message);
        Assert.NotEqual(LogLevel.Error, entry.Level);
    }

    [Fact]
    public async Task CaptureLogsAsync_NoErrorsProducesNoErrorLogEntries()
    {
        var logger = new CapturingLogger();
        var dataBuilder = new StringBuilder();

        var logs = CreateLogEntries(
            new LogLine(1, "info:    Applying migration '20240101_Init'...", false),
            new LogLine(2, "info:    Done.", false));

        using var cts = new CancellationTokenSource();
        await EFCoreOperationExecutor.CaptureLogsAsync(logs, logger, dataBuilder, cts.Token);

        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Error);
        Assert.NotEmpty(logger.Entries);
    }

    [Fact]
    public async Task UpdateDatabaseAsync_ReturnsFailureWhenStartCommandFails()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        using var app = builder.Build();
        var toolResource = CreateToolResource(_ => Task.FromResult(CommandResults.Failure("tool startup failed")));

        using var executor = new EFCoreOperationExecutor(
            project.Resource,
            targetProjectPath: null,
            contextTypeName: null,
            NullLogger.Instance,
            CancellationToken.None,
            app.Services,
            toolResource);

        var result = await executor.UpdateDatabaseAsync();

        Assert.False(result.Success);
        Assert.Equal("tool startup failed", result.ErrorMessage);
    }

    [Fact]
    public async Task UpdateDatabaseAsync_ReturnsFailureWhenStartCommandIsCanceled()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        using var app = builder.Build();
        var toolResource = CreateToolResource(_ => Task.FromResult(CommandResults.Canceled()));

        using var executor = new EFCoreOperationExecutor(
            project.Resource,
            targetProjectPath: null,
            contextTypeName: null,
            NullLogger.Instance,
            CancellationToken.None,
            app.Services,
            toolResource);

        var result = await executor.UpdateDatabaseAsync();

        Assert.False(result.Success);
        Assert.Equal("dotnet-ef command was canceled.", result.ErrorMessage);
    }

    private static DotnetToolResource CreateToolResource(Func<ExecuteCommandContext, Task<ExecuteCommandResult>> executeCommand)
    {
        var toolResource = new DotnetToolResource("ef-tool", "dotnet-ef");
        toolResource.Annotations.Add(new ResourceCommandAnnotation(
            KnownResourceCommands.StartCommand,
            "Start",
            _ => ResourceCommandState.Enabled,
            executeCommand,
            displayDescription: null,
            parameter: null,
            confirmationMessage: null,
            iconName: null,
            iconVariant: null,
            isHighlighted: false));

        return toolResource;
    }

    private static async IAsyncEnumerable<IReadOnlyList<LogLine>> CreateLogEntries(params LogLine[] lines)
    {
        yield return lines;
        await Task.CompletedTask;
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
