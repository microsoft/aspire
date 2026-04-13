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
        var provider = new SpectreConsoleLoggerProvider(output);

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
        var aspireLogger = new SpectreConsoleLogger(output, "Aspire.Cli.Test");
        var systemLogger = new SpectreConsoleLogger(output, "System.Test");

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
        var logger = new SpectreConsoleLogger(output, "Aspire.Cli.Test");

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
        var logger = new SpectreConsoleLogger(output, "Aspire.Cli.NuGet.NuGetPackageCache");

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
        var logger = new SpectreConsoleLogger(output, "Aspire.Cli.Test");

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
        var logger = new SpectreConsoleLogger(output, "Aspire.Cli.Test");

        // Act
        using (SpectreConsoleLoggerProvider.BeginInteractivePromptScope())
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
        var logger = new SpectreConsoleLogger(output, "Aspire.Cli.Test");

        // Act
        using (SpectreConsoleLoggerProvider.BeginInteractivePromptScope())
        {
            logger.LogInformation("first");

            using (SpectreConsoleLoggerProvider.BeginInteractivePromptScope())
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
}
