// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Aspire.Cli.Interaction;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

internal sealed class FeedbackCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    private static readonly Argument<FeedbackIssueKind?> s_kindArgument = new("kind")
    {
        Description = SharedCommandStrings.FeedbackKindArgumentDescription,
        Arity = ArgumentArity.ZeroOrOne
    };

    private static readonly Option<string?> s_titleOption = new("--title")
    {
        Description = SharedCommandStrings.FeedbackTitleOptionDescription
    };

    private static readonly Option<string?> s_bodyOption = new("--body")
    {
        Description = SharedCommandStrings.FeedbackBodyOptionDescription
    };

    private readonly IAppHostProjectFactory _projectFactory;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly IAppHostInfoResolver _appHostInfoResolver;
    private readonly IDotNetSdkInstaller _dotNetSdkInstaller;
    private readonly ILogger<FeedbackCommand> _logger;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly IProcessExecutionFactory _processExecutionFactory;

    public FeedbackCommand(
        IAppHostProjectFactory projectFactory,
        ILanguageDiscovery languageDiscovery,
        IAppHostInfoResolver appHostInfoResolver,
        IDotNetSdkInstaller dotNetSdkInstaller,
        ILogger<FeedbackCommand> logger,
        IProcessExecutionFactory processExecutionFactory,
        CommonCommandServices services)
        : base("feedback", SharedCommandStrings.FeedbackCommandDescription, services)
    {
        _projectFactory = projectFactory;
        _languageDiscovery = languageDiscovery;
        _appHostInfoResolver = appHostInfoResolver;
        _dotNetSdkInstaller = dotNetSdkInstaller;
        _logger = logger;
        _processExecutionFactory = processExecutionFactory;
        _hostEnvironment = services.HostEnvironment;

        Arguments.Add(s_kindArgument);
        Options.Add(s_titleOption);
        Options.Add(s_bodyOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var kind = await ResolveKindAsync(parseResult.GetValue(s_kindArgument), cancellationToken).ConfigureAwait(false);
        if (kind is null)
        {
            DisplayFeedbackLink(FeedbackIssueUrlBuilder.BuildChooserUrl());
            return CommandResult.Success();
        }

        var title = await ResolveTitleAsync(kind.Value, parseResult, cancellationToken).ConfigureAwait(false);
        var body = await ResolveBodyAsync(kind.Value, parseResult, cancellationToken).ConfigureAwait(false);
        var additionalContext = await BuildAdditionalContextAsync(kind.Value, cancellationToken).ConfigureAwait(false);
        var doctorOutput = kind.Value is FeedbackIssueKind.Bug
            ? await InteractionService.ShowStatusAsync(
                SharedCommandStrings.FeedbackCaptureDoctorStatus,
                () => CaptureDoctorOutputAsync(cancellationToken)).ConfigureAwait(false)
            : null;

        var url = FeedbackIssueUrlBuilder.BuildUrl(new FeedbackIssueContext(
            Kind: kind.Value,
            Title: title,
            MainText: body,
            AspireDoctorOutput: doctorOutput,
            AdditionalContext: additionalContext));

        DisplayFeedbackLink(url);

        return CommandResult.Success();
    }

    private void DisplayFeedbackLink(string url)
    {
        InteractionService.DisplayMessage(
            KnownEmojis.Information,
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.FeedbackLinkMessage, MarkupHelpers.SafeLink(InteractionService, url)),
            allowMarkup: true);
    }

    private async Task<FeedbackIssueKind?> ResolveKindAsync(FeedbackIssueKind? kind, CancellationToken cancellationToken)
    {
        if (kind is not null)
        {
            return kind;
        }

        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            return null;
        }

        var choices = new[]
        {
            new FeedbackKindChoice(FeedbackIssueKind.Bug, SharedCommandStrings.FeedbackKindChoiceBug),
            new FeedbackKindChoice(FeedbackIssueKind.Idea, SharedCommandStrings.FeedbackKindChoiceIdea),
            new FeedbackKindChoice(FeedbackIssueKind.General, SharedCommandStrings.FeedbackKindChoiceGeneral)
        };

        var selected = await InteractionService.PromptForSelectionAsync(
            SharedCommandStrings.FeedbackKindPrompt,
            choices,
            static choice => choice.Text,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return selected.Kind;
    }

    private async Task<string> ResolveTitleAsync(FeedbackIssueKind kind, ParseResult parseResult, CancellationToken cancellationToken)
    {
        var defaultTitle = kind switch
        {
            FeedbackIssueKind.Bug => SharedCommandStrings.FeedbackDefaultBugTitle,
            FeedbackIssueKind.Idea => SharedCommandStrings.FeedbackDefaultIdeaTitle,
            _ => SharedCommandStrings.FeedbackDefaultGeneralTitle
        };

        return await InteractionService.PromptForStringAsync(
            SharedCommandStrings.FeedbackTitlePrompt,
            required: true,
            binding: PromptBinding.Create(parseResult, s_titleOption, defaultTitle),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ResolveBodyAsync(FeedbackIssueKind kind, ParseResult parseResult, CancellationToken cancellationToken)
    {
        var prompt = kind switch
        {
            FeedbackIssueKind.Bug => SharedCommandStrings.FeedbackBugBodyPrompt,
            FeedbackIssueKind.Idea => SharedCommandStrings.FeedbackIdeaBodyPrompt,
            _ => SharedCommandStrings.FeedbackGeneralBodyPrompt
        };

        return await InteractionService.PromptForStringAsync(
            prompt,
            required: true,
            binding: PromptBinding.Create(parseResult, s_bodyOption),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> BuildAdditionalContextAsync(FeedbackIssueKind kind, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine("- Posted from: CLI");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Aspire version: {ExecutionContext.IdentityVersion}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Operating system: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");

        if (kind is FeedbackIssueKind.Bug && TryResolveConfiguredAppHostFile(ExecutionContext.WorkingDirectory) is { } appHostFile)
        {
            var appHostStack = await GetAppHostStackAsync(appHostFile, cancellationToken).ConfigureAwait(false);
            builder.AppendLine(CultureInfo.InvariantCulture, $"- AppHost: {appHostStack}");
        }

        return builder.ToString();
    }

    private static FileInfo? TryResolveConfiguredAppHostFile(DirectoryInfo startDirectory)
    {
        for (var currentDirectory = startDirectory; currentDirectory is not null; currentDirectory = currentDirectory.Parent)
        {
            var config = AspireConfigFile.Load(currentDirectory.FullName);
            if (config?.AppHost?.Path is not { Length: > 0 } configuredPath)
            {
                continue;
            }

            var appHostPath = Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(currentDirectory.FullName, PathNormalizer.NormalizePathForCurrentPlatform(configuredPath));

            return File.Exists(appHostPath) ? new FileInfo(appHostPath) : null;
        }

        return null;
    }

    private async Task<string> GetAppHostStackAsync(FileInfo appHostFile, CancellationToken cancellationToken)
    {
        if (_languageDiscovery.GetLanguageByFile(appHostFile) is not { } language)
        {
            return $"Unknown ({appHostFile.Name})";
        }

        var project = _projectFactory.GetProject(language);
        if (language.LanguageId.Value.Equals(KnownLanguageId.CSharp, StringComparison.OrdinalIgnoreCase))
        {
            var (_, highestDetectedVersion, _) = await _dotNetSdkInstaller.CheckAsync(cancellationToken).ConfigureAwait(false);
            var info = await _appHostInfoResolver.GetAppHostInfoAsync(appHostFile, cancellationToken).ConfigureAwait(false);
            var target = info.TargetFramework ?? info.TargetFrameworks;
            return $"{project.DisplayName} on .NET SDK {highestDetectedVersion ?? "unknown"}{(target is not null ? $" targeting `{target}`" : string.Empty)}";
        }

        if (TypeScriptAppHostToolchainResolver.IsTypeScriptLanguage(language))
        {
            var toolchain = TypeScriptAppHostToolchainResolver.Resolve(appHostFile.Directory ?? ExecutionContext.WorkingDirectory, _logger);
            var runtimeVersion = await CaptureCommandVersionAsync(
                TypeScriptAppHostToolchainResolver.GetCommandName(toolchain),
                "--version",
                cancellationToken).ConfigureAwait(false);
            return $"TypeScript on {TypeScriptAppHostToolchainResolver.GetDisplayName(toolchain)} {runtimeVersion ?? "unknown"}";
        }

        return project.DisplayName;
    }

    private async Task<string?> CaptureCommandVersionAsync(string fileName, string versionArgument, CancellationToken cancellationToken)
    {
        var result = await CaptureProcessOutputAsync(fileName, [versionArgument], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            _logger.LogDebug("Could not capture version from '{FileName} {VersionArgument}': {FailureMessage}", fileName, versionArgument, result.FailureMessage);
            return null;
        }

        return result.Output.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
    }

    private async Task<string?> CaptureDoctorOutputAsync(CancellationToken cancellationToken)
    {
        if (Environment.ProcessPath is not { Length: > 0 } processPath)
        {
            return SharedCommandStrings.FeedbackDoctorProcessPathUnavailable;
        }

        var result = await CaptureProcessOutputAsync(processPath, ["doctor", "--format", "json"], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (result.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Output))
        {
            return string.Format(CultureInfo.InvariantCulture, SharedCommandStrings.FeedbackDoctorCaptureFailed, result.FailureMessage ?? $"exit code {result.ExitCode}");
        }

        // `aspire doctor --format json` writes clean JSON to stdout; progress text goes to stderr
        // (drained and discarded by the capture), so the captured stdout can be used directly.
        return result.Output.Trim();
    }

    private async Task<(int ExitCode, string Output, string? FailureMessage, bool Cancelled)> CaptureProcessOutputAsync(
        string fileName,
        string[] arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var outputBuilder = new StringBuilder();

        var options = new ProcessInvocationOptions
        {
            // Diagnostics-capture spawns are noisy and best-effort, so don't surface them in the CLI log.
            SuppressLogging = true,
            // Capture stdout only. The execution still consumes stderr on its internal pump (so a child
            // that floods stderr can't deadlock on a full pipe buffer), but we discard it here so callers
            // such as `aspire doctor --format json` and `<tool> --version` get a clean stdout payload.
            StandardOutputCallback = line => outputBuilder.AppendLine(line)
        };

        IProcessExecution execution;
        try
        {
            execution = _processExecutionFactory.CreateExecution(
                fileName,
                arguments,
                env: null,
                ExecutionContext.WorkingDirectory,
                options);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogDebug(ex, "Could not create process execution for '{FileName}'.", fileName);
            return (-1, string.Empty, ex.Message, false);
        }

        await using (execution.ConfigureAwait(false))
        {
            try
            {
                // Start() spawns the child and throws (rather than returning false) when the executable is
                // missing or cannot be launched, so a graceful "tool not installed" result lives here.
                execution.Start();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                _logger.LogDebug(ex, "Could not start process '{FileName}'.", fileName);
                return (-1, string.Empty, ex.Message, false);
            }

            // IProcessExecution.WaitForExitAsync has no built-in timeout, so impose one with a linked CTS.
            // Distinguishing a timeout from caller cancellation matters: CaptureDoctorOutputAsync rethrows
            // on caller cancellation but surfaces a failure string on timeout. WaitForExitAsync already
            // kills the child and drains its output before propagating the OperationCanceledException.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                var exitCode = await execution.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                return (exitCode, outputBuilder.ToString().Trim(), null, false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return (-1, outputBuilder.ToString().Trim(), null, Cancelled: true);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                return (-1, outputBuilder.ToString().Trim(), $"Process timed out after {timeout.TotalSeconds:F1}s.", false);
            }
        }
    }

    private sealed record FeedbackKindChoice(FeedbackIssueKind Kind, string Text);
}
