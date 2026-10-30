// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Native.Client;

/// <summary>Reports an ATS capability failure independently of JSON-RPC transport errors.</summary>
/// <param name="code">The server's stable failure classification.</param>
/// <param name="message">The server's sanitized failure message.</param>
public sealed class NativeCapabilityException(string code, string message) : Exception(message)
{
    /// <summary>Gets the server-provided ATS failure classification.</summary>
    public string Code { get; } = code;
}
