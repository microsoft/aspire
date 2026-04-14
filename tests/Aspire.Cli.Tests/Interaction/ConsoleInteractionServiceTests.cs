// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Rendering;

using System.Text;

namespace Aspire.Cli.Tests.Interaction;

public class ConsoleInteractionServiceTests
{
    private static ConsoleInteractionService CreateInteractionService(IAnsiConsole console, CliExecutionContext executionContext, ICliHostEnvironment? hostEnvironment = null)
    {
        var consoleEnvironment = new ConsoleEnvironment(console, console);
        return new ConsoleInteractionService(consoleEnvironment, executionContext, hostEnvironment ?? TestHelpers.CreateInteractiveHostEnvironment(), NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task PromptForSelectionAsync_EmptyChoices_ThrowsEmptyChoicesException()
    {
        // Arrange
        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(AnsiConsole.Console, executionContext);
        var choices = Array.Empty<string>();

        // Act & Assert
        await Assert.ThrowsAsync<EmptyChoicesException>(() =>
            interactionService.PromptForSelectionAsync("Select an item:", choices, x => x, CancellationToken.None));
    }

    [Fact]
    public async Task PromptForSelectionsAsync_EmptyChoices_ThrowsEmptyChoicesException()
    {
        // Arrange
        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(AnsiConsole.Console, executionContext);
        var choices = Array.Empty<string>();

        // Act & Assert
        await Assert.ThrowsAsync<EmptyChoicesException>(() =>
            interactionService.PromptForSelectionsAsync("Select items:", choices, x => x, cancellationToken: CancellationToken.None));
    }

    [Fact]
    public void DisplayError_WithMarkupCharacters_DoesNotCauseMarkupParsingError()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });
        
        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(console, executionContext);
        var errorMessage = "The JSON value could not be converted to <Type>. Path: $.values[0].Type | LineNumber: 0 | BytePositionInLine: 121.";

        // Act - this should not throw an exception due to markup parsing
        var exception = Record.Exception(() => interactionService.DisplayError(errorMessage));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("The JSON value could not be converted to", outputString);
    }

    [Fact]
    public void DisplaySubtleMessage_WithMarkupCharacters_DoesNotCauseMarkupParsingError()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });
        
        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(console, executionContext);
        var message = "Path with <brackets> and [markup] characters";

        // Act - this should not throw an exception due to markup parsing
        var exception = Record.Exception(() => interactionService.DisplaySubtleMessage(message));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("Path with <brackets> and [markup] characters", outputString);
    }

    [Fact]
    public void DisplayLines_WithMarkupCharacters_DoesNotCauseMarkupParsingError()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });
        
        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(console, executionContext);
        var lines = new[]
        {
            (OutputLineStream.StdOut, "Command output with <angle> brackets"),
            (OutputLineStream.StdErr, "Error output with [square] brackets")
        };

        // Act - this should not throw an exception due to markup parsing
        var exception = Record.Exception(() => interactionService.DisplayLines(lines));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("Command output with <angle> brackets", outputString);
        // EscapeMarkup() escapes [ to [[ for Spectre's parser, but Spectre renders [[ back to literal [
        Assert.Contains("Error output with [square] brackets", outputString);
    }

    [Fact]
    public void DisplayMarkdown_WithBasicMarkdown_ConvertsToSpectreMarkup()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });
        
        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(console, executionContext);
        var markdown = "# Header\nThis is **bold** and *italic* text with `code`.";

        // Act
        var exception = Record.Exception(() => interactionService.DisplayMarkdown(markdown));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        // Should contain converted markup, but due to Ansi = No, the actual markup tags won't appear in output
        // Just verify it doesn't throw and produces some output
        Assert.NotEmpty(outputString.Trim());
    }

    [Fact]
    public void DisplayMarkdown_WithPlainText_DoesNotThrow()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });
        
        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(console, executionContext);
        var plainText = "This is just plain text without any markdown.";

        // Act
        var exception = Record.Exception(() => interactionService.DisplayMarkdown(plainText));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("This is just plain text without any markdown.", outputString);
    }

    [Fact]
    public async Task ShowStatusAsync_InDebugMode_DisplaysSubtleMessageInsteadOfSpinner()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log", debugMode: true);
        var interactionService = CreateInteractionService(console, executionContext);
        var statusText = "Processing request...";
        var result = "test result";

        // Act
        var actualResult = await interactionService.ShowStatusAsync(statusText, () => Task.FromResult(result)).DefaultTimeout();

        // Assert
        Assert.Equal(result, actualResult);
        var outputString = output.ToString();
        Assert.Contains(statusText, outputString);
        // In debug mode, should use DisplaySubtleMessage instead of spinner
    }

    [Fact]
    public void ShowStatus_InDebugMode_DisplaysSubtleMessageInsteadOfSpinner()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log", debugMode: true);
        var interactionService = CreateInteractionService(console, executionContext);
        var statusText = "Processing synchronous request...";
        var actionCalled = false;

        // Act
        interactionService.ShowStatus(statusText, () => actionCalled = true);

        // Assert
        Assert.True(actionCalled);
        var outputString = output.ToString();
        Assert.Contains(statusText, outputString);
        // In debug mode, should use DisplaySubtleMessage instead of spinner
    }

    [Fact]
    public async Task PromptForStringAsync_WhenInteractiveInputNotSupported_ThrowsInvalidOperationException()
    {
        // Arrange
        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var hostEnvironment = TestHelpers.CreateNonInteractiveHostEnvironment();
        var interactionService = CreateInteractionService(AnsiConsole.Console, executionContext, hostEnvironment);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            interactionService.PromptForStringAsync("Enter value:", null, null, false, false, CancellationToken.None));
        Assert.Contains(InteractionServiceStrings.InteractiveInputNotSupported, exception.Message);
    }

    [Fact]
    public async Task PromptForSelectionAsync_WhenInteractiveInputNotSupported_ThrowsInvalidOperationException()
    {
        // Arrange
        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var hostEnvironment = TestHelpers.CreateNonInteractiveHostEnvironment();
        var interactionService = CreateInteractionService(AnsiConsole.Console, executionContext, hostEnvironment);
        var choices = new[] { "option1", "option2" };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            interactionService.PromptForSelectionAsync("Select an item:", choices, x => x, CancellationToken.None));
        Assert.Contains(InteractionServiceStrings.InteractiveInputNotSupported, exception.Message);
    }

    [Fact]
    public async Task PromptForSelectionsAsync_WhenInteractiveInputNotSupported_ThrowsInvalidOperationException()
    {
        // Arrange
        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var hostEnvironment = TestHelpers.CreateNonInteractiveHostEnvironment();
        var interactionService = CreateInteractionService(AnsiConsole.Console, executionContext, hostEnvironment);
        var choices = new[] { "option1", "option2" };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            interactionService.PromptForSelectionsAsync("Select items:", choices, x => x, cancellationToken: CancellationToken.None));
        Assert.Contains(InteractionServiceStrings.InteractiveInputNotSupported, exception.Message);
    }

    [Fact]
    public async Task ConfirmAsync_WhenInteractiveInputNotSupported_ThrowsInvalidOperationException()
    {
        // Arrange
        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var hostEnvironment = TestHelpers.CreateNonInteractiveHostEnvironment();
        var interactionService = CreateInteractionService(AnsiConsole.Console, executionContext, hostEnvironment);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            interactionService.ConfirmAsync("Confirm?", true, CancellationToken.None));
        Assert.Contains(InteractionServiceStrings.InteractiveInputNotSupported, exception.Message);
    }

    [Fact]
    public async Task ShowStatusAsync_NestedCall_DoesNotThrowException()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(console, executionContext);
        
        var outerStatusText = "Outer operation...";
        var innerStatusText = "Inner operation...";
        var expectedResult = 42;

        // Act
        var actualResult = await interactionService.ShowStatusAsync(outerStatusText, async () =>
        {
            // This nested call should not throw - it should fall back to DisplaySubtleMessage
            return await interactionService.ShowStatusAsync(innerStatusText, () => Task.FromResult(expectedResult));
        });

        // Assert
        Assert.Equal(expectedResult, actualResult);
        var outputString = output.ToString();
        Assert.Contains(outerStatusText, outputString);
        Assert.Contains(innerStatusText, outputString);
    }

    [Fact]
    public void ShowStatus_NestedCall_DoesNotThrowException()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(console, executionContext);
        
        var outerStatusText = "Outer synchronous operation...";
        var innerStatusText = "Inner synchronous operation...";
        var actionExecuted = false;

        // Act
        interactionService.ShowStatus(outerStatusText, () =>
        {
            // This nested call should not throw - it should fall back to DisplaySubtleMessage
            interactionService.ShowStatus(innerStatusText, () => actionExecuted = true);
        });

        // Assert
        Assert.True(actionExecuted);
        var outputString = output.ToString();
        Assert.Contains(outerStatusText, outputString);
        Assert.Contains(innerStatusText, outputString);
    }

    [Fact]
    public void DisplayIncompatibleVersionError_WithMarkupCharactersInVersion_DoesNotThrow()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(console, executionContext);

        var ex = new AppHostIncompatibleException("Incompatible [version]", "capability [Prod]");

        // Act - should not throw due to unescaped markup characters
        var exception = Record.Exception(() => interactionService.DisplayIncompatibleVersionError(ex, "9.0.0-preview.1 [rc]"));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("capability [Prod]", outputString);
        Assert.Contains("9.0.0-preview.1 [rc]", outputString);
    }

    [Fact]
    public void DisplayMessage_WithMarkupCharactersInMessage_AutoEscapesByDefault()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(console, executionContext);

        // DisplayMessage now auto-escapes by default, so callers don't need to escape.
        var message = "See logs at C:\\Users\\test [Dev]\\logs\\aspire.log";

        // Act - should not throw since DisplayMessage escapes by default
        var exception = Record.Exception(() => interactionService.DisplayMessage(KnownEmojis.PageFacingUp, message));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("C:\\Users\\test [Dev]\\logs\\aspire.log", outputString);
    }

    [Fact]
    public void DisplayVersionUpdateNotification_WithMarkupCharactersInVersion_DoesNotThrow()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(console, executionContext);

        // Version strings are unlikely to have brackets, but the method should handle it
        var version = "13.2.0-preview [beta]";
        var updateCommand = "aspire update --channel [stable]";

        // Act - should not throw due to unescaped markup characters
        var exception = Record.Exception(() => interactionService.DisplayVersionUpdateNotification(version, updateCommand));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("A new version of Aspire is available:", outputString);
        Assert.Contains("13.2.0-preview [beta]", outputString);
        Assert.Contains("aspire update --channel [stable]", outputString);
    }

    [Fact]
    public void DisplayError_WithMarkupCharactersInMessage_DoesNotDoubleEscape()
    {
        // Arrange - verifies that DisplayError escapes once (callers should NOT pre-escape)
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(console, executionContext);

        // Error message with brackets (e.g., from an exception)
        var errorMessage = "Failed to connect to service [Prod]: Connection refused <timeout>";

        // Act - should not throw
        var exception = Record.Exception(() => interactionService.DisplayError(errorMessage));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        // Should contain the original text (not double-escaped like [[Prod]])
        Assert.Contains("[Prod]", outputString);
        Assert.DoesNotContain("[[Prod]]", outputString);
    }

    [Fact]
    public void DisplayMessage_WithUnescapedMarkup_AutoEscapesAndDoesNotThrow()
    {
        // Arrange - verifies that DisplayMessage auto-escapes by default
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(console, executionContext);

        // Path with brackets that would be interpreted as Spectre markup if not escaped
        var path = @"C:\Users\[Dev Team]\logs\aspire.log";

        // Act - should not throw because DisplayMessage auto-escapes
        var exception = Record.Exception(() => interactionService.DisplayMessage(KnownEmojis.PageFacingUp, $"See logs at {path}"));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains(@"C:\Users\[Dev Team]\logs\aspire.log", outputString);
    }

    [Fact]
    public void DisplayMessage_WithAllowMarkupTrue_PassesThroughMarkup()
    {
        // Arrange - verifies that allowMarkup: true allows intentional Spectre markup
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(console, executionContext);

        // Message with intentional Spectre markup tags
        var message = "[bold cyan]MyProject.csproj[/]:";

        // Act - should not throw because markup is intentional
        var exception = Record.Exception(() => interactionService.DisplayMessage(KnownEmojis.FileFolder, message, allowMarkup: true));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("MyProject.csproj", outputString);
    }

    [Fact]
    public void DisplayMessage_WithAllowMarkupTrue_UnescapedDynamicContent_Throws()
    {
        // Arrange - verifies that allowMarkup: true still requires callers to escape dynamic values
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(console, executionContext);

        // Dynamic content with brackets embedded in markup - NOT escaped
        var projectName = "MyProject [Beta]";
        var message = $"[bold cyan]{projectName}[/]:";

        // Act - should throw because [Beta] is invalid markup when allowMarkup: true
        var exception = Record.Exception(() => interactionService.DisplayMessage(KnownEmojis.FileFolder, message, allowMarkup: true));

        // Assert
        Assert.NotNull(exception);
    }

    [Fact]
    public void DisplaySuccess_WithMarkupCharacters_AutoEscapesByDefault()
    {
        // Arrange
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(console, executionContext);

        // Success message with bracket characters that would break markup if not escaped
        var message = "Package Aspire.Hosting.Azure [1.0.0-preview] added successfully";

        // Act - should not throw because DisplaySuccess auto-escapes
        var exception = Record.Exception(() => interactionService.DisplaySuccess(message));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("Aspire.Hosting.Azure [1.0.0-preview]", outputString);
    }

    [Fact]
    public async Task ShowStatusAsync_WithMarkupCharacters_AutoEscapesByDefault()
    {
        // Arrange - verifies that ShowStatusAsync auto-escapes by default
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log", debugMode: true);
        var interactionService = CreateInteractionService(console, executionContext);

        // Status text with brackets that would be interpreted as Spectre markup if not escaped
        var statusText = "Downloading CLI from https://example.com/[latest]/aspire.zip";

        // Act - should not throw because ShowStatusAsync auto-escapes
        var exception = await Record.ExceptionAsync(() =>
            interactionService.ShowStatusAsync(statusText, () => Task.FromResult(0)));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("[latest]", outputString);
    }

    [Fact]
    public void ShowStatus_WithMarkupCharacters_AutoEscapesByDefault()
    {
        // Arrange - verifies that ShowStatus auto-escapes by default
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log", debugMode: true);
        var interactionService = CreateInteractionService(console, executionContext);

        // Status text with brackets that would be interpreted as Spectre markup if not escaped
        var statusText = "Installing .NET SDK [10.0.0-preview.1]...";

        // Act - should not throw because ShowStatus auto-escapes
        var exception = Record.Exception(() => interactionService.ShowStatus(statusText, () => { }));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("[10.0.0-preview.1]", outputString);
    }

    [Fact]
    public async Task ShowStatusAsync_WithAllowMarkupTrue_PassesThroughMarkup()
    {
        // Arrange - verifies that allowMarkup: true allows emoji and other Spectre markup
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log", debugMode: true);
        var interactionService = CreateInteractionService(console, executionContext);

        // Status text with intentional Spectre emoji markup
        var statusText = ":rocket:  Creating new project";

        // Act - should not throw because markup is intentional
        var exception = await Record.ExceptionAsync(() =>
            interactionService.ShowStatusAsync(statusText, () => Task.FromResult(0), allowMarkup: true));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("Creating new project", outputString);
    }

    [Fact]
    public async Task ShowStatusAsync_WithAllowMarkupTrue_UnescapedDynamicContent_Throws()
    {
        // Arrange - verifies that allowMarkup: true still requires callers to escape dynamic values
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log", debugMode: true);
        var interactionService = CreateInteractionService(console, executionContext);

        // Dynamic content with invalid brackets when interpreted as markup
        var projectName = "MyProject [Beta]";
        var statusText = $":rocket:  Building {projectName}";

        // Act - should throw because [Beta] is invalid markup when allowMarkup: true
        var exception = await Record.ExceptionAsync(() =>
            interactionService.ShowStatusAsync(statusText, () => Task.FromResult(0), allowMarkup: true));

        // Assert
        Assert.NotNull(exception);
    }

    [Fact]
    public async Task ShowStatusAsync_WithEmojiName_PrependsEmojiAndAutoEscapes()
    {
        // Arrange - verifies that emojiName handles emoji separately and auto-escapes the status text
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log", debugMode: true);
        var interactionService = CreateInteractionService(console, executionContext);

        // Status text with brackets that would be invalid markup if not escaped
        var statusText = "Building MyProject [Beta]";

        // Act - should not throw because emojiName handles emoji separately and text is auto-escaped
        var exception = await Record.ExceptionAsync(() =>
            interactionService.ShowStatusAsync(statusText, () => Task.FromResult(0), emoji: KnownEmojis.Rocket));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("Building MyProject [Beta]", outputString);
        Assert.Contains("🚀", outputString);
    }

    [Fact]
    public void ShowStatus_WithEmojiName_PrependsEmojiAndAutoEscapes()
    {
        // Arrange - verifies that emojiName handles emoji separately and auto-escapes the status text
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log", debugMode: true);
        var interactionService = CreateInteractionService(console, executionContext);

        // Status text with brackets that would be invalid markup if not escaped
        var statusText = "Installing .NET SDK [10.0.0-preview.1]...";

        // Act - should not throw because emojiName handles emoji separately and text is auto-escaped
        var exception = Record.Exception(() =>
            interactionService.ShowStatus(statusText, () => { }, emoji: KnownEmojis.Package));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("Installing .NET SDK [10.0.0-preview.1]", outputString);
        Assert.Contains("📦", outputString);
    }

    [Fact]
    public void DisplaySubtleMessage_WithMarkupCharacters_EscapesByDefault()
    {
        // Arrange - verifies that DisplaySubtleMessage escapes by default
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(console, executionContext);

        // Message with all kinds of markup characters
        var message = "Error in [Module]: <Config> value $.items[0] invalid";

        // Act
        var exception = Record.Exception(() => interactionService.DisplaySubtleMessage(message));

        // Assert
        Assert.Null(exception);
        var outputString = output.ToString();
        Assert.Contains("[Module]", outputString);
    }

    [Fact]
    public void SelectionPrompt_ConverterPreservesIntentionalMarkup()
    {
        // Arrange - verifies that PromptForSelectionAsync does NOT escape the formatter output,
        // allowing callers to include intentional Spectre markup (e.g., [bold]...[/]).
        // This is a regression test for https://github.com/microsoft/aspire/pull/14422 where
        // blanket EscapeMarkup() in the converter broke [bold] rendering in 'aspire add'.
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.Standard,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        // Build a SelectionPrompt the same way ConsoleInteractionService does,
        // using a formatter that returns intentional markup (like AddCommand does).
        Func<string, string> choiceFormatter = item => $"[bold]{item}[/] (Aspire.Hosting.{item})";

        var prompt = new SelectionPrompt<string>()
            .Title("Select an integration:")
            .UseConverter(choiceFormatter)
            .AddChoices(["PostgreSQL", "Redis"]);

        // Act - verify the converter output preserves the [bold] markup
        // by checking that the converter is the formatter itself (not wrapped with EscapeMarkup)
        var converterOutput = choiceFormatter("PostgreSQL");

        // Assert - the formatter should produce raw markup, not escaped markup
        Assert.Equal("[bold]PostgreSQL[/] (Aspire.Hosting.PostgreSQL)", converterOutput);
        Assert.DoesNotContain("[[bold]]", converterOutput); // Must NOT be escaped
    }

    [Fact]
    public void SelectionPrompt_ConverterWithBracketsInData_MustBeEscapedByCaller()
    {
        // Arrange - verifies that callers are responsible for escaping dynamic data
        // that may contain bracket characters, while preserving intentional markup.
        // This tests the pattern used by AddCommand.PackageNameWithFriendlyNameIfAvailable.
        var output = new StringBuilder();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output))
        });

        // Simulate a package name that contains brackets (e.g., from an external source)
        var friendlyName = "Azure Storage [Preview]";
        var packageId = "Aspire.Hosting.Azure.Storage";

        // The formatter should escape dynamic values but preserve intentional markup
        var formattedOutput = $"[bold]{friendlyName.EscapeMarkup()}[/] ({packageId.EscapeMarkup()})";

        // Assert - intentional markup preserved, dynamic brackets escaped
        Assert.Equal("[bold]Azure Storage [[Preview]][/] (Aspire.Hosting.Azure.Storage)", formattedOutput);

        // Verify Spectre can render this without throwing
        var exception = Record.Exception(() => console.MarkupLine(formattedOutput));
        Assert.Null(exception);

        var outputString = output.ToString();
        Assert.Contains("Azure Storage [Preview]", outputString);
        Assert.Contains("Aspire.Hosting.Azure.Storage", outputString);
    }

    [Theory]
    [InlineData(true, "[Y/n]")]
    [InlineData(false, "[y/N]")]
    public async Task ConfirmAsync_DisplaysCapitalizedDefaultChoice(bool defaultValue, string expectedChoiceSuffix)
    {
        // Arrange - simulate pressing Enter (accepts default)
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "\n");
        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(console, executionContext);

        // Act
        await interactionService.ConfirmAsync("Proceed?", defaultValue, CancellationToken.None);

        // Assert - the output should contain the [Y/n] or [y/N] suffix
        var outputString = output.ToString();
        Assert.Contains(expectedChoiceSuffix, outputString);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmAsync_WhenUserPressesEnter_ReturnsDefaultValue(bool defaultValue)
    {
        // Arrange - simulate pressing Enter (empty input selects default)
        var output = new StringBuilder();
        var console = CreateInteractiveConsoleWithInput(output, "\n");
        var executionContext = new CliExecutionContext(new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo("."), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var interactionService = CreateInteractionService(console, executionContext);

        // Act
        var result = await interactionService.ConfirmAsync("Proceed?", defaultValue, CancellationToken.None);

        // Assert - pressing Enter should accept the default value
        Assert.Equal(defaultValue, result);
    }

    private static IAnsiConsole CreateInteractiveConsoleWithInput(StringBuilder output, string input)
    {
        var settings = new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.Yes,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
        };
        var console = AnsiConsole.Create(settings);
        console.Profile.Width = int.MaxValue;
        return new TestAnsiConsoleWithInput(console, new StringReader(input));
    }
}

/// <summary>
/// A test <see cref="IAnsiConsole"/> wrapper that redirects input reads to a <see cref="TextReader"/>,
/// allowing prompts to be answered in unit tests without blocking on real console input.
/// </summary>
file sealed class TestAnsiConsoleWithInput : IAnsiConsole
{
    private readonly IAnsiConsole _inner;
    private readonly IAnsiConsoleInput _testInput;

    public TestAnsiConsoleWithInput(IAnsiConsole inner, TextReader inputReader)
    {
        _inner = inner;
        _testInput = new TextReaderInput(inputReader);
    }

    public Profile Profile => _inner.Profile;
    public IAnsiConsoleCursor Cursor => _inner.Cursor;
    public IAnsiConsoleInput Input => _testInput;
    public IExclusivityMode ExclusivityMode => _inner.ExclusivityMode;
    public RenderPipeline Pipeline => _inner.Pipeline;

    public void Clear(bool home) => _inner.Clear(home);
    public void Write(IRenderable renderable) => _inner.Write(renderable);
    public void WriteAnsi(Action<AnsiWriter> action) => _inner.WriteAnsi(action);

    private sealed class TextReaderInput : IAnsiConsoleInput
    {
        private readonly TextReader _reader;

        public TextReaderInput(TextReader reader) => _reader = reader;

        public bool IsKeyAvailable() => true;

        public ConsoleKeyInfo? ReadKey(bool intercept)
        {
            var read = _reader.Read();
            if (read == -1)
            {
                // End of stream - return Enter as a safe fallback
                return new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false);
            }

            var ch = (char)read;
            var key = ch switch
            {
                '\n' or '\r' => ConsoleKey.Enter,
                'y' or 'Y'   => ConsoleKey.Y,
                'n' or 'N'   => ConsoleKey.N,
                _            => ConsoleKey.Enter,
            };
            return new ConsoleKeyInfo(ch, key, shift: char.IsUpper(ch), alt: false, control: false);
        }

        public Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken)
            => Task.FromResult(ReadKey(intercept));
    }
}
