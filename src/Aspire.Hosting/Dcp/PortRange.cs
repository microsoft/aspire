// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Shared validation helpers for TCP/UDP port numbers so the valid port bounds are defined in
/// exactly one place rather than sprinkled as magic numbers throughout the orchestration code.
/// </summary>
internal static class PortRange
{
    /// <summary>
    /// The lowest valid port number. Port 0 is excluded because it signals "pick an ephemeral port".
    /// </summary>
    public const int MinPort = 1;

    /// <summary>
    /// The highest valid port number (the maximum value representable in a 16-bit port field).
    /// </summary>
    public const int MaxPort = 65535;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="port"/> is a usable port number.
    /// </summary>
    public static bool IsValidPort(int port)
    {
        return port is >= MinPort and <= MaxPort;
    }

    /// <summary>
    /// Validates that <paramref name="rangeStart"/> and <paramref name="rangeEnd"/> form a valid,
    /// non-empty, ordered port range, throwing if not.
    /// </summary>
    public static void ValidateRange(int rangeStart, int rangeEnd, string rangeStartParamName, string rangeEndParamName)
    {
        if (!IsValidPort(rangeStart))
        {
            throw new ArgumentOutOfRangeException(rangeStartParamName, rangeStart, $"Port range start must be between {MinPort} and {MaxPort}.");
        }

        if (!IsValidPort(rangeEnd))
        {
            throw new ArgumentOutOfRangeException(rangeEndParamName, rangeEnd, $"Port range end must be between {MinPort} and {MaxPort}.");
        }

        if (rangeStart > rangeEnd)
        {
            throw new ArgumentException("Port range start must be less than or equal to the range end.", rangeStartParamName);
        }
    }
}
