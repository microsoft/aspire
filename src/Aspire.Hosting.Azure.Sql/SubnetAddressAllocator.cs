// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using Aspire.Hosting.Azure;

namespace Aspire.Hosting;

/// <summary>
/// Allocates subnet address space within a virtual network.
/// </summary>
[Experimental("ASPIREAZURE003")]
internal static class SubnetAddressAllocator
{
    /// <summary>
    /// Allocates a /29 subnet from the highest available address in the virtual network.
    /// </summary>
    /// <param name="vnet">The virtual network to allocate from.</param>
    /// <param name="existingSubnets">All existing subnets in the VirtualNetwork (from the app model).</param>
    /// <returns>The CIDR notation for the allocated subnet (e.g., "10.0.255.248/29").</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the VirtualNetwork address prefix is parameterized, or when no space is available.
    /// </exception>
    public static string AllocateDeploymentScriptSubnet(AzureVirtualNetworkResource vnet, IEnumerable<AzureSubnetResource> existingSubnets)
    {
        const int prefixLength = 29;
        const uint blockSize = 8; // 2^(32-29) = 8

        var vnetAddressPrefix = vnet.AddressPrefix
            ?? throw new InvalidOperationException(
                $"Cannot automatically allocate a deployment script subnet for virtual network '{vnet.Name}' because it uses a parameterized address prefix. " +
                $"Use 'WithAdminDeploymentScriptSubnet' to provide an explicit subnet.");

        var (vnetStart, vnetEnd) = ParseCidr(vnetAddressPrefix);

        // Ensure the VNet is large enough to contain at least one /29 block
        if (vnetEnd - vnetStart + 1 < blockSize)
        {
            throw new InvalidOperationException(
                $"Virtual network '{vnet.Name}' address space '{vnetAddressPrefix}' is too small to allocate a /{prefixLength} subnet (requires at least {blockSize} addresses). " +
                $"Use 'WithAdminDeploymentScriptSubnet' to provide an explicit subnet.");
        }

        // Collect all existing subnet ranges
        var existingRanges = new List<(uint Start, uint End)>();
        foreach (var subnet in existingSubnets)
        {
            if (subnet.AddressPrefix is { } subnetCidr)
            {
                var range = ParseCidr(subnetCidr);
                existingRanges.Add(range);
            }
            // Skip subnets with parameterized addresses — can't check overlap
        }

        // Start from the highest /29-aligned address and work downward
        var candidate = (vnetEnd - blockSize + 1) & ~(blockSize - 1);

        while (candidate >= vnetStart)
        {
            var candidateEnd = candidate + blockSize - 1;

            if (candidateEnd <= vnetEnd && !OverlapsAny(candidate, candidateEnd, existingRanges))
            {
                return $"{UintToIp(candidate)}/{prefixLength}";
            }

            if (candidate < blockSize)
            {
                break; // Prevent underflow
            }

            candidate -= blockSize;
        }

        throw new InvalidOperationException(
            $"Cannot allocate a /29 subnet in virtual network '{vnet.Name}' (address space: {vnetAddressPrefix}). " +
            $"No non-overlapping address space is available. " +
            $"Use 'WithAdminDeploymentScriptSubnet' to provide an explicit subnet.");
    }

    private static bool OverlapsAny(uint start, uint end, List<(uint Start, uint End)> ranges)
    {
        foreach (var (rStart, rEnd) in ranges)
        {
            if (start <= rEnd && rStart <= end)
            {
                return true;
            }
        }

        return false;
    }

    internal static (uint Start, uint End) ParseCidr(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix > 32)
        {
            throw new FormatException($"Invalid CIDR notation: '{cidr}'.");
        }

        var ip = IPAddress.Parse(parts[0]);
        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new FormatException($"Only IPv4 CIDR notation is supported: '{cidr}'.");
        }

        var bytes = ip.GetAddressBytes();
        var address = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);

        // Compute the network mask
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var networkAddress = address & mask;
        var broadcastAddress = networkAddress | ~mask;

        return (networkAddress, broadcastAddress);
    }

    private static string UintToIp(uint address)
    {
        return $"{(address >> 24) & 0xFF}.{(address >> 16) & 0xFF}.{(address >> 8) & 0xFF}.{address & 0xFF}";
    }
}
