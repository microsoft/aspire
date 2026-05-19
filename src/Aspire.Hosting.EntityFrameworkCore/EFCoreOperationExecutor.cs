// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETTOOL

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Executes EF Core design-time operations by running a DotnetToolResource.
/// </summary>
/// <remarks>
/// This class uses a pre-created DotnetToolResource to execute EF Core operations through Aspire's orchestration.
/// The tool is automatically downloaded and run. The target project must reference Microsoft.EntityFrameworkCore.Design.
/// </remarks>
internal sealed class EFCoreOperationExecutor : IDisposable
{
    private readonly ProjectResource _startupProjectResource;
    private readonly string? _targetProjectPath;
    private readonly string? _contextTypeName;
    private readonly ILogger _logger;
    private readonly CancellationToken _cancellationToken;
    private readonly IServiceProvider _serviceProvider;
    private readonly DotnetToolResource _toolResource;

    private string? _startupProjectPath;
    private string? _resolvedTargetProjectPath;
    private string? _configuration;
    private bool _initialized;
    internal const string ToolStartCommandName = "ef-tool-start";
    internal string? ResolvedFramework { get; private set;}

    // EF Core CLI output prefixes (used with --prefix-output). All are exactly 9 characters.
    private const int PrefixLength = 9;
    private const string ErrorPrefix = "error:   ";
    private const string WarningPrefix = "warn:    ";
    private const string InfoPrefix = "info:    ";
    private const string DataPrefix = "data:    ";
    private const string VerbosePrefix = "verbose: ";

    public EFCoreOperationExecutor(
        ProjectResource startupProjectResource,
        string? targetProjectPath,
        string? contextTypeName,
        ILogger logger,
        CancellationToken cancellationToken,
        IServiceProvider serviceProvider,
        DotnetToolResource toolResource)
    {
        _startupProjectResource = startupProjectResource;
        _targetProjectPath = targetProjectPath;
        _contextTypeName = contextTypeName;
        _logger = logger;
        _cancellationToken = cancellationToken;
        _serviceProvider = serviceProvider;
        _toolResource = toolResource;
    }

    private EFOperationResult EnsurePathsInitialized()
    {
        if (_initialized)
        {
            return _startupProjectPath != null
                ? new EFOperationResult { Success = true }
                : new EFOperationResult { Success = false, ErrorMessage = "Could not determine project path." };
        }

        _initialized = true;

        // Get build settings from the entry assembly (AppHost)
        var entryAssembly = Assembly.GetEntryAssembly();
        if (entryAssembly?.Location is { } appHostAssemblyPath && !string.IsNullOrEmpty(appHostAssemblyPath))
        {
            // Get configuration from assembly attribute
            var configAttribute = entryAssembly.GetCustomAttribute<AssemblyConfigurationAttribute>();
            if (configAttribute?.Configuration is { } configuration)
            {
                _configuration = configuration;
            }

            // Get target framework from assembly attribute
            var frameworkAttribute = entryAssembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>();
            if (frameworkAttribute?.FrameworkName is { } frameworkName)
            {
                // FrameworkName is in format ".NETCoreApp,Version=v8.0" - extract the version part
                const string versionPrefix = ".NETCoreApp,Version=v";
                var versionIndex = frameworkName.IndexOf(versionPrefix, StringComparison.OrdinalIgnoreCase);
                if (versionIndex >= 0)
                {
                    var version = frameworkName.Substring(versionIndex + versionPrefix.Length);
                    ResolvedFramework = $"net{version}";
                }
            }

            // Parse configuration, framework from the assembly path (if not found from attributes)
            ParseBuildSettingsFromPath(appHostAssemblyPath);
        }

        _startupProjectPath = GetProjectPath(_startupProjectResource);
        if (string.IsNullOrEmpty(_startupProjectPath))
        {
            return new EFOperationResult { Success = false, ErrorMessage = "Could not determine startup project path." };
        }

        _resolvedTargetProjectPath = _targetProjectPath ?? _startupProjectPath;

        if (string.IsNullOrEmpty(_resolvedTargetProjectPath))
        {
            return new EFOperationResult { Success = false, ErrorMessage = "Could not determine target project path." };
        }

        _logger.LogDebug("Using startup project: {StartupProject}", _startupProjectPath);
        _logger.LogDebug("Using target project: {TargetProject}", _resolvedTargetProjectPath);

        var workingDir = Path.GetDirectoryName(_startupProjectPath);
        if (!string.IsNullOrEmpty(workingDir))
        {
            var execAnnotation = _toolResource.Annotations.OfType<ExecutableAnnotation>().FirstOrDefault();
            if (execAnnotation != null && execAnnotation.WorkingDirectory != workingDir)
            {
                _toolResource.Annotations.Remove(execAnnotation);
                _toolResource.Annotations.Add(new ExecutableAnnotation
                {
                    Command = execAnnotation.Command,
                    WorkingDirectory = workingDir
                });
            }
        }

        return new EFOperationResult { Success = true };
    }

    private void ParseBuildSettingsFromPath(string assemblyPath)
    {
        var segments = assemblyPath.Split(Path.DirectorySeparatorChar, '/');
        var binIndex = Array.FindLastIndex(segments, s => s.Equals("bin", StringComparison.OrdinalIgnoreCase));

        if (binIndex < 0 || binIndex + 2 >= segments.Length)
        {
            return;
        }

        // Typical layout: bin/Debug/net10.0/ or bin/Debug/net10.0/win-x64/
        var configIndex = binIndex + 1;
        var frameworkIndex = binIndex + 2;

        if (_configuration == null && configIndex < segments.Length)
        {
            var configCandidate = segments[configIndex];
            if (configCandidate.Equals("Debug", StringComparison.OrdinalIgnoreCase) ||
                configCandidate.Equals("Release", StringComparison.OrdinalIgnoreCase))
            {
                _configuration = configCandidate;
            }
        }

        if (ResolvedFramework == null && frameworkIndex < segments.Length)
        {
            var frameworkCandidate = segments[frameworkIndex];
            if (frameworkCandidate.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            {
                ResolvedFramework = frameworkCandidate;
            }
        }
    }

    private static string? GetProjectPath(ProjectResource projectResource)
    {
        if (projectResource.TryGetLastAnnotation<IProjectMetadata>(out var metadata))
        {
            return metadata.ProjectPath;
        }
        return null;
    }

    private async Task<EFOperationResult> ExecuteEfCommandAsync(string command, string subCommand, Dictionary<string, string?>? additionalArgs = null)
    {
        var initResult = EnsurePathsInitialized();
        if (!initResult.Success)
        {
            return initResult;
        }

        // Build the EF command arguments (these go after the -- in dotnet tool exec)
        var efArgs = new List<string> { command, subCommand, "--no-build", "--no-color", "--prefix-output" };

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            efArgs.Add("--verbose");
        }

        // Add project paths
        efArgs.Add("--project");
        efArgs.Add(_resolvedTargetProjectPath!);

        if (_startupProjectPath != _resolvedTargetProjectPath)
        {
            efArgs.Add("--startup-project");
            efArgs.Add(_startupProjectPath!);
        }

        // Add configuration if available
        if (!string.IsNullOrEmpty(_configuration))
        {
            efArgs.Add("--configuration");
            efArgs.Add(_configuration);
        }

        // Add framework if available
        if (!string.IsNullOrEmpty(ResolvedFramework))
        {
            efArgs.Add("--framework");
            efArgs.Add(ResolvedFramework);
        }

        // Add context if specified
        if (!string.IsNullOrEmpty(_contextTypeName))
        {
            efArgs.Add("--context");
            efArgs.Add(_contextTypeName);
        }

        // Add additional arguments
        if (additionalArgs != null)
        {
            foreach (var (key, value) in additionalArgs)
            {
                efArgs.Add(key);
                if (!string.IsNullOrEmpty(value))
                {
                    efArgs.Add(value);
                }
            }
        }

        _logger.LogDebug("Executing dotnet tool exec dotnet-ef --yes -- {Args}", string.Join(" ", efArgs));

        try
        {
            // Get required services
            var resourceCommandService = _serviceProvider.GetRequiredService<ResourceCommandService>();
            var notificationService = _serviceProvider.GetRequiredService<ResourceNotificationService>();
            var loggerService = _serviceProvider.GetRequiredService<ResourceLoggerService>();

            var argsAnnotation = new CommandLineArgsCallbackAnnotation(args =>
            {
                foreach (var arg in efArgs)
                {
                    args.Add(arg);
                }
            });
            _toolResource.Annotations.Add(argsAnnotation);

            // Capture output before starting
            var dataBuilder = new StringBuilder();
            using var logCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);

            try
            {
                // Start a background task to capture logs
                var logTask = CaptureLogsAsync(loggerService.WatchAsync(_toolResource), _logger, dataBuilder, logCancellation.Token);

                // Start the hidden tool resource using the explicitly configured command when present,
                // otherwise choose the available start-like command from the resource annotations.
                var startCommandName = GetToolStartCommandName(_toolResource);
                var startResult = await resourceCommandService.ExecuteCommandAsync(_toolResource, startCommandName, _cancellationToken).ConfigureAwait(false);
                if (!startResult.Success)
                {
                    return new EFOperationResult
                    {
                        Success = false,
                        ErrorMessage = startResult.Canceled
                            ? "dotnet-ef command was canceled."
                            : string.IsNullOrWhiteSpace(startResult.Message) ? "dotnet-ef command failed" : startResult.Message
                    };
                }

                // Wait for the resource to finish
                await notificationService.WaitForResourceAsync(
                    _toolResource.Name,
                    r => r.Snapshot.State?.Text == KnownResourceStates.Finished ||
                         r.Snapshot.State?.Text == KnownResourceStates.Exited ||
                         r.Snapshot.State?.Text == KnownResourceStates.FailedToStart,
                    _cancellationToken).ConfigureAwait(false);

                // Give a moment for logs to flush, then cancel log capture
                await Task.Delay(100, _cancellationToken).ConfigureAwait(false);
                // Cancel log capture and wait for it to drain
                await logCancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    await logTask.WaitAsync(TimeSpan.FromSeconds(5), _cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when we cancel the log capture
                }
                catch (TimeoutException)
                {
                    // Log drain took too long, proceed with what we have
                }

                var data = dataBuilder.ToString();

                // Get final state
                var resourceEvent = await notificationService.WaitForResourceAsync(
                    _toolResource.Name,
                    _ => true, // Just get current state
                    _cancellationToken).ConfigureAwait(false);

                // Check if the command succeeded
                var snapshot = resourceEvent.Snapshot;
                var exitCode = snapshot.Properties.FirstOrDefault(p => p.Name == "ExitCode")?.Value?.ToString();
                if ((exitCode != null && exitCode != "0") || snapshot.State?.Text == KnownResourceStates.FailedToStart)
                {
                    return new EFOperationResult
                    {
                        Success = false,
                        ErrorMessage = "dotnet-ef command failed"
                    };
                }

                return new EFOperationResult { Success = true, Output = !string.IsNullOrEmpty(data) ? data : "[]" };
            }
            finally
            {
                // Clean up command-specific annotations
                _toolResource.Annotations.Remove(argsAnnotation);
            }
        }
        catch (Exception ex)
        {
            return new EFOperationResult { Success = false, ErrorMessage = $"dotnet-ef command failed: {ex.Message}" };
        }
    }

    private static string GetToolStartCommandName(DotnetToolResource toolResource)
    {
        // In run mode the DCP lifecycle wiring adds a standard start command — prefer that.
        // In publish mode no lifecycle commands are added, so fall back to the EF-specific one.
        if (toolResource.Annotations.OfType<ResourceCommandAnnotation>().Any(a => a.Name == KnownResourceCommands.StartCommand))
        {
            return KnownResourceCommands.StartCommand;
        }

        return ToolStartCommandName;
    }

    internal static async Task CaptureLogsAsync(
        IAsyncEnumerable<IReadOnlyList<LogLine>> logChannel,
        ILogger logger,
        StringBuilder dataBuilder,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var entries in logChannel.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                foreach (var entry in entries)
                {
                    var (logLevel, normalizedContent, isData) = ParsePrefixedOutput(entry.Content, entry.IsErrorMessage);

                    if (isData)
                    {
                        dataBuilder.AppendLine(normalizedContent);
                    }
                    else
                    {
                        logger.Log(logLevel, "{Output}", normalizedContent);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
    }

    internal static async Task StreamOutputAsync(
        StreamReader reader,
        ILogger logger,
        bool isErrorOutput,
        StringBuilder? captureBuilder,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            var (logLevel, normalizedContent, isData) = ParsePrefixedOutput(line, isErrorOutput);
            if (!isData)
            {
                logger.Log(logLevel, "{Output}", normalizedContent);
                captureBuilder?.AppendLine(normalizedContent);
            }
        }
    }

    private static (LogLevel LogLevel, string Content, bool IsData) ParsePrefixedOutput(string content, bool isErrorMessage)
    {
        content = StripDateTimePrefix(content);
        if (content.Length >= PrefixLength)
        {
            var prefix = content.AsSpan(0, PrefixLength);
            var remainder = content[PrefixLength..];

            if (prefix.SequenceEqual(ErrorPrefix))
            {
                return (LogLevel.Error, remainder, false);
            }
            if (prefix.SequenceEqual(WarningPrefix))
            {
                return (LogLevel.Warning, remainder, false);
            }
            if (prefix.SequenceEqual(InfoPrefix))
            {
                return (LogLevel.Information, remainder, false);
            }
            if (prefix.SequenceEqual(DataPrefix))
            {
                return (LogLevel.Debug, remainder, true);
            }
            if (prefix.SequenceEqual(VerbosePrefix))
            {
                return (LogLevel.Trace, remainder, false);
            }
        }

        // Lines without a recognized prefix (e.g., raw subprocess output from MSBuild
        // evaluation) are treated as trace-level so they don't clutter normal output.
        // Error stream lines without a prefix are still treated as errors.
        return (isErrorMessage ? LogLevel.Error : LogLevel.Trace, content, false);
    }

    /// <summary>
    /// Strips the datetime prefix from log lines.
    /// Uses format from <see cref="KnownFormats.ConsoleLogsTimestampFormat"/>: "yyyy-MM-ddTHH:mm:ss.fffffffK"
    /// Examples:
    /// - UTC: 2026-02-06T21:54:18.2290000Z (28 chars)
    /// - Non-UTC: 2026-02-06T21:54:18.2290000-07:00 (33 chars)
    /// </summary>
    private static string StripDateTimePrefix(string content)
    {
        // Format from KnownFormats.ConsoleLogsTimestampFormat: "yyyy-MM-ddTHH:mm:ss.fffffffK"
        // The K specifier produces:
        // - 'Z' for UTC (total 28 characters)
        // - '+HH:mm' or '-HH:mm' for non-UTC (total 33 characters)
        
        // First verify common separators for ISO 8601 format
        if (content.Length < 29 ||
            content[4] != '-' ||   // yyyy-
            content[7] != '-' ||   // MM-
            content[10] != 'T' ||  // ddT
            content[13] != ':' ||  // HH:
            content[16] != ':' ||  // mm:
            content[19] != '.')    // ss.
        {
            return content;
        }
        
        // Check for UTC format: ends with 'Z' at position 27
        if (content.Length > 28 && content[27] == 'Z' && content[28] == ' ')
        {
            return content[29..];
        }
        
        // Check for non-UTC format: ends with offset like '-07:00' or '+05:30'
        // Position 26 is '+' or '-', position 29 is ':', position 32 is last digit, position 33 is space
        if (content.Length > 33 && 
            (content[26] == '+' || content[26] == '-') && 
            content[29] == ':' && 
            content[33] == ' ')
        {
            return content[34..];
        }
        
        return content;
    }

    public async Task<EFOperationResult> UpdateDatabaseAsync()
    {
        _logger.LogInformation("Updating database...");
        return await ExecuteEfCommandAsync("database", "update").ConfigureAwait(false);
    }

    public async Task<EFOperationResult> DropDatabaseAsync()
    {
        _logger.LogInformation("Dropping database...");
        return await ExecuteEfCommandAsync("database", "drop", new Dictionary<string, string?>
        {
            { "--force", null }
        }).ConfigureAwait(false);
    }

    public async Task<EFOperationResult> ResetDatabaseAsync()
    {
        // First drop the database
        var dropResult = await DropDatabaseAsync().ConfigureAwait(false);
        if (!dropResult.Success)
        {
            // If drop fails because database doesn't exist, continue with update
            if (!dropResult.ErrorMessage?.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ?? false)
            {
                return dropResult;
            }
        }

        // Then update (recreate with migrations)
        return await UpdateDatabaseAsync().ConfigureAwait(false);
    }

    public async Task<EFOperationResult> AddMigrationAsync(string? migrationName = null, string? outputDir = null, string? @namespace = null)
    {
        migrationName ??= $"Migration_{DateTime.UtcNow:yyyyMMddHHmmss}";
        _logger.LogInformation("Creating migration with name: {MigrationName}", migrationName);

        var args = new Dictionary<string, string?>
        {
            { migrationName, null } // Migration name is positional
        };

        if (!string.IsNullOrEmpty(outputDir))
        {
            args["--output-dir"] = outputDir;
        }

        if (!string.IsNullOrEmpty(@namespace))
        {
            args["--namespace"] = @namespace;
        }

        return await ExecuteEfCommandAsync("migrations", "add", args).ConfigureAwait(false);
    }

    public async Task<EFOperationResult> RemoveMigrationAsync()
    {
        _logger.LogInformation("Removing last migration...");
        return await ExecuteEfCommandAsync("migrations", "remove", new Dictionary<string, string?>
        {
            { "--force", null }
        }).ConfigureAwait(false);
    }

    public async Task<EFOperationResult> GetDatabaseStatusAsync()
    {
        _logger.LogInformation("Getting migration status...");

        // Use migrations list --json to get structured output
        var result = await ExecuteEfCommandAsync("migrations", "list", new Dictionary<string, string?>
        {
            { "--json", null }
        }).ConfigureAwait(false);

        if (!result.Success)
        {
            // Handle case where database doesn't exist
            if (result.ErrorMessage?.Contains("does not exist", StringComparison.OrdinalIgnoreCase) == true ||
                result.ErrorMessage?.Contains("Cannot open database", StringComparison.OrdinalIgnoreCase) == true)
            {
                return new EFOperationResult
                {
                    Success = true,
                    Output = "**Database Status:** Database has not been created yet."
                };
            }
            return result;
        }

        // Parse JSON output from dotnet ef migrations list --json
        var migrationLines = new List<string>();
        var pendingCount = 0;
        var appliedCount = 0;

        // The JSON output is an array of migration objects with "id", "name", and "applied" properties
        using var doc = JsonDocument.Parse(result.Output ?? "[]");
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var migration in doc.RootElement.EnumerateArray())
            {
                var id = migration.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                var name = migration.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                var applied = migration.TryGetProperty("applied", out var appliedProp) && appliedProp.GetBoolean();

                var displayName = id ?? name ?? "Unknown";

                if (applied)
                {
                    migrationLines.Add($"- ✅ {displayName}");
                    appliedCount++;
                }
                else
                {
                    migrationLines.Add($"- ⚠️ {displayName} (Pending)");
                    pendingCount++;
                }
            }
        }

        var summary = new StringBuilder();
        summary.AppendLine(CultureInfo.InvariantCulture, $"**Applied Migrations:** {appliedCount}");
        summary.AppendLine(CultureInfo.InvariantCulture, $"**Pending Migrations:** {pendingCount}");
        summary.AppendLine();

        if (migrationLines.Count > 0)
        {
            summary.AppendLine("**Migration History:**");
            summary.AppendLine();
            foreach (var line in migrationLines)
            {
                summary.AppendLine(line);
            }
        }
        else
        {
            summary.AppendLine("No migrations found.");
        }

        // Check for pending model changes
        var pendingChangesResult = await ExecuteEfCommandAsync("migrations", "has-pending-model-changes").ConfigureAwait(false);
        summary.AppendLine();

        if (!pendingChangesResult.Success)
        {
            summary.AppendLine("ℹ️ Unable to check for pending model changes.");
        }
        else if (pendingChangesResult.Output?.Contains("Changes have been made", StringComparison.OrdinalIgnoreCase) == true ||
                 pendingChangesResult.Output?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true)
        {
            summary.AppendLine("⚠️ Pending model changes detected. A new migration is needed.");
        }
        else
        {
            summary.AppendLine("✅ No pending model changes.");
        }

        return new EFOperationResult { Success = true, Output = summary.ToString() };
    }

    /// <summary>
    /// Generates a SQL migration script.
    /// </summary>
    /// <param name="outputPath">The output path for the script file.</param>
    /// <param name="idempotent">If true, generates an idempotent script with IF NOT EXISTS checks.</param>
    /// <param name="noTransactions">If true, omits transaction statements from the script.</param>
    /// <returns>The operation result.</returns>
    public async Task<EFOperationResult> GenerateMigrationScriptAsync(string? outputPath = null, bool idempotent = true, bool noTransactions = false)
    {
        _logger.LogInformation("Generating migration script...");

        var args = new Dictionary<string, string?>();

        if (idempotent)
        {
            args["--idempotent"] = null;
        }

        if (noTransactions)
        {
            args["--no-transactions"] = null;
        }

        if (!string.IsNullOrEmpty(outputPath))
        {
            args["--output"] = outputPath;
        }

        return await ExecuteEfCommandAsync("migrations", "script", args).ConfigureAwait(false);
    }

    /// <summary>
    /// Generates a migration bundle executable.
    /// </summary>
    /// <param name="outputPath">The output path for the bundle file.</param>
    /// <param name="targetRuntime">The target runtime identifier (e.g., "linux-x64", "win-x64").</param>
    /// <param name="selfContained">If true, creates a self-contained bundle that includes the .NET runtime.</param>
    /// <returns>The operation result.</returns>
    public async Task<EFOperationResult> GenerateMigrationBundleAsync(string? outputPath = null, string? targetRuntime = null, bool selfContained = false)
    {
        _logger.LogInformation("Generating migration bundle...");

        var args = new Dictionary<string, string?>();

        if (!string.IsNullOrEmpty(outputPath))
        {
            args["--output"] = outputPath;
        }

        if (!string.IsNullOrEmpty(targetRuntime))
        {
            args["--runtime"] = targetRuntime;
        }

        if (selfContained)
        {
            args["--self-contained"] = null;
        }

        // Overwrite existing bundle
        args["--force"] = null;

        return await ExecuteEfCommandAsync("migrations", "bundle", args).ConfigureAwait(false);
    }

    public void Dispose()
    {
        // Nothing to dispose - processes are cleaned up after each operation
    }
}

/// <summary>
/// Result of an EF Core operation.
/// </summary>
internal sealed record EFOperationResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Output { get; init; }
}
