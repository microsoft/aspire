// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace Aspire.Hosting.Dcp.Process;

/// <summary>
/// Owns input content independently of an invocation's asynchronous delivery.
/// </summary>
internal sealed class ProcessInput
{
    private ProcessInput(byte[] bytes) => Bytes = bytes;

    /// <summary>The owned bytes to deliver before closing stdin.</summary>
    internal ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>Synchronously copies bytes so stack-backed or subsequently mutated input is safe.</summary>
    public static ProcessInput FromBytes(ReadOnlySpan<byte> bytes) => new(bytes.ToArray());

    /// <summary>Encodes text without adding a preamble or newline.</summary>
    public static ProcessInput FromText(string text, Encoding encoding) => new(encoding.GetBytes(text));
}
