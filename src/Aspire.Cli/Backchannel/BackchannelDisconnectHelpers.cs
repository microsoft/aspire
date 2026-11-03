// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using StreamJsonRpc;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Classifies connection loss shared by AppHost backchannel clients and follow commands.
/// </summary>
internal static class BackchannelDisconnectHelpers
{
    /// <summary>
    /// Identifies exceptions caused by a closed backchannel rather than a failed RPC.
    /// </summary>
    internal static bool IsExpectedDisconnect(Exception ex)
    {
        return ex is ConnectionLostException
            || ex is ObjectDisposedException
            || ex is OperationCanceledException { InnerException: ConnectionLostException };
    }
}
