// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Hosting.Maui.Utilities;

/// <summary>
/// Maps an Android resource's <c>AdbTarget</c> to the devices reported by the Android SDK's
/// <c>ComputeAvailableDevices</c> target.
/// </summary>
internal static class MauiAndroidDeviceSelection
{
    internal const string EmulatorType = "Emulator";
    internal const string DeviceType = "Device";

    /// <summary>
    /// Gets the serial from an <c>-s &lt;serial&gt;</c> target, or <see langword="null"/> for other targets.
    /// </summary>
    public static string? GetSerial(string adbTarget)
    {
        var parts = adbTarget.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts is ["-s", var serial] ? serial : null;
    }

    /// <summary>
    /// Gets the <c>ComputeAvailableDevices</c> device type that <c>-e</c> (the only emulator) or <c>-d</c>
    /// (the only USB device) selects, or <see langword="null"/> for other targets.
    /// </summary>
    public static string? GetDeviceType(string adbTarget) => adbTarget.Trim() switch
    {
        "-e" => EmulatorType,
        "-d" => DeviceType,
        _ => null
    };

    /// <summary>
    /// Gets the online devices of the given type from <c>dotnet msbuild -getItem:Devices</c> output.
    /// </summary>
    public static IReadOnlyList<string> GetOnlineDevices(string devicesJson, string deviceType)
    {
        var start = devicesJson.IndexOf('{');
        if (start < 0)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(devicesJson.AsMemory(start));
            if (!document.RootElement.TryGetProperty("Items", out var items) ||
                !items.TryGetProperty("Devices", out var devices) ||
                devices.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var online = new List<string>();
            foreach (var device in devices.EnumerateArray())
            {
                if (GetString(device, "Identity") is { Length: > 0 } identity &&
                    string.Equals(GetString(device, "Type"), deviceType, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(GetString(device, "Status"), "Online", StringComparison.OrdinalIgnoreCase))
                {
                    online.Add(identity);
                }
            }

            return online;
        }
        catch (JsonException)
        {
            return [];
        }

        static string? GetString(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
