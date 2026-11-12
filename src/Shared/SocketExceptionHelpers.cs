// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;

namespace Aspire.Shared;

/// <summary>
/// Classifies socket transport failures.
/// </summary>
internal static class SocketExceptionHelpers
{
    /// <summary>
    /// Identifies an I/O exception wrapping a socket connection reset.
    /// </summary>
    internal static bool IsConnectionReset(Exception exception)
    {
        return exception is IOException { InnerException: SocketException { SocketErrorCode: SocketError.ConnectionReset } };
    }
}
