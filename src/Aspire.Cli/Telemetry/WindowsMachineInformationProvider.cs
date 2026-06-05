// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Aspire.Cli.Telemetry;

// This is copied from https://github.com/microsoft/mcp/tree/6bb4d76a63d24854efe0fa0bd96f5ab6f699ed3a/core/Azure.Mcp.Core/src/Services/Telemetry
// Keep in sync with updates there.

[SupportedOSPlatform("windows")]
internal class WindowsMachineInformationProvider(ILogger<WindowsMachineInformationProvider> logger)
    : MachineInformationProviderBase(logger)
{
    // Construct the parts necessary to cache the ids in the registry.
    // The final path is HKEY_CURRENT_USER/SOFTWARE/Microsoft/DeveloperTools
    private const RegistryHive Hive = RegistryHive.CurrentUser;
    private const string RegistryPathRoot = $"SOFTWARE\\{MicrosoftDirectory}\\{DeveloperToolsDirectory}";

    private readonly ILogger<WindowsMachineInformationProvider> _logger = logger;

    public override Task<string?> GetOrCreateDeviceId()
    {
        return Task.Run<string?>(() =>
        {
            try
            {
                if (TryGetRegistryValue(RegistryPathRoot, DeviceId, out var existingDeviceId))
                {
                    return existingDeviceId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to fetch {Key} value from {RegistryRoot}.", DeviceId, RegistryPathRoot);
            }

            var newDeviceId = GenerateDeviceId();

            try
            {
                if (TrySetRegistryValue(RegistryPathRoot, DeviceId, newDeviceId))
                {
                    return newDeviceId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to persist {Key} in {RegistryPath}.", DeviceId, RegistryPathRoot);
            }

            return newDeviceId;
        });
    }

    private static bool TryGetRegistryValue(string registryRoot, string keyName, out string value)
    {
        using var registry = RegistryKey.OpenBaseKey(Hive, RegistryView.Registry64);
        using var key = registry.OpenSubKey(registryRoot, writable: false);

        if (key == null)
        {
            value = string.Empty;
            return false;
        }

        var matchingKeyName = key.GetValueNames().SingleOrDefault(x => string.Equals(x, keyName), null);
        if (matchingKeyName != null)
        {
            var existingValue = key.GetValue(matchingKeyName)?.ToString();
            value = existingValue ?? string.Empty;

            return !string.IsNullOrEmpty(existingValue);
        }

        value = string.Empty;
        return false;
    }

    private static bool TrySetRegistryValue(string keyPath, string keyName, string value)
    {
        using var keyRoot = RegistryKey.OpenBaseKey(Hive, RegistryView.Registry64);
        using var key = keyRoot.OpenSubKey(keyPath, writable: true)
            ?? keyRoot.CreateSubKey(keyPath, RegistryKeyPermissionCheck.ReadWriteSubTree);

        if (key == null)
        {
            return false;
        }

        key.SetValue(keyName, value, RegistryValueKind.String);

        return true;
    }
}
