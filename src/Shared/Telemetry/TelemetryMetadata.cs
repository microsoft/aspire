// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Shared.Telemetry;

/// <summary>
/// Calculates common machine, environment, and Microsoft-internal telemetry metadata.
/// </summary>
internal static class TelemetryMetadata
{
    internal static IMachineInformationProvider CreateMachineInformationProvider(ILoggerFactory loggerFactory, ITelemetryEnvironment environment)
    {
        if (environment.IsWindows())
        {
            return new WindowsMachineInformationProvider(loggerFactory.CreateLogger<WindowsMachineInformationProvider>());
        }
        if (environment.IsMacOS())
        {
            return new MacOSXMachineInformationProvider(loggerFactory.CreateLogger<MacOSXMachineInformationProvider>());
        }
        if (environment.IsLinux())
        {
            return new LinuxMachineInformationProvider(loggerFactory.CreateLogger<LinuxMachineInformationProvider>());
        }

        return new DefaultMachineInformationProvider(loggerFactory.CreateLogger<MachineInformationProviderBase>());
    }

    internal static IInternalMicrosoftDetector CreateInternalMicrosoftDetector(ILoggerFactory loggerFactory, bool isCIEnvironment)
    {
        var environment = new TelemetryEnvironment();
        return new InternalMicrosoftDetector(
            new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            new DirectoryInfo(Environment.CurrentDirectory),
            new DirectoryInfo(Path.Combine(AspireHomeDirectory.GetDefault(), "cache")),
            environment, TimeProvider.System, loggerFactory.CreateLogger<InternalMicrosoftDetector>(),
            (startInfo, stdout, stderr) => new ChildProcess(startInfo, NullLogger.Instance,
                new ChildProcessOptions { StandardOutputCallback = stdout, StandardErrorCallback = stderr },
                environment.IsWindows()),
            isCIEnvironment);
    }

    internal static async Task<IReadOnlyList<KeyValuePair<string, object?>>> GetMachineTagsAsync(IMachineInformationProvider provider)
    {
        var macAddressHash = provider.GetMacAddressHash();
        var deviceId = provider.GetOrCreateDeviceId();
        Task[] tasks = [macAddressHash, deviceId];
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return
        [
            new("machine.mac_address_hash", macAddressHash.Result),
            new("machine.device_id", deviceId.Result)
        ];
    }

    internal static List<KeyValuePair<string, object?>> CreateDefaultTags(
        IReadOnlyList<KeyValuePair<string, object?>> machineTags, bool isCIEnvironment,
        InternalMicrosoftDetectionResult? internalMicrosoftResult, string productPrefix)
    {
        List<KeyValuePair<string, object?>> tags = [.. machineTags];
        if (internalMicrosoftResult is not null)
        {
            tags.Add(new($"{productPrefix}.is_microsoft_internal", internalMicrosoftResult.IsInternalMicrosoft));
            if (internalMicrosoftResult.IsInternalMicrosoft)
            {
                if (!string.IsNullOrEmpty(internalMicrosoftResult.Source))
                {
                    tags.Add(new($"{productPrefix}.microsoft_internal_source", internalMicrosoftResult.Source));
                }
                if (!isCIEnvironment && !string.IsNullOrEmpty(internalMicrosoftResult.Alias))
                {
                    tags.Add(new($"{productPrefix}.microsoft_internal_alias", internalMicrosoftResult.Alias));
                }
                if (!isCIEnvironment && !string.IsNullOrEmpty(internalMicrosoftResult.Domain))
                {
                    tags.Add(new($"{productPrefix}.microsoft_internal_domain", internalMicrosoftResult.Domain));
                }
            }
        }

        tags.Add(new("deployment.environment.name", isCIEnvironment ? "ci" : "local"));
        tags.Add(new("os.name", OperatingSystemTelemetry.GetOsName()));
        tags.Add(new("os.type", OperatingSystemTelemetry.GetOsType()));
        tags.Add(new("os.version", OperatingSystemTelemetry.GetOsVersion()));
        return tags;
    }
}
