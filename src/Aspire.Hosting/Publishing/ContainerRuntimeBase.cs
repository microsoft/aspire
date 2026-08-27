// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly IProcessRunner _processRunner;

    protected ContainerRuntimeBase(ILogger<TLogger> logger, IProcessRunner processRunner)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    /// <summary>
    /// Gets the logger instance for use in derived classes.
    /// </summary>
    protected ILogger<TLogger> Logger => _logger;

    /// <summary>
    /// Gets the process runner used for container runtime commands.
    /// </summary>
    protected IProcessRunner ProcessRunner => _processRunner;

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

    public virtual async Task<ContainerImageConfigInspectionResult> InspectImageConfigAsync(string imageName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);

        string output;
        try
        {
            output = await ExecuteContainerCommandForOutputAsync(
                [
                    "image",
                    "inspect",
                    imageName,
                    "--format",
                    """{"Entrypoint":{{json .Config.Entrypoint}},"Cmd":{{json .Config.Cmd}},"WorkingDir":{{json .Config.WorkingDir}}}"""
                ],
                "inspect image config",
                imageName,
                cancellationToken).ConfigureAwait(false);
        }
        catch (DistributedApplicationException ex)
        {
            return new ContainerImageConfigInspectionResult(
                ContainerImageInspectionStatus.Failed,
                rawJson: null,
                ex.Message,
                configAccessor: null);
        }

        if (!TryParseImageConfig(output, out var config))
        {
            return new ContainerImageConfigInspectionResult(
                ContainerImageInspectionStatus.Failed,
                output,
                $"Container runtime returned invalid image configuration for '{imageName}'.",
                configAccessor: null);
        }

        return new ContainerImageConfigInspectionResult(
            ContainerImageInspectionStatus.Succeeded,
            output,
            errorMessage: null,
            () => config);
    }

    public virtual async Task<ContainerImageManifestInspectionResult> InspectImageManifestAsync(string imageName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);

        string output;
        try
        {
            output = await ExecuteContainerCommandForOutputAsync(
                ["manifest", "inspect", "--verbose", imageName],
                "inspect image manifest",
                imageName,
                cancellationToken).ConfigureAwait(false);
        }
        catch (DistributedApplicationException ex)
        {
            return new ContainerImageManifestInspectionResult(
                ContainerImageInspectionStatus.Failed,
                rawJson: null,
                ex.Message,
                manifestAccessor: null);
        }

        if (!IsJsonObjectOrArray(output))
        {
            return new ContainerImageManifestInspectionResult(
                ContainerImageInspectionStatus.Failed,
                output,
                $"Container runtime returned invalid image manifest for '{imageName}'.",
                manifestAccessor: null);
        }

        return new ContainerImageManifestInspectionResult(
            ContainerImageInspectionStatus.Succeeded,
            output,
            errorMessage: null,
            (operatingSystem, architecture) => FindManifest(output, operatingSystem, architecture));
    }

    private static bool TryParseImageConfig(string output, [NotNullWhen(true)] out ContainerImageConfig? config)
    {
        config = null;

        try
        {
            var root = JsonNode.Parse(output) as JsonObject;
            if (root is null)
            {
                return false;
            }

            config = new ContainerImageConfig(
                ReadStringArray(root["Entrypoint"]),
                ReadStringArray(root["Cmd"]),
                root["WorkingDir"]?.GetValue<string>());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        var values = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item?.GetValue<string>() is { } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    protected static bool IsJsonObjectOrArray(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    protected static ContainerImageManifest? FindManifest(string output, string operatingSystem, string architecture)
    {
        try
        {
            var root = JsonNode.Parse(output);
            if (root is JsonArray verboseManifests)
            {
                foreach (var item in verboseManifests.OfType<JsonObject>())
                {
                    if (TryCreateManifest(item["Descriptor"] as JsonObject, operatingSystem, architecture, out var manifest))
                    {
                        return manifest;
                    }
                }

                return null;
            }

            if (root is not JsonObject manifestObject)
            {
                return null;
            }

            if (manifestObject["manifests"] is JsonArray manifests)
            {
                foreach (var item in manifests.OfType<JsonObject>())
                {
                    if (TryCreateManifest(item, operatingSystem, architecture, out var manifest))
                    {
                        return manifest;
                    }
                }

                return null;
            }

            var descriptor = manifestObject["Descriptor"] as JsonObject ??
                manifestObject["descriptor"] as JsonObject ??
                manifestObject;
            return TryCreateManifest(descriptor, operatingSystem, architecture, out var singleManifest)
                ? singleManifest
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryCreateManifest(
        JsonObject? descriptor,
        string operatingSystem,
        string architecture,
        [NotNullWhen(true)] out ContainerImageManifest? manifest)
    {
        manifest = null;
        if (descriptor is null)
        {
            return false;
        }

        var platform = descriptor["platform"] as JsonObject;
        var actualOperatingSystem = platform?["os"]?.GetValue<string>();
        var actualArchitecture = platform?["architecture"]?.GetValue<string>();
        var digest = descriptor["digest"]?.GetValue<string>();
        if (digest is null ||
            !ContainerImageManifest.IsValidDigest(digest) ||
            actualOperatingSystem is null ||
            actualArchitecture is null ||
            !string.Equals(actualOperatingSystem, operatingSystem, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actualArchitecture, architecture, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        manifest = new ContainerImageManifest(digest, actualOperatingSystem, actualArchitecture);
        return true;
    }

    public virtual async Task LoginToRegistryAsync(string registryServer, string username, string password, CancellationToken cancellationToken)
    {
        // Escape quotes in arguments to prevent command injection
        var escapedRegistryServer = EscapeArgument(registryServer);
        var escapedUsername = EscapeArgument(username);
        var arguments = $"login --username \"{escapedUsername}\" --password-stdin \"{escapedRegistryServer}\"";

        var spec = new ProcessSpec(RuntimeExecutable)
        {
            Arguments = arguments,
            StandardInputContent = password,
            RetainedOutputLineCount = ProcessSpec.DefaultRetainedOutputLineCount,
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
        var (pendingProcessResult, processDisposable) = _processRunner.Run(spec);

        await using (processDisposable)
        {
            var processResult = await pendingProcessResult
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (processResult.ExitCode != 0)
            {
                _logger.LogError("{RuntimeName} login to {RegistryServer} failed with exit code {ExitCode}.", Name, registryServer, processResult.ExitCode);

                var message = $"{Name} login failed with exit code {processResult.ExitCode}.";
                if (processResult.TotalProcessOutputLineCount > 0)
                {
                    message = $"{message}{Environment.NewLine}{processResult.GetFormattedOutput()}";
                }

                throw new DistributedApplicationException(message);
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
        var spec = CreateProcessSpec(arguments, retainOutput: true);

        _logger.LogDebug("Running {RuntimeName} with arguments: {ArgumentList}", Name, spec.Arguments);
        var (pendingProcessResult, processDisposable) = _processRunner.Run(spec);

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
                if (processResult.TotalProcessOutputLineCount > 0)
                {
                    message = $"{message}{Environment.NewLine}{processResult.GetFormattedOutput(outputDescription: "Command output")}";
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
    /// <returns>The exit code of the process.</returns>
    protected async Task<int> ExecuteContainerCommandWithExitCodeAsync(
        string arguments,
        string errorLogTemplate,
        string successLogTemplate,
        CancellationToken cancellationToken,
        object[] logArguments,
        Dictionary<string, string>? environmentVariables = null)
    {
        var processResult = await ExecuteContainerCommandWithResultAsync(
            arguments,
            errorLogTemplate,
            successLogTemplate,
            cancellationToken,
            logArguments,
            environmentVariables).ConfigureAwait(false);

        return processResult.ExitCode;
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

    /// <summary>
    /// Executes a container runtime command and returns the process result without throwing for non-zero exit codes.
    /// </summary>
    protected async Task<ProcessResult> ExecuteContainerCommandWithResultAsync(
        string arguments,
        string errorLogTemplate,
        string successLogTemplate,
        CancellationToken cancellationToken,
        object[] logArguments,
        Dictionary<string, string>? environmentVariables = null,
        bool retainOutput = false)
    {
        var spec = CreateProcessSpec(arguments, retainOutput);

        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                spec.EnvironmentVariables[key] = value;
            }
        }

        _logger.LogDebug("Running {RuntimeName} with arguments: {ArgumentList}", Name, spec.Arguments);
        var (pendingProcessResult, processDisposable) = _processRunner.Run(spec);

        await using (processDisposable)
        {
            var processResult = await pendingProcessResult
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (processResult.ExitCode != 0)
            {
                var errorArgs = logArguments.Concat(new object[] { processResult.ExitCode }).ToArray();
                _logger.LogError(errorLogTemplate, errorArgs);
            }
            else
            {
                _logger.LogDebug(successLogTemplate, logArguments);
            }

            return processResult;
        }
    }

    protected async Task<string> ExecuteContainerCommandForOutputAsync(
        string arguments,
        string operationName,
        string imageName,
        CancellationToken cancellationToken)
    {
        var stdout = new List<string>();
        var spec = CreateProcessSpec(arguments, retainOutput: true, onOutputData: output =>
        {
            stdout.Add(output);
            _logger.LogDebug("{RuntimeName} (stdout): {Output}", RuntimeExecutable, output);
        });
        return await ExecuteContainerCommandForOutputAsync(spec, stdout, operationName, imageName, cancellationToken).ConfigureAwait(false);
    }

    protected async Task<string> ExecuteContainerCommandForOutputAsync(
        IReadOnlyList<string> argumentList,
        string operationName,
        string imageName,
        CancellationToken cancellationToken)
    {
        var stdout = new List<string>();
        var spec = CreateProcessSpec(argumentList, retainOutput: true, onOutputData: output =>
        {
            stdout.Add(output);
            _logger.LogDebug("{RuntimeName} (stdout): {Output}", RuntimeExecutable, output);
        });
        return await ExecuteContainerCommandForOutputAsync(spec, stdout, operationName, imageName, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ExecuteContainerCommandForOutputAsync(
        ProcessSpec spec,
        List<string> stdout,
        string operationName,
        string imageName,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Running {RuntimeName} with arguments: {ArgumentList}", Name, spec.ArgumentList ?? (object?)spec.Arguments);
        var (pendingProcessResult, processDisposable) = _processRunner.Run(spec);

        ProcessResult processResult;
        await using (processDisposable)
        {
            processResult = await pendingProcessResult
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (processResult.ExitCode != 0)
        {
            _logger.LogError("{RuntimeName} {OperationName} for {ImageName} failed with exit code {ExitCode}.", Name, operationName, imageName, processResult.ExitCode);
            throw new DistributedApplicationException($"{Name} {operationName} for '{imageName}' failed with exit code {processResult.ExitCode}.{Environment.NewLine}{processResult.GetFormattedOutput()}");
        }

        _logger.LogDebug("{RuntimeName} {OperationName} for {ImageName} succeeded.", Name, operationName, imageName);
        return string.Join(Environment.NewLine, stdout);
    }

    private ProcessSpec CreateProcessSpec(string arguments, bool retainOutput = false, Action<string>? onOutputData = null)
    {
        return CreateProcessSpecCore(arguments, argumentList: null, retainOutput, onOutputData);
    }

    private ProcessSpec CreateProcessSpec(IReadOnlyList<string> argumentList, bool retainOutput = false, Action<string>? onOutputData = null)
    {
        return CreateProcessSpecCore(arguments: null, argumentList, retainOutput, onOutputData);
    }

    private ProcessSpec CreateProcessSpecCore(
        string? arguments,
        IReadOnlyList<string>? argumentList,
        bool retainOutput,
        Action<string>? onOutputData)
    {
        return new ProcessSpec(RuntimeExecutable)
        {
            Arguments = arguments,
            ArgumentList = argumentList,
            RetainedOutputLineCount = retainOutput ? ProcessSpec.DefaultRetainedOutputLineCount : null,
            OnOutputData = onOutputData ?? (output =>
            {
                _logger.LogDebug("{RuntimeName} (stdout): {Output}", RuntimeExecutable, output);
            }),
            OnErrorData = error =>
            {
                _logger.LogDebug("{RuntimeName} (stderr): {Error}", RuntimeExecutable, error);
            },
            ThrowOnNonZeroReturnCode = false,
            InheritEnv = true
        };
    }

    protected static string EscapeArgument(string value) => value.Replace("\"", "\\\"", StringComparison.Ordinal);

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
            RetainedOutputLineCount = ProcessSpec.DefaultRetainedOutputLineCount,
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

        var (pendingProcessResult, processDisposable) = _processRunner.Run(spec);

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

                var message =
                    $"'{RuntimeExecutable} compose up' failed with exit code {processResult.ExitCode}. " +
                    $"Ensure '{RuntimeExecutable}' is installed and available on PATH. " +
                    envHint;

                if (processResult.TotalProcessOutputLineCount > 0)
                {
                    message = $"{message}{Environment.NewLine}{processResult.GetFormattedOutput()}";
                }

                throw new DistributedApplicationException(message);
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

        var (pendingProcessResult, processDisposable) = _processRunner.Run(spec);

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

        var (pendingProcessResult, processDisposable) = _processRunner.Run(spec);

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

            var (pendingResult, processDisposable) = _processRunner.Run(spec);
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
