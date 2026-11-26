// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Backchannel;

/// <summary>
/// The exception that is thrown when a backchannel request fails before it is handed to the RPC connection, so the
/// AppHost cannot have received it.
/// </summary>
/// <remarks>
/// Callers use this to tell a request that provably never reached the AppHost apart from one whose delivery is
/// uncertain. A failure raised by the RPC connection itself, such as a lost connection, does not prove that the
/// request was not delivered and is therefore not wrapped in this exception.
/// </remarks>
internal sealed class AppHostRequestNotSentException(string message, Exception innerException) : Exception(message, innerException)
{
}
