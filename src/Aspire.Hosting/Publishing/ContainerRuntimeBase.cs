// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Base class for container runtime implementations that provides common process execution,
/// logging, and error handling patterns.
/// </summary>
internal abstract class ContainerRuntimeBase<TLogger> : IContainerRuntime where TLogger : class
{
    private readonly ILogger<TLogger> _logger;

    protected ContainerRuntimeBase(ILogger<TLogger> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the logger instance for use in derived classes.
    /// </summary>
    protected ILogger<TLogger> Logger => _logger;

    /// <summary>
    /// Gets the name of the container runtime executable (e.g., "docker", "podman").
    /// </summary>
    protected abstract string RuntimeExecutable { get; }

    public abstract string Name { get; }

    public abstract Task<bool> CheckIfRunningAsync(CancellationToken cancellationToken);

    public abstract Task BuildImageAsync(string contextPath, string dockerfilePath, ContainerImageBuildOptions? options, Dictionary<string, string?> buildArguments, Dictionary<string, BuildImageSecretValue> buildSecrets, string? stage, CancellationToken cancellationToken);

    public virtual async Task TagImageAsync(string localImageName, string targetImageName, CancellationToken cancellationToken)
    {
        var arguments = $"tag \"{localImageName}\" \"{targetImageName}\"";

        await ExecuteContainerCommandAsync(
            arguments,
            $"{Name} tag for {{LocalImageName}} -> {{TargetImageName}} failed with exit code {{ExitCode}}.",
            $"{Name} tag for {{LocalImageName}} -> {{TargetImageName}} succeeded.",
            $"{Name} tag failed with exit code {{0}}.",
            cancellationToken,
            localImageName, targetImageName).ConfigureAwait(false);
    }

    public virtual async Task RemoveImageAsync(string imageName, CancellationToken cancellationToken)
    {
        var arguments = $"rmi \"{imageName}\"";

        await ExecuteContainerCommandAsync(
            arguments,
            $"{Name} rmi for {{ImageName}} failed with exit code {{ExitCode}}.",
            $"{Name} rmi for {{ImageName}} succeeded.",
            $"{Name} rmi failed with exit code {{0}}.",
            cancellationToken,
            imageName).ConfigureAwait(false);
    }

    public virtual async Task PushImageAsync(IResource resource, CancellationToken cancellationToken)
    {
        var localImageName = resource.TryGetContainerImageName(out var imageName)
            ? imageName
            : resource.Name.ToLowerInvariant();

        var remoteImageName = await resource.GetFullRemoteImageNameAsync(cancellationToken).ConfigureAwait(false);

        await TagImageAsync(localImageName, remoteImageName, cancellationToken).ConfigureAwait(false);

        var arguments = $"push \"{remoteImageName}\"";

        await ExecuteContainerCommandAsync(
            arguments,
            $"{Name} push for {{ImageName}} failed with exit code {{ExitCode}}.",
            $"{Name} push for {{ImageName}} succeeded.",
            $"{Name} push failed with exit code {{0}}.",
            cancellationToken,
            remoteImageName).ConfigureAwait(false);
    }

    public virtual async Task LoginToRegistryAsync(string registryServer, string username, string password, CancellationToken cancellationToken)
    {
        // Escape quotes in arguments to prevent command injection
        var escapedRegistryServer = registryServer.Replace("\"", "\\\"");
        var escapedUsername = username.Replace("\"", "\\\"");
        var arguments = $"login --username \"{escapedUsername}\" --password-stdin \"{escapedRegistryServer}\"";

        var spec = new ProcessSpec(RuntimeExecutable)
        {
            Arguments = arguments,
            StandardInputContent = password,
            OnOutputData = output =>
            {
                _logger.LogDebug("{RuntimeName} (stdout): {Output}", RuntimeExecutable, output);
            },
            OnErrorData = error =>
            {
                _logger.LogDebug("{RuntimeName} (stderr): {Error}", RuntimeExecutable, error);
            },
            ThrowOnNonZeroReturnCode = false,
            InheritEnv = true
        };

        _logger.LogDebug("Running {RuntimeName} with arguments: {Arguments}", RuntimeExecutable, arguments);
        _logger.LogDebug("Password length being passed to stdin: {PasswordLength}", password?.Length ?? 0);
        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

        await using (processDisposable)
        {
            var processResult = await pendingProcessResult
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (processResult.ExitCode != 0)
            {
                _logger.LogError("{RuntimeName} login to {RegistryServer} failed with exit code {ExitCode}.", Name, registryServer, processResult.ExitCode);
                throw new DistributedApplicationException($"{Name} login failed with exit code {processResult.ExitCode}.");
            }

            _logger.LogInformation("{RuntimeName} login to {RegistryServer} succeeded.", Name, registryServer);
        }
    }

    /// <summary>
    /// Executes a container runtime command with standard logging and error handling.
    /// </summary>
    /// <param name="arguments">The command arguments to pass to the container runtime.</param>
    /// <param name="errorLogTemplate">Log template for error messages (must include {ExitCode} placeholder).</param>
    /// <param name="successLogTemplate">Log template for success messages.</param>
    /// <param name="exceptionMessageTemplate">Exception message template (must include {ExitCode} placeholder).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="logArguments">Arguments to pass to the log templates.</param>
    protected async Task ExecuteContainerCommandAsync(
        string arguments,
        string errorLogTemplate,
        string successLogTemplate,
        string exceptionMessageTemplate,
        CancellationToken cancellationToken,
        params object[] logArguments)
    {
        var outputBuffer = new BuildOutputCapture();
        var spec = CreateProcessSpec(arguments, outputBuffer);

        _logger.LogDebug("Running {RuntimeName} with arguments: {ArgumentList}", Name, spec.Arguments);
        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

        await using (processDisposable)
        {
            var processResult = await pendingProcessResult
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (processResult.ExitCode != 0)
            {
                var errorArgs = logArguments.Concat(new object[] { processResult.ExitCode }).ToArray();
                _logger.LogError(errorLogTemplate, errorArgs);

                var message = string.Format(System.Globalization.CultureInfo.InvariantCulture, exceptionMessageTemplate, processResult.ExitCode);
                if (outputBuffer.TotalLineCount > 0)
                {
                    message = $"{message}{Environment.NewLine}{outputBuffer.GetFormattedOutput(outputDescription: "Command output")}";
                }

                throw new DistributedApplicationException(message);
            }

            _logger.LogInformation(successLogTemplate, logArguments);
        }
    }

    /// <summary>
    /// Executes a container runtime command and returns the exit code without throwing exceptions.
    /// </summary>
    /// <param name="arguments">The command arguments to pass to the container runtime.</param>
    /// <param name="errorLogTemplate">Log template for error messages (must include {ExitCode} placeholder).</param>
    /// <param name="successLogTemplate">Log template for success messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="logArguments">Arguments to pass to the log templates.</param>
    /// <param name="environmentVariables">Optional environment variables to set for the process.</param>
    /// <param name="outputBuffer">Optional buffer to retain stdout/stderr lines.</param>
    /// <returns>The exit code of the process.</returns>
    protected async Task<int> ExecuteContainerCommandWithExitCodeAsync(
        string arguments,
        string errorLogTemplate,
        string successLogTemplate,
        CancellationToken cancellationToken,
        object[] logArguments,
        Dictionary<string, string>? environmentVariables = null,
        BuildOutputCapture? outputBuffer = null)
    {
        var spec = CreateProcessSpec(arguments, outputBuffer);

        // Add environment variables if provided
        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                spec.EnvironmentVariables[key] = value;
            }
        }

        _logger.LogDebug("Running {RuntimeName} with arguments: {ArgumentList}", Name, spec.Arguments);
        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

        await using (processDisposable)
        {
            var processResult = await pendingProcessResult
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (processResult.ExitCode != 0)
            {
                var errorArgs = logArguments.Concat(new object[] { processResult.ExitCode }).ToArray();
                _logger.LogError(errorLogTemplate, errorArgs);
                return processResult.ExitCode;
            }

            _logger.LogDebug(successLogTemplate, logArguments);
            return processResult.ExitCode;
        }
    }

    /// <summary>
    /// Builds a string of build arguments for container build commands.
    /// </summary>
    /// <param name="buildArguments">The build arguments to include.</param>
    /// <returns>A string containing the formatted build arguments.</returns>
    protected static string BuildArgumentsString(Dictionary<string, string?> buildArguments)
    {
        var result = string.Empty;
        foreach (var buildArg in buildArguments)
        {
            result += buildArg.Value is not null
                ? $" --build-arg \"{buildArg.Key}={buildArg.Value}\""
                : $" --build-arg \"{buildArg.Key}\"";
        }
        return result;
    }

    /// <summary>
    /// Builds a string of build secrets for container build commands.
    /// </summary>
    /// <param name="buildSecrets">The build secrets to include.</param>
    /// <param name="requireValue">Whether to require a non-null value for secrets (default: false).</param>
    /// <returns>A string containing the formatted build secrets.</returns>
    internal static string BuildSecretsString(Dictionary<string, BuildImageSecretValue> buildSecrets, bool requireValue = false)
    {
        var result = string.Empty;
        foreach (var buildSecret in buildSecrets)
        {
            if (buildSecret.Value.Type == BuildImageSecretType.File)
            {
                result += $" --secret \"id={buildSecret.Key},type=file,src={buildSecret.Value.Value}\"";
            }
            else if (requireValue && buildSecret.Value.Value is null)
            {
                result += $" --secret \"id={buildSecret.Key},type=env\"";
            }
            else
            {
                result += $" --secret \"id={buildSecret.Key},type=env,env={buildSecret.Key.ToUpperInvariant()}\"";
            }
        }
        return result;
    }

    /// <summary>
    /// Builds a string for the target stage in container build commands.
    /// </summary>
    /// <param name="stage">The target stage to include.</param>
    /// <returns>A string containing the formatted target stage, or empty string if stage is null or empty.</returns>
    protected static string BuildStageString(string? stage)
    {
        return !string.IsNullOrEmpty(stage) ? $" --target \"{stage}\"" : string.Empty;
    }

    private ProcessSpec CreateProcessSpec(string arguments, BuildOutputCapture? outputBuffer)
    {
        return new ProcessSpec(RuntimeExecutable)
        {
            Arguments = arguments,
            OnOutputData = output =>
            {
                _logger.LogDebug("{RuntimeName} (stdout): {Output}", RuntimeExecutable, output);
                outputBuffer?.Add(output);
            },
            OnErrorData = error =>
            {
                _logger.LogDebug("{RuntimeName} (stderr): {Error}", RuntimeExecutable, error);
                outputBuffer?.Add(error);
            },
            ThrowOnNonZeroReturnCode = false,
            InheritEnv = true
        };
    }

    public virtual async Task ComposeUpAsync(ComposeOperationContext context, CancellationToken cancellationToken)
    {
        await EnsureRuntimeAvailableAsync().ConfigureAwait(false);

        var arguments = BuildComposeArguments(context);
        arguments += " up -d --remove-orphans";

        _logger.LogInformation("Using container runtime '{Runtime}' for compose operations.", RuntimeExecutable);
        _logger.LogDebug("Running {Runtime} compose up with arguments: {Arguments}", RuntimeExecutable, arguments);

        var spec = new ProcessSpec(RuntimeExecutable)
        {
            Arguments = arguments,
            WorkingDirectory = context.WorkingDirectory,
            ThrowOnNonZeroReturnCode = false,
            InheritEnv = true,
            OnOutputData = output =>
            {
                _logger.LogDebug("{Runtime} compose up (stdout): {Output}", RuntimeExecutable, output);
            },
            OnErrorData = error =>
            {
                _logger.LogDebug("{Runtime} compose up (stderr): {Error}", RuntimeExecutable, error);
            },
        };

        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

        await using (processDisposable)
        {
            var processResult = await pendingProcessResult
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (processResult.ExitCode != 0)
            {
                var envHint = Environment.GetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME") is not null
                    ? $"The container runtime is configured via ASPIRE_CONTAINER_RUNTIME (current: '{RuntimeExecutable}')."
                    : $"The container runtime was auto-detected as '{RuntimeExecutable}'. Set ASPIRE_CONTAINER_RUNTIME to override (e.g., 'docker' or 'podman').";

                throw new DistributedApplicationException(
                    $"'{RuntimeExecutable} compose up' failed with exit code {processResult.ExitCode}. " +
                    $"Ensure '{RuntimeExecutable}' is installed and available on PATH. " +
                    envHint);
            }
        }
    }

    public virtual async Task ComposeDownAsync(ComposeOperationContext context, CancellationToken cancellationToken)
    {
        await EnsureRuntimeAvailableAsync().ConfigureAwait(false);

        var arguments = BuildComposeArguments(context);
        arguments += " down";

        _logger.LogDebug("Running {Runtime} compose down with arguments: {Arguments}", RuntimeExecutable, arguments);

        var stderrLines = new List<string>();
        var spec = new ProcessSpec(RuntimeExecutable)
        {
            Arguments = arguments,
            WorkingDirectory = context.WorkingDirectory,
            ThrowOnNonZeroReturnCode = false,
            InheritEnv = true,
            OnOutputData = output =>
            {
                _logger.LogDebug("{Runtime} compose down (stdout): {Output}", RuntimeExecutable, output);
            },
            OnErrorData = error =>
            {
                _logger.LogDebug("{Runtime} compose down (stderr): {Error}", RuntimeExecutable, error);
                if (!string.IsNullOrWhiteSpace(error))
                {
                    stderrLines.Add(error);
                }
            },
        };

        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

        await using (processDisposable)
        {
            var processResult = await pendingProcessResult
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (processResult.ExitCode != 0)
            {
                var stderrOutput = stderrLines.Count > 0
                    ? " " + string.Join(" ", stderrLines)
                    : "";

                throw new DistributedApplicationException(
                    $"'{RuntimeExecutable} compose down' failed with exit code {processResult.ExitCode}.{stderrOutput}");
            }
        }
    }

    public virtual async Task<IReadOnlyList<ComposeServiceInfo>?> ComposeListServicesAsync(ComposeOperationContext context, CancellationToken cancellationToken)
    {
        await EnsureRuntimeAvailableAsync().ConfigureAwait(false);

        var arguments = BuildComposeArguments(context);
        arguments += " ps --format json";

        var outputLines = new List<string>();

        var spec = new ProcessSpec(RuntimeExecutable)
        {
            Arguments = arguments,
            WorkingDirectory = context.WorkingDirectory,
            ThrowOnNonZeroReturnCode = false,
            InheritEnv = true,
            OnOutputData = output =>
            {
                if (!string.IsNullOrWhiteSpace(output))
                {
                    outputLines.Add(output);
                }
            },
            OnErrorData = error =>
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    _logger.LogDebug("{Runtime} compose ps (stderr): {Error}", RuntimeExecutable, error);
                }
            }
        };

        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

        await using (processDisposable)
        {
            var processResult = await pendingProcessResult
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (processResult.ExitCode != 0)
            {
                _logger.LogDebug("{Runtime} compose ps failed with exit code {ExitCode}", RuntimeExecutable, processResult.ExitCode);
                return null;
            }
        }

        return ParseComposeServiceEntries(outputLines);
    }

    /// <summary>
    /// Parses Docker Compose ps JSON output, handling both NDJSON (one object per line) and JSON array formats.
    /// </summary>
    /// <example>
    /// NDJSON (Docker Compose v2+):
    /// <code>
    /// {"Service":"web","Publishers":[{"URL":"","TargetPort":80,"PublishedPort":8080,"Protocol":"tcp"}]}
    /// {"Service":"cache","Publishers":[{"TargetPort":6379,"PublishedPort":6379}]}
    /// </code>
    /// JSON array (older versions):
    /// <code>
    /// [{"Service":"web","Publishers":[{"TargetPort":80,"PublishedPort":8080}]}]
    /// </code>
    /// </example>
    internal static List<ComposeServiceInfo> ParseComposeServiceEntries(List<string> outputLines)
    {
        var results = new List<ComposeServiceInfo>();

        foreach (var line in outputLines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // Try parsing as JSON array first (older Docker Compose versions)
            if (trimmed.StartsWith('['))
            {
                try
                {
                    var entries = JsonSerializer.Deserialize(trimmed, ComposeJsonContext.Default.ListDockerComposePsEntry);
                    if (entries is not null)
                    {
                        foreach (var entry in entries)
                        {
                            results.Add(MapDockerComposeEntry(entry));
                        }
                    }
                }
                catch (JsonException)
                {
                    // Skip unparseable lines
                }
                continue;
            }

            // Parse as single JSON object (NDJSON format)
            if (trimmed.StartsWith('{'))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize(trimmed, ComposeJsonContext.Default.DockerComposePsEntry);
                    if (entry is not null)
                    {
                        results.Add(MapDockerComposeEntry(entry));
                    }
                }
                catch (JsonException)
                {
                    // Skip unparseable lines
                }
            }
        }

        return results;
    }

    private static ComposeServiceInfo MapDockerComposeEntry(DockerComposePsEntry entry)
    {
        return new ComposeServiceInfo
        {
            Service = entry.Service,
            Publishers = entry.Publishers?.Select(p => new ComposeServicePort
            {
                PublishedPort = p.PublishedPort,
                TargetPort = p.TargetPort
            }).ToList()
        };
    }

    /// <summary>
    /// Builds the compose CLI arguments from a <see cref="ComposeOperationContext"/>.
    /// </summary>
    private static string BuildComposeArguments(ComposeOperationContext context)
    {
        var arguments = context.ComposeFilePath is not null
            ? $"compose -f \"{context.ComposeFilePath}\" --project-name \"{context.ProjectName}\""
            : $"compose --project-name \"{context.ProjectName}\"";

        if (context.EnvFilePath is not null && File.Exists(context.EnvFilePath))
        {
            arguments += $" --env-file \"{context.EnvFilePath}\"";
        }

        return arguments;
    }

    /// <summary>
    /// Validates that the container runtime binary is available on the system PATH.
    /// Fails fast with an actionable error message instead of a cryptic exit code.
    /// </summary>
    protected async Task EnsureRuntimeAvailableAsync()
    {
        try
        {
            var whichCommand = OperatingSystem.IsWindows() ? "where" : "which";
            var spec = new ProcessSpec(whichCommand)
            {
                Arguments = RuntimeExecutable,
                ThrowOnNonZeroReturnCode = false,
                InheritEnv = true
            };

            var (pendingResult, processDisposable) = ProcessUtil.Run(spec);
            await using (processDisposable)
            {
                var result = await pendingResult.ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    throw new DistributedApplicationException(
                        $"Container runtime '{RuntimeExecutable}' was not found on PATH. " +
                        $"Install {Name} or set ASPIRE_CONTAINER_RUNTIME to a different runtime (e.g., 'docker' or 'podman').");
                }
            }
        }
        catch (DistributedApplicationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check if {Runtime} is available on PATH", RuntimeExecutable);
        }
    }
}

/// <summary>
/// Internal DTO for deserializing Docker Compose ps JSON output.
/// </summary>
internal sealed class DockerComposePsEntry
{
    public string? Service { get; set; }
    public List<DockerComposePsPublisher>? Publishers { get; set; }
}

/// <summary>
/// Internal DTO for deserializing Docker Compose ps publisher entries.
/// </summary>
internal sealed class DockerComposePsPublisher
{
    public int? PublishedPort { get; set; }
    public int? TargetPort { get; set; }
}

[JsonSerializable(typeof(DockerComposePsEntry))]
[JsonSerializable(typeof(List<DockerComposePsEntry>))]
internal sealed partial class ComposeJsonContext : JsonSerializerContext
{
}
