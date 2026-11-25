// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Resources;
using Aspire.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Dcp;

internal sealed partial class DcpDependencyCheck : IDcpDependencyCheckService
{
    [GeneratedRegex("[^\\d\\.].*$")]
    private static partial Regex VersionRegex();

    private readonly DcpOptions _dcpOptions;
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
    // The last successful DCP check, or null if no check has succeeded yet. Failed checks leave it unchanged.
    private DcpInfo? _dcpInfo;

    public DcpDependencyCheck(IOptions<DcpOptions> dcpOptions)
    {
        _dcpOptions = dcpOptions.Value;
    }

    public async Task<DcpInfo?> GetDcpInfoAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Only a 'dcp info' result includes the container runtime status.
            if (!force && _dcpInfo?.Containers is not null)
            {
                return _dcpInfo;
            }

            var arguments = "info";
            var containerRuntime = _dcpOptions.ContainerRuntime;
            if (!string.IsNullOrEmpty(containerRuntime))
            {
                arguments += $" --container-runtime {containerRuntime}";
            }

            _dcpInfo = await RunDcpAsync(arguments, cancellationToken).ConfigureAwait(false);
            return _dcpInfo;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DcpInfo?> GetDcpVersionAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_dcpInfo is not null)
            {
                return _dcpInfo;
            }

            // 'dcp version' validates the DCP installation without probing the container runtime,
            // which 'dcp info' does and which can hang when the runtime CLI is unresponsive.
            _dcpInfo = await RunDcpAsync("version", cancellationToken).ConfigureAwait(false);
            return _dcpInfo;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<DcpInfo> RunDcpAsync(string arguments, CancellationToken cancellationToken)
    {
        var dcpPath = _dcpOptions.CliPath;

        if (!File.Exists(dcpPath))
        {
            throw new FileNotFoundException($"The Aspire orchestration component is not installed at \"{dcpPath}\". The application cannot be run without it.", dcpPath);
        }

        IAsyncDisposable? processDisposable = null;
        Task<ProcessResult> task;
        var outputStringBuilder = new StringBuilder();
        var errorStringBuilder = new StringBuilder();

        try
        {
            var processSpec = new ProcessSpec(dcpPath)
            {
                Arguments = arguments,
                OnOutputData = s => outputStringBuilder.Append(s),
                OnErrorData = s => errorStringBuilder.Append(s),
                ThrowOnNonZeroReturnCode = false
            };

            (task, processDisposable) = ProcessUtil.Run(processSpec);
            ProcessResult processResult;

            // Disable timeout if DependencyCheckTimeout is set to zero or a negative value
            if (_dcpOptions.DependencyCheckTimeout > 0)
            {
                processResult = await task.WaitAsync(TimeSpan.FromSeconds(_dcpOptions.DependencyCheckTimeout), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                processResult = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (processResult.ExitCode != 0)
            {
                throw new DistributedApplicationException(string.Format(
                    CultureInfo.InvariantCulture,
                    MessageStrings.DcpDependencyCheckFailedMessage,
                    $"'dcp {arguments}' returned exit code {processResult.ExitCode}. {errorStringBuilder.ToString()}{Environment.NewLine}{outputStringBuilder.ToString()}"
                ));
            }

            // Parse the output as JSON
            var output = outputStringBuilder.ToString();
            var dcpInfo = string.IsNullOrWhiteSpace(output) ? null : JsonSerializer.Deserialize<DcpInfo>(output);
            if (dcpInfo is null)
            {
                throw new DistributedApplicationException(string.Format(
                    CultureInfo.InvariantCulture,
                    MessageStrings.DcpDependencyCheckFailedMessage,
                    $"'dcp {arguments}' did not return any information. {errorStringBuilder.ToString()}"
                ));
            }

            EnsureDcpVersion(dcpInfo);
            return dcpInfo;
        }
        catch (Exception ex) when
            (ex is not DistributedApplicationException
            && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            throw new DistributedApplicationException(string.Format(
                CultureInfo.InvariantCulture,
                MessageStrings.DcpDependencyCheckFailedMessage,
                $"{ex.Message} {errorStringBuilder.ToString()}{Environment.NewLine}{outputStringBuilder.ToString()}"
            ));
        }
        finally
        {
            if (processDisposable != null)
            {
                try
                {
                    await processDisposable.DisposeAsync().ConfigureAwait(false);
                }
                catch { } // Dispose (dcp process termination) is best effort.
            }
        }
    }

    private static void EnsureDcpVersion(DcpInfo dcpInfo)
    {
        var dcpVersionString = dcpInfo.VersionString;

        if (dcpVersionString == null
            || dcpVersionString == string.Empty
            || dcpVersionString == "dev")
        {
            // If empty, null, or a dev version, pass
            dcpInfo.Version = DcpVersion.Dev;
            return;
        }

        // Early DCP versions (e.g. preview 1) have a +x at the end of their version string, e.g. 0.1.42+5,
        // which does not parse. Strip off anything like that.
        dcpVersionString = VersionRegex().Replace(dcpVersionString, string.Empty);

        if (Version.TryParse(dcpVersionString, out var dcpVersion))
        {
            if (dcpVersion < DcpVersion.MinimumVersionInclusive)
            {
                throw new DistributedApplicationException(string.Format(
                    CultureInfo.InvariantCulture,
                    MessageStrings.DcpVersionCheckTooLowMessage,
                    GetCurrentPackageVersion(typeof(DcpDependencyCheck).Assembly)
                ));
            }

            dcpInfo.Version = dcpVersion;
        }
    }

    private static string GetCurrentPackageVersion(Assembly assembly)
    {
        // The package version is stamped into the assembly's AssemblyInformationalVersionAttribute at build time, followed by a '+' and
        // the commit hash, e.g.:
        // [assembly: AssemblyInformationalVersion("9.1.0-preview.1.25111.1+ad18db0213e9db8209bca0feb83fc801f34634f5)]

        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (version is not null)
        {
            var plusIndex = version.IndexOf('+');

            if (plusIndex > 0)
            {
                return version[..plusIndex];
            }

            return version;
        }

        // Fallback to the first 3 parts of the assembly version
        if (Version.TryParse(assembly.GetCustomAttribute<AssemblyVersionAttribute>()?.Version, out var assemblyVersion))
        {
            return assemblyVersion.ToString(3);
        }

        return "<unknown>";
    }

    internal static (string message, string? linkUrl) BuildContainerRuntimeUnhealthyMessage(string containerRuntime)
    {
        var messageBuilder = new StringBuilder();
        messageBuilder.AppendFormat(CultureInfo.InvariantCulture, InteractionStrings.ContainerRuntimeUnhealthyMessage, containerRuntime);
        string? linkUrl = null;

        if (string.Equals(containerRuntime, KnownContainerRuntimes.Docker, StringComparison.OrdinalIgnoreCase))
        {
            messageBuilder.Append(InteractionStrings.ContainerRuntimeDockerAdvice);
            linkUrl = "https://docs.docker.com/desktop/use-desktop/resource-saver/";
        }
        else if (string.Equals(containerRuntime, KnownContainerRuntimes.Podman, StringComparison.OrdinalIgnoreCase))
        {
            messageBuilder.Append(InteractionStrings.ContainerRuntimePodmanAdvice);
        }
        else
        {
            messageBuilder.Append(InteractionStrings.ContainerRuntimeGenericAdvice);
        }

        return (messageBuilder.ToString(), linkUrl);
    }

    internal static void CheckDcpInfoAndLogErrors(ILogger logger, DcpOptions options, DcpInfo dcpInfo, bool throwIfUnhealthy = false)
    {
        var containerRuntime = options.ContainerRuntime;
        if (string.IsNullOrEmpty(containerRuntime))
        {
            // Default runtime is Docker
            containerRuntime = KnownContainerRuntimes.Docker;
        }
        var installed = dcpInfo.Containers?.Installed ?? false;
        var running = dcpInfo.Containers?.Running ?? false;
        var rawError = dcpInfo.Containers?.Error;
        var error = string.IsNullOrWhiteSpace(rawError) ? null : rawError.Trim();

        if (!installed)
        {
            const string messageFormat = "Container runtime '{0}' could not be found. See https://aka.ms/aspire/containers for more details on supported container runtimes.";
            var message = string.Format(CultureInfo.InvariantCulture, messageFormat, containerRuntime);
            var detailedMessage = AppendContainerRuntimeCheckError(message, error);

            logger.LogWarning("{Message}", detailedMessage);
            if (throwIfUnhealthy)
            {
                throw new DistributedApplicationException(detailedMessage);
            }
        }
        else if (!running)
        {
            var (message, linkUrl) = BuildContainerRuntimeUnhealthyMessage(containerRuntime);

            var logMessage = message;
            if (linkUrl is not null)
            {
                logMessage += " For more information, visit: " + linkUrl;
            }

            logger.LogWarning("{Message}", AppendContainerRuntimeCheckError(logMessage, error));
            if (throwIfUnhealthy)
            {
                throw new DistributedApplicationException(AppendContainerRuntimeCheckError(message, error));
            }
        }
    }

    private static string AppendContainerRuntimeCheckError(string message, string? error)
    {
        return error is null
            ? message
            : $"{message}{Environment.NewLine}The error from the container runtime check was: {error}";
    }
}
