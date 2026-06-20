// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
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

    private static readonly Argument<string> s_kindArgument = new("kind")
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

    static FeedbackCommand()
    {
        s_kindArgument.AcceptOnlyFromAmong("bug", "idea", "general");
    }

    public FeedbackCommand(
        IAppHostProjectFactory projectFactory,
        ILanguageDiscovery languageDiscovery,
        IAppHostInfoResolver appHostInfoResolver,
        IDotNetSdkInstaller dotNetSdkInstaller,
        ILogger<FeedbackCommand> logger,
        CommonCommandServices services)
        : base("feedback", SharedCommandStrings.FeedbackCommandDescription, services)
    {
        _projectFactory = projectFactory;
        _languageDiscovery = languageDiscovery;
        _appHostInfoResolver = appHostInfoResolver;
        _dotNetSdkInstaller = dotNetSdkInstaller;
        _logger = logger;
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

    private async Task<FeedbackIssueKind?> ResolveKindAsync(string? kind, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(kind))
        {
            return ParseKind(kind);
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

    private static FeedbackIssueKind ParseKind(string kind)
    {
        return kind.ToLowerInvariant() switch
        {
            "bug" => FeedbackIssueKind.Bug,
            "idea" => FeedbackIssueKind.Idea,
            "general" => FeedbackIssueKind.General,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
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

        var result = await CaptureProcessOutputAsync(processPath, ["doctor", "--no-logo"], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (result.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Output))
        {
            return string.Format(CultureInfo.InvariantCulture, SharedCommandStrings.FeedbackDoctorCaptureFailed, result.FailureMessage ?? $"exit code {result.ExitCode}");
        }

        return result.Output.Trim();
    }

    private async Task<(int ExitCode, string Output, string? FailureMessage, bool Cancelled)> CaptureProcessOutputAsync(
        string fileName,
        string[] arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = ExecutionContext.WorkingDirectory.FullName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var result = await ProcessCaptureRunner.RunAsync(
            startInfo,
            timeout,
            async (process, token) =>
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(token);
                var errorTask = process.StandardError.ReadToEndAsync(token);
                await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(errorTask.Result))
                {
                    return outputTask.Result;
                }

                return string.IsNullOrWhiteSpace(outputTask.Result)
                    ? errorTask.Result
                    : string.Concat(outputTask.Result, Environment.NewLine, errorTask.Result);
            },
            static () => string.Empty,
            _logger,
            cancellationToken).ConfigureAwait(false);

        return (result.ExitCode, result.Capture, result.FailureMessage, result.Cancelled);
    }

    private sealed record FeedbackKindChoice(FeedbackIssueKind Kind, string Text);
}
