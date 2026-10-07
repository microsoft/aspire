// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Aspire.Tray;

/// <summary>
/// Writes only the owned Run value in an explicitly supplied registry root and key.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsRunStartupRegistrationStore(RegistryKey root, string keyPath, string valueName) : ITrayStartupRegistrationStore
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string RunValueName = "AspireTray";

    public string? Read()
    {
        using var key = root.OpenSubKey(keyPath, writable: false);
        return ReadValue(key);
    }

    public void Write(string? expected, string? value)
    {
        using var key = value is null ? root.OpenSubKey(keyPath, writable: true) : root.CreateSubKey(keyPath, writable: true);
        // Windows exposes no registry compare-and-swap. Check again using the writable
        // handle immediately before changing our one value; never touch StartupApproved.
        if (!string.Equals(ReadValue(key), expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The startup registration changed outside Aspire. It was left unchanged.");
        }
        if (value is null)
        {
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        else
        {
            key!.SetValue(valueName, value, RegistryValueKind.String);
        }
    }

    private string? ReadValue(RegistryKey? key)
    {
        var value = key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null)
        {
            return null;
        }
        if (key!.GetValueKind(valueName) != RegistryValueKind.String || value is not string command)
        {
            throw new InvalidOperationException("An unrelated registry value occupies Aspire's startup location. It was left unchanged.");
        }
        return command;
    }
}
