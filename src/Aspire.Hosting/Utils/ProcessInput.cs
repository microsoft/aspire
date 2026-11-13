// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace Aspire.Hosting.Utils;

/// <summary>
/// Owns input content independently of an invocation's asynchronous delivery.
/// </summary>
internal sealed class ProcessInput
{
    private ProcessInput(byte[] bytes) => Bytes = bytes;

    /// <summary>
    /// Gets the owned bytes to deliver before closing standard input.
    /// </summary>
    // ReadOnlyMemory provides a read-only view, not immutable storage. The factories
    // establish ownership by copying caller bytes or retaining a newly allocated encoding result.
    internal ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>
    /// Copies bytes synchronously into owned storage for later asynchronous delivery.
    /// </summary>
    /// <param name="bytes">The bytes to copy. Later changes to the caller's storage do not affect the input.</param>
    /// <returns>Input backed by a copy of <paramref name="bytes"/>.</returns>
    public static ProcessInput FromBytes(ReadOnlySpan<byte> bytes) => new(bytes.ToArray());

    /// <summary>
    /// Encodes text without adding a preamble or newline.
    /// </summary>
    /// <param name="text">The text to encode.</param>
    /// <param name="encoding">The encoding used to produce the input bytes.</param>
    /// <returns>Input that owns the byte array allocated by the encoding operation.</returns>
    // GetBytes already allocates the array this instance owns; another defensive copy is unnecessary.
    public static ProcessInput FromText(string text, Encoding encoding) => new(encoding.GetBytes(text));
}
