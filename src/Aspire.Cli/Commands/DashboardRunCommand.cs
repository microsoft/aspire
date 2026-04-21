// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Bundles;
using Aspire.Cli.Configuration;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Layout;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command that starts a standalone Aspire Dashboard instance.
/// </summary>
internal sealed class DashboardRunCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.Monitoring;

    private readonly IInteractionService _interactionService;
    private readonly IBundleService _bundleService;
    private readonly LayoutProcessRunner _layoutProcessRunner;
    private readonly FileLoggerProvider _fileLoggerProvider;
    private readonly ILogger<DashboardRunCommand> _logger;

    private static readonly Option<string?> s_frontendUrlOption = new("--frontend-url")
    {
        Description = DashboardCommandStrings.FrontendUrlOptionDescription
    };

    private static readonly Option<string?> s_otlpGrpcUrlOption = new("--otlp-grpc-url")
    {
        Description = DashboardCommandStrings.OtlpGrpcUrlOptionDescription
    };

    private static readonly Option<string?> s_otlpHttpUrlOption = new("--otlp-http-url")
    {
        Description = DashboardCommandStrings.OtlpHttpUrlOptionDescription
    };

    private static readonly Option<bool> s_allowAnonymousOption = new("--allow-anonymous")
    {
        Description = DashboardCommandStrings.AllowAnonymousOptionDescription
    };

    private static readonly Option<string?> s_configFilePathOption = new("--config-file-path")
    {
        Description = DashboardCommandStrings.ConfigFilePathOptionDescription
    };

    public DashboardRunCommand(
        IInteractionService interactionService,
        IBundleService bundleService,
        LayoutProcessRunner layoutProcessRunner,
        FileLoggerProvider fileLoggerProvider,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        ILogger<DashboardRunCommand> logger,
        AspireCliTelemetry telemetry)
        : base("run", DashboardCommandStrings.RunDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _bundleService = bundleService;
        _layoutProcessRunner = layoutProcessRunner;
        _fileLoggerProvider = fileLoggerProvider;
        _logger = logger;

        Options.Add(s_frontendUrlOption);
        Options.Add(s_otlpGrpcUrlOption);
        Options.Add(s_otlpHttpUrlOption);
        Options.Add(s_allowAnonymousOption);
        Options.Add(s_configFilePathOption);
        TreatUnmatchedTokensAsErrors = false;
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var layout = await _bundleService.EnsureExtractedAndGetLayoutAsync(cancellationToken).ConfigureAwait(false);
        if (layout is null)
        {
            _interactionService.DisplayError(DashboardCommandStrings.BundleLayoutNotFound);
            return ExitCodeConstants.DashboardFailure;
        }

        var managedPath = layout.GetManagedPath();
        if (managedPath is null || !File.Exists(managedPath))
        {
            _interactionService.DisplayError(DashboardCommandStrings.ManagedBinaryNotFound);
            return ExitCodeConstants.DashboardFailure;
        }

        var dashboardArgs = new List<string> { "dashboard" };

        // Build args from typed options. These are added before unmatched tokens
        // so that raw pass-through arguments (unmatched tokens) take precedence.
        var unmatchedTokens = parseResult.UnmatchedTokens;
        var allowAnonymous = parseResult.GetValue(s_allowAnonymousOption);
        AddOptionArgs(parseResult, dashboardArgs, unmatchedTokens, ExecutionContext);

        // Set a browser token for frontend auth unless anonymous access is enabled.
        // Tokens and keys are passed via environment variables (not command-line args)
        // to avoid exposing them in process listings (e.g. ps, Task Manager).
        string? browserToken = null;
        var environmentVariables = new Dictionary<string, string>();
        if (!allowAnonymous && !ConfigSettingHasValue(unmatchedTokens, ExecutionContext, KnownConfigNames.DashboardUnsecuredAllowAnonymous))
        {
            if (!ConfigSettingHasValue(unmatchedTokens, ExecutionContext, DashboardConfigNames.DashboardFrontendBrowserTokenName.EnvVarName))
            {
                browserToken = TokenGenerator.GenerateToken();
                environmentVariables[DashboardConfigNames.DashboardFrontendBrowserTokenName.EnvVarName] = browserToken;
            }

            // Enable API key authentication for the telemetry API so that only
            // callers who possess the key (or the browser token) can query it.
            if (!ConfigSettingHasValue(unmatchedTokens, ExecutionContext, DashboardConfigNames.DashboardApiPrimaryApiKeyName.EnvVarName))
            {
                var apiKey = TokenGenerator.GenerateToken();
                environmentVariables[DashboardConfigNames.DashboardApiPrimaryApiKeyName.EnvVarName] = apiKey;

                if (!ConfigSettingHasValue(unmatchedTokens, ExecutionContext, DashboardConfigNames.DashboardApiAuthModeName.EnvVarName))
                {
                    environmentVariables[DashboardConfigNames.DashboardApiAuthModeName.EnvVarName] = "ApiKey";
                }
            }
        }

        dashboardArgs.AddRange(unmatchedTokens);

        // Resolve URLs for the summary display.
        var dashboardInfo = ResolveDashboardInfo(dashboardArgs, unmatchedTokens, ExecutionContext, browserToken);

        return await ExecuteForegroundAsync(managedPath, dashboardArgs, dashboardInfo, environmentVariables, cancellationToken).ConfigureAwait(false);
    }

    private static void AddOptionArgs(ParseResult parseResult, List<string> args, IReadOnlyList<string> unmatchedTokens, CliExecutionContext executionContext)
    {
        AddStringOptionArg(parseResult, args, unmatchedTokens, executionContext, s_frontendUrlOption, KnownConfigNames.AspNetCoreUrls, defaultValue: "http://localhost:18888");
        AddStringOptionArg(parseResult, args, unmatchedTokens, executionContext, s_otlpGrpcUrlOption, KnownConfigNames.DashboardOtlpGrpcEndpointUrl, defaultValue: "http://localhost:4317");
        AddStringOptionArg(parseResult, args, unmatchedTokens, executionContext, s_otlpHttpUrlOption, KnownConfigNames.DashboardOtlpHttpEndpointUrl, defaultValue: "http://localhost:4318");
        AddBoolOptionArg(parseResult, args, unmatchedTokens, executionContext, s_allowAnonymousOption, KnownConfigNames.DashboardUnsecuredAllowAnonymous);

        // Always enable the telemetry API so CLI commands (e.g. aspire otel) can query the dashboard,
        // unless the user has explicitly configured either the enabled or disabled setting.
        if (!ConfigSettingHasValue(unmatchedTokens, executionContext, KnownConfigNames.DashboardApiEnabled) &&
            !ConfigSettingHasValue(unmatchedTokens, executionContext, KnownConfigNames.DashboardApiDisabled))
        {
            args.Add($"--{KnownConfigNames.DashboardApiEnabled}=true");
        }

        AddStringOptionArg(parseResult, args, unmatchedTokens, executionContext, s_configFilePathOption, KnownConfigNames.DashboardConfigFilePath, defaultValue: null);
    }

    private static void AddStringOptionArg(ParseResult parseResult, List<string> args, IReadOnlyList<string> unmatchedTokens,
        CliExecutionContext executionContext, Option<string?> option, string envVarName, string? defaultValue)
    {
        if (ConfigSettingHasValue(unmatchedTokens, executionContext, envVarName))
        {
            return;
        }

        var value = parseResult.GetResult(option) is not null
            ? parseResult.GetValue(option)
            : defaultValue;

        if (value is not null)
        {
            args.Add($"--{envVarName}={value}");
        }
    }

    private static void AddBoolOptionArg(ParseResult parseResult, List<string> args, IReadOnlyList<string> unmatchedTokens,
        CliExecutionContext executionContext, Option<bool> option, string envVarName, bool? defaultValue = null)
    {
        if (ConfigSettingHasValue(unmatchedTokens, executionContext, envVarName))
        {
            return;
        }

        var result = parseResult.GetResult(option);

        // When the user explicitly specified the option, use their value.
        // When a defaultValue is provided and the user did not specify the option, use the default.
        // Without a defaultValue, skip when the result comes from the option's default value rather
        // than explicit user input, to avoid always emitting e.g. "--ALLOW_ANONYMOUS=false".
        if (result is not null && !result.Implicit)
        {
            var value = parseResult.GetValue(option);
            args.Add($"--{envVarName}={value.ToString().ToLowerInvariant()}");
        }
        else if (defaultValue is not null)
        {
            args.Add($"--{envVarName}={defaultValue.Value.ToString().ToLowerInvariant()}");
        }
    }

    internal static bool ConfigSettingHasValue(IReadOnlyList<string> unmatchedTokens, CliExecutionContext executionContext, string envVarName)
    {
        // Check if already provided via unmatched tokens.
        var prefix = $"--{envVarName}=";
        for (var i = 0; i < unmatchedTokens.Count; i++)
        {
            if (unmatchedTokens[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Also handle bare "--KEY" (boolean flag) or "--KEY value" (space-separated) forms.
            // A bare key is treated as present because it will be forwarded to the
            // child process via AddRange(unmatchedTokens), which uses last-wins
            // semantics. For booleans (e.g. "--ALLOW_ANONYMOUS") it means true;
            // for strings the bare key overrides our default regardless.
            if (unmatchedTokens[i].Equals($"--{envVarName}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Check if already set as an environment variable.
        if (executionContext.GetEnvironmentVariable(envVarName) is not null)
        {
            return true;
        }

        return false;
    }

    internal static DashboardInfo ResolveDashboardInfo(List<string> dashboardArgs, IReadOnlyList<string> unmatchedTokens, CliExecutionContext executionContext, string? browserToken)
    {
        var frontendUrl = ResolveSettingValue(dashboardArgs, unmatchedTokens, executionContext, KnownConfigNames.AspNetCoreUrls) ?? "http://localhost:18888";
        var otlpGrpcUrl = ResolveSettingValue(dashboardArgs, unmatchedTokens, executionContext, KnownConfigNames.DashboardOtlpGrpcEndpointUrl) ?? "http://localhost:4317";
        var otlpHttpUrl = ResolveSettingValue(dashboardArgs, unmatchedTokens, executionContext, KnownConfigNames.DashboardOtlpHttpEndpointUrl) ?? "http://localhost:4318";

        // Take the first URL if multiple are specified (semicolon-separated).
        var parts = frontendUrl.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var firstUrl = parts.Length > 0 ? parts[0].TrimEnd('/') : "http://localhost:18888";

        var dashboardUrl = browserToken is not null
            ? $"{firstUrl}/login?t={browserToken}"
            : firstUrl;

        return new DashboardInfo(dashboardUrl, otlpGrpcUrl, otlpHttpUrl);
    }

    /// <summary>
    /// Resolves a setting value by checking, in order: args (--KEY=value), unmatched tokens
    /// (--KEY value with space separator), and environment variables.
    /// </summary>
    internal static string? ResolveSettingValue(List<string> args, IReadOnlyList<string> unmatchedTokens, CliExecutionContext executionContext, string key)
    {
        // First check --KEY=value in args (last-wins).
        var result = ResolveArgValue(args, key);
        if (result is not null)
        {
            return result;
        }

        // Check unmatched tokens for space-separated form: --KEY value
        var bareKey = $"--{key}";
        for (var i = 0; i < unmatchedTokens.Count; i++)
        {
            if (unmatchedTokens[i].Equals(bareKey, StringComparison.OrdinalIgnoreCase) && i + 1 < unmatchedTokens.Count)
            {
                return unmatchedTokens[i + 1];
            }
        }

        // Fall back to environment variable.
        return executionContext.GetEnvironmentVariable(key);
    }

    internal static string? ResolveArgValue(List<string> args, string key)
    {
        // Scan for --KEY=value (last-wins).
        string? result = null;
        var prefix = $"--{key}=";
        foreach (var arg in args)
        {
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                result = arg.Substring(prefix.Length);
            }
        }

        return result;
    }

    internal sealed record DashboardInfo(string DashboardUrl, string OtlpGrpcUrl, string OtlpHttpUrl);

    private static string GetExitCodeMessage(int exitCode)
    {
        return exitCode switch
        {
            DashboardExitCodes.UnexpectedError => DashboardCommandStrings.DashboardExitedUnexpectedError,
            DashboardExitCodes.ValidationFailure => DashboardCommandStrings.DashboardExitedValidationFailure,
            DashboardExitCodes.AddressInUse => DashboardCommandStrings.DashboardExitedAddressInUse,
            _ => string.Format(CultureInfo.CurrentCulture, DashboardCommandStrings.DashboardExitedWithError, exitCode),
        };
    }

    private void RenderDashboardSummary(DashboardInfo info, string logFilePath)
    {
        _interactionService.DisplayEmptyLine();
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();

        var dashboardLabel = DashboardCommandStrings.DashboardLabel;
        var otlpGrpcLabel = DashboardCommandStrings.OtlpGrpcLabel;
        var otlpHttpLabel = DashboardCommandStrings.OtlpHttpLabel;
        var logsLabel = DashboardCommandStrings.LogsLabel;

        var labels = new List<string> { dashboardLabel, otlpGrpcLabel, otlpHttpLabel, logsLabel };

        var longestLabelLength = labels.Max(s => s.Length) + 1; // +1 for colon
        grid.Columns[0].Width = longestLabelLength;

        // Dashboard row
        var escapedDashboardUrl = Markup.Escape(info.DashboardUrl);
        grid.AddRow(
            new Align(new Markup($"[bold green]{dashboardLabel}[/]:"), HorizontalAlignment.Right),
            new Markup($"[link={escapedDashboardUrl}]{escapedDashboardUrl}[/]"));
        grid.AddRow(Text.Empty, Text.Empty);

        // OTLP gRPC row
        grid.AddRow(
            new Align(new Markup($"[bold green]{otlpGrpcLabel}[/]:"), HorizontalAlignment.Right),
            new Text(info.OtlpGrpcUrl));
        grid.AddRow(Text.Empty, Text.Empty);

        // OTLP HTTP row
        grid.AddRow(
            new Align(new Markup($"[bold green]{otlpHttpLabel}[/]:"), HorizontalAlignment.Right),
            new Text(info.OtlpHttpUrl));
        grid.AddRow(Text.Empty, Text.Empty);

        // Logs row
        grid.AddRow(
            new Align(new Markup($"[bold green]{logsLabel}[/]:"), HorizontalAlignment.Right),
            new Text(logFilePath));

        var padder = new Padder(grid, new Padding(3, 0));
        _interactionService.DisplayRenderable(padder);
    }

    private async Task<int> ExecuteForegroundAsync(string managedPath, List<string> dashboardArgs, DashboardInfo dashboardInfo, IDictionary<string, string>? environmentVariables, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting dashboard in foreground: {ManagedPath}", managedPath);

        var outputCollector = new OutputCollector(_fileLoggerProvider, "Dashboard");
        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var options = new ProcessInvocationOptions
        {
            StandardOutputCallback = line =>
            {
                outputCollector.AppendOutput(line);

                // The dashboard writes "Now listening on: {urls}" when it's ready to accept requests. 
                // Wait for that message before showing the dashboard URL to the user.
                // This message isn't localized, so we can reliably look for it in the output regardless of the user's language/locale.
                if (line.Contains("Now listening on:", StringComparison.OrdinalIgnoreCase))
                {
                    readyTcs.TrySetResult();
                }
            },
            StandardErrorCallback = line =>
            {
                outputCollector.AppendError(line);
            },
        };

        IProcessExecution process;
        try
        {
            process = _layoutProcessRunner.Start(managedPath, dashboardArgs, environmentVariables: environmentVariables, options: options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start dashboard process: {ManagedPath}", managedPath);
            _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, DashboardCommandStrings.DashboardFailedToStart, ex.Message));
            return ExitCodeConstants.DashboardFailure;
        }

        using var _ = process;

        // Wait for the dashboard to become ready, the process to exit, or a timeout.
        var processExitTask = process.WaitForExitAsync(cancellationToken);
        var readyOrFailed = Task.WhenAny(readyTcs.Task, processExitTask);

        var completedTask = await _interactionService.ShowStatusAsync(
            DashboardCommandStrings.StartingDashboard,
            async () =>
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                try
                {
                    return await readyOrFailed.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // Timeout — return the processExitTask so the caller detects it wasn't the ready signal.
                    return processExitTask;
                }
            });

        if (cancellationToken.IsCancellationRequested)
        {
            _interactionService.DisplayMessage(KnownEmojis.StopSign, $"[teal bold]{DashboardCommandStrings.StoppingDashboard}[/]", allowMarkup: true);

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return ExitCodeConstants.Success;
        }

        if (completedTask != readyTcs.Task)
        {
            // Observe the processExitTask to avoid unobserved task exceptions.
            if (process.HasExited)
            {
                try
                {
                    await processExitTask.ConfigureAwait(false);
                }
                catch
                {
                    /* already handled via ExitCode below */
                }
            }

            // Dashboard didn't become ready — either it exited or timed out.
            var exitMessage = process.HasExited
                ? GetExitCodeMessage(process.ExitCode)
                : DashboardCommandStrings.DashboardStartTimedOut;

            _interactionService.DisplayError(exitMessage);
            _interactionService.DisplayMessage(KnownEmojis.PageFacingUp, string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeLogsAt, ExecutionContext.LogFilePath));

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return ExitCodeConstants.DashboardFailure;
        }

        // Dashboard is ready.
        RenderDashboardSummary(dashboardInfo, ExecutionContext.LogFilePath);
        _interactionService.DisplayEmptyLine();

        try
        {
            await processExitTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _interactionService.DisplayMessage(KnownEmojis.StopSign, $"[teal bold]{DashboardCommandStrings.StoppingDashboard}[/]", allowMarkup: true);

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return ExitCodeConstants.Success;
        }

        if (process.ExitCode != 0)
        {
            _interactionService.DisplayError(GetExitCodeMessage(process.ExitCode));
        }

        return process.ExitCode == 0 ? ExitCodeConstants.Success : ExitCodeConstants.DashboardFailure;
    }
}
