// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Telemetry;

// This is copied from https://github.com/microsoft/mcp/tree/6bb4d76a63d24854efe0fa0bd96f5ab6f699ed3a/core/Azure.Mcp.Core/src/Services/Telemetry
// Keep in sync with updates there.

internal abstract class MachineInformationProviderBase(ILogger<MachineInformationProviderBase> logger)
    : IMachineInformationProvider
{
    protected const string MicrosoftDirectory = "Microsoft";
    protected const string DeveloperToolsDirectory = "DeveloperTools";
    protected const string NotAvailable = "N/A";

    /// <summary>
    /// The name of the registry key or file containing device id.
    /// </summary>
    protected const string DeviceId = "deviceid";

    private static readonly SHA256 s_sHA256 = SHA256.Create();

    private readonly ILogger<MachineInformationProviderBase> _logger = logger;

    /// <inheritdoc/>
    public abstract Task<string?> GetOrCreateDeviceId();

    /// <inheritdoc/>
    public virtual Task<string> GetMacAddressHash()
    {
        return Task.Run(() =>
        {
            try
            {
                var address = GetMacAddress();

                return address != null
                    ? HashValue(address)
                    : NotAvailable;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to calculate MAC address hash.");
                return NotAvailable;
            }
        });
    }

    /// <summary>
    /// Searches for first network interface card that is up and has a physical address.
    /// </summary>
    /// <returns>Hash of the MAC address or <see cref="NotAvailable"/> if none can be found.</returns>
    protected virtual string? GetMacAddress()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(x => x.GetPhysicalAddress().ToString())
            .FirstOrDefault(x => !string.IsNullOrEmpty(x));
    }

    /// <summary>
    /// Generates a new device identifier.  Conditions that have to be met are:
    /// <list type="bullet">
    /// <item>Randomly generated UUID/GUID</item>
    /// <item>Follows the 8-4-4-4-12 format (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)</item>
    /// <item>Contains only lowercase characters and hyphens</item>
    /// </list>
    /// </summary>
    /// <returns>The generated device identifier.</returns>
    protected static string GenerateDeviceId() => Guid.NewGuid().ToString("D").ToLowerInvariant();

    /// <summary>
    /// Generates a SHA-256 of the given value.
    /// </summary>
    protected static string HashValue(string value)
    {
        var hashInput = s_sHA256.ComputeHash(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToString(hashInput).Replace("-", string.Empty).ToLowerInvariant();
    }
}
