// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Interaction;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Tests.Interaction;

public class SpectreConsoleLoggerProviderTests
{
    [Fact]
    public void CreateLogger_ReturnsSpectreConsoleLogger()
    {
        // Arrange
        var output = new StringWriter();
        var provider = new SpectreConsoleLoggerProvider(output, new ConsoleLogBufferContext());

        // Act
        var logger = provider.CreateLogger("Test.Category");

        // Assert
        Assert.NotNull(logger);
        Assert.IsType<SpectreConsoleLogger>(logger);
    }

    [Fact]
    public void SpectreConsoleLogger_IsEnabled_FiltersCorrectly()
    {
        // Arrange
        var output = new StringWriter();
        var bufferContext = new ConsoleLogBufferContext();
        var aspireLogger = new SpectreConsoleLogger(output, "Aspire.Cli.Test", bufferContext);
        var systemLogger = new SpectreConsoleLogger(output, "System.Test", bufferContext);

        // Act & Assert
        Assert.True(aspireLogger.IsEnabled(LogLevel.Debug));
        Assert.True(aspireLogger.IsEnabled(LogLevel.Information));
        Assert.True(aspireLogger.IsEnabled(LogLevel.Warning));

        Assert.False(systemLogger.IsEnabled(LogLevel.Debug));
        Assert.False(systemLogger.IsEnabled(LogLevel.Information));
        Assert.True(systemLogger.IsEnabled(LogLevel.Warning)); // Warnings and above are allowed for non-Aspire categories
    }

    [Fact]
    public void SpectreConsoleLogger_Log_FormatsMessageCorrectly()
    {
        // Arrange
        var output = new StringWriter();
        var logger = new SpectreConsoleLogger(output, "Aspire.Cli.Test", new ConsoleLogBufferContext());

        // Act
        logger.LogDebug("Test debug message");
        logger.LogInformation("Test info message");
        logger.LogWarning("Test warning message");

        // Assert
        var outputString = output.ToString();
        // Note: With timestamp format, the log line will be: [HH:mm:ss] [dbug] Test: Test debug message
        Assert.Contains("[dbug] Test: Test debug message", outputString);
        Assert.Contains("[info] Test: Test info message", outputString);
        Assert.Contains("[warn] Test: Test warning message", outputString);

        // Verify that timestamps are present
        Assert.Matches(@"\[\d{2}:\d{2}:\d{2}\]", outputString);
    }

    [Fact]
    public void SpectreConsoleLogger_Log_UsesShortCategoryName()
    {
        // Arrange
        var output = new StringWriter();
        var logger = new SpectreConsoleLogger(output, "Aspire.Cli.NuGet.NuGetPackageCache", new ConsoleLogBufferContext());

        // Act
        logger.LogDebug("Getting integrations from NuGet");

        // Assert
        var outputString = output.ToString();
        // Note: With timestamp format, the log line will be: [HH:mm:ss] [dbug] NuGetPackageCache: Getting integrations from NuGet
        Assert.Contains("[dbug] NuGetPackageCache: Getting integrations from NuGet", outputString);
        Assert.DoesNotContain("Aspire.Cli.NuGet.NuGetPackageCache", outputString);

        // Verify that timestamps are present
        Assert.Matches(@"\[\d{2}:\d{2}:\d{2}\]", outputString);
    }

    [Fact]
    public void SpectreConsoleLogger_Log_IncludesTimestampInHHmmssFormat()
    {
        // Arrange
        var output = new StringWriter();
        var logger = new SpectreConsoleLogger(output, "Aspire.Cli.Test", new ConsoleLogBufferContext());

        // Act
        logger.LogDebug("Test debug message");

        // Assert
        var outputString = output.ToString();

        // Verify timestamp format (HH:mm:ss) is included at the beginning
        // The format should be: [HH:mm:ss] [dbug] Test: Test debug message
        Assert.Matches(@"\[\d{2}:\d{2}:\d{2}\] \[dbug\] Test: Test debug message", outputString);
    }

    [Fact]
    public void SpectreConsoleLogger_Log_BuffersWhileInteractivePromptScopeIsActive()
    {
        // Arrange
        var output = new StringWriter();
        var bufferContext = new ConsoleLogBufferContext();
        var logger = new SpectreConsoleLogger(output, "Aspire.Cli.Test", bufferContext);

        // Act
        using (bufferContext.BeginInteractivePromptScope())
        {
            logger.LogInformation("buffered while prompting");

            // Assert
            Assert.DoesNotContain("buffered while prompting", output.ToString());
        }

        // Assert
        Assert.Contains("[info] Test: buffered while prompting", output.ToString());
    }

    [Fact]
    public void SpectreConsoleLogger_Log_FlushesOnlyAfterOuterPromptScopeEnds()
    {
        // Arrange
        var output = new StringWriter();
        var bufferContext = new ConsoleLogBufferContext();
        var logger = new SpectreConsoleLogger(output, "Aspire.Cli.Test", bufferContext);

        // Act
        using (bufferContext.BeginInteractivePromptScope())
        {
            logger.LogInformation("first");

            using (bufferContext.BeginInteractivePromptScope())
            {
                logger.LogInformation("second");
            }

            Assert.DoesNotContain("first", output.ToString());
            Assert.DoesNotContain("second", output.ToString());
        }

        // Assert
        var flushedOutput = output.ToString();
        Assert.Contains("[info] Test: first", flushedOutput);
        Assert.Contains("[info] Test: second", flushedOutput);
        Assert.True(flushedOutput.IndexOf("first", StringComparison.Ordinal) < flushedOutput.IndexOf("second", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WriteOrBuffer_IsAtomic_NoLogSlipsDuringPromptStart()
    {
        // Verify that a write started without a prompt active cannot appear after a
        // prompt scope is opened on another thread — the decision and I/O are atomic.
        var output = new StringWriter();
        var bufferContext = new ConsoleLogBufferContext();

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Start a prompt scope on another thread, signaling when it's active.
        var promptTask = Task.Run(async () =>
        {
            await barrier.Task;
            using var scope = bufferContext.BeginInteractivePromptScope();
            promptStarted.SetResult();
            // Hold the scope open long enough for the write attempt.
            await Task.Delay(200);
        });

        // Signal the prompt thread and wait until it's active.
        barrier.SetResult();
        await promptStarted.Task;

        // Any write after the prompt is active must be buffered.
        bufferContext.WriteOrBuffer(output, "should-be-buffered");

        await promptTask;

        // After prompt ends the message is flushed.
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Equal("should-be-buffered", lines[0]);
    }

    [Fact]
    public void EndScope_FlushingState_BuffersNewWritesDuringDrain()
    {
        // Messages written during the flush of a prior scope must not appear before
        // the buffered messages — they should be appended after.
        var output = new StringWriter();
        var bufferContext = new ConsoleLogBufferContext();

        // Use a custom TextWriter that writes another message via the buffer context
        // the first time it's used during flush, simulating a concurrent log arriving
        // while drain is in progress.
        var interceptWriter = new FlushInterceptingWriter(output, bufferContext);

        using (bufferContext.BeginInteractivePromptScope())
        {
            // This will be flushed first; during its flush the interceptWriter triggers
            // another WriteOrBuffer call.
            bufferContext.WriteOrBuffer(interceptWriter, "original");
        }

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("original", lines[0]);
        Assert.Equal("injected-during-flush", lines[1]);
    }

    [Fact]
    public void WriteOrBuffer_DropsOldestWhenBufferCapReached()
    {
        var output = new StringWriter();
        var bufferContext = new ConsoleLogBufferContext();

        using (bufferContext.BeginInteractivePromptScope())
        {
            // Fill the buffer beyond the cap.
            for (var i = 0; i < ConsoleLogBufferContext.MaxBufferedMessages + 50; i++)
            {
                bufferContext.WriteOrBuffer(output, $"msg-{i}");
            }
        }

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        // Only the last MaxBufferedMessages should remain; the first 50 were dropped.
        Assert.Equal(ConsoleLogBufferContext.MaxBufferedMessages, lines.Length);
        Assert.Equal("msg-50", lines[0]);
        Assert.Equal($"msg-{ConsoleLogBufferContext.MaxBufferedMessages + 49}", lines[^1]);
    }

    [Fact]
    public void WriteOrBuffer_WritesDirectlyWhenNoPromptActive()
    {
        var output = new StringWriter();
        var bufferContext = new ConsoleLogBufferContext();

        bufferContext.WriteOrBuffer(output, "direct-write");

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Equal("direct-write", lines[0]);
    }

    [Fact]
    public async Task EndScope_DoesNotStealDepthFromConcurrentNewScope()
    {
        // Validates that the flush loop of a closing scope does not decrement
        // _interactivePromptDepth for a scope opened on another thread mid-flush.
        var output = new StringWriter();
        var bufferContext = new ConsoleLogBufferContext();

        // Use a writer that opens a new scope during the flush of the first scope,
        // simulating a concurrent prompt starting while the previous prompt's buffered
        // messages are being drained.
        var scopeOpenedDuringFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scopeWriter = new ScopeOpeningWriter(output, bufferContext, scopeOpenedDuringFlush);

        using (bufferContext.BeginInteractivePromptScope())
        {
            bufferContext.WriteOrBuffer(scopeWriter, "from-first-scope");
        }

        // The ScopeOpeningWriter opened a new scope during flush. Messages written
        // while that scope is active should be buffered.
        await scopeOpenedDuringFlush.Task;
        bufferContext.WriteOrBuffer(output, "during-second-scope");

        // End the second scope — this should flush "during-second-scope".
        scopeWriter.SecondScope!.Dispose();

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("from-first-scope", lines[0]);
        Assert.Equal("during-second-scope", lines[1]);
    }

    /// <summary>
    /// A TextWriter wrapper that injects a message via the buffer context the first time
    /// <see cref="WriteLine(string)"/> is called, simulating a concurrent log during flush.
    /// </summary>
    private sealed class FlushInterceptingWriter(StringWriter inner, ConsoleLogBufferContext context) : TextWriter
    {
        private int _intercepted;

        public override System.Text.Encoding Encoding => inner.Encoding;

        public override void WriteLine(string? value)
        {
            inner.WriteLine(value);

            // On the first flush write, inject another message into the buffer context.
            if (Interlocked.Exchange(ref _intercepted, 1) == 0)
            {
                context.WriteOrBuffer(inner, "injected-during-flush");
            }
        }
    }

    /// <summary>
    /// A TextWriter that opens a new interactive prompt scope during flush, simulating
    /// a concurrent prompt starting while a previous scope's buffer is being drained.
    /// </summary>
    private sealed class ScopeOpeningWriter(StringWriter inner, ConsoleLogBufferContext context, TaskCompletionSource scopeOpened) : TextWriter
    {
        private int _intercepted;

        public IDisposable? SecondScope { get; private set; }

        public override System.Text.Encoding Encoding => inner.Encoding;

        public override void WriteLine(string? value)
        {
            inner.WriteLine(value);

            // On the first flush write, open a new scope to simulate a concurrent prompt.
            if (Interlocked.Exchange(ref _intercepted, 1) == 0)
            {
                SecondScope = context.BeginInteractivePromptScope();
                scopeOpened.SetResult();
            }
        }
    }
}
