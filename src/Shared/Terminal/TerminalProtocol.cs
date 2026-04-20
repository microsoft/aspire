// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Terminal;

/// <summary>
/// Constants and types for the Aspire Terminal Protocol.
/// See docs/specs/terminal-protocol.md for the full specification.
/// </summary>
internal static class TerminalProtocol
{
    /// <summary>
    /// Current protocol version.
    /// </summary>
    public const byte Version = 1;

    /// <summary>
    /// Maximum allowed payload size (1 MiB).
    /// </summary>
    public const int MaxPayloadSize = 1_048_576;

    /// <summary>
    /// Size of the frame header (1 byte type + 4 bytes length).
    /// </summary>
    public const int FrameHeaderSize = 5;

    /// <summary>
    /// Frame message types.
    /// </summary>
    public static class MessageType
    {
        /// <summary>Server → Client. First frame on connection with version, dimensions, and flags.</summary>
        public const byte Hello = 0x01;

        /// <summary>Either direction. Raw terminal bytes (PTY output or user input).</summary>
        public const byte Data = 0x02;

        /// <summary>Either direction. Terminal resize notification.</summary>
        public const byte Resize = 0x03;

        /// <summary>Server → Client. Process exited with exit code and reason.</summary>
        public const byte Exit = 0x04;

        /// <summary>Either direction. Graceful session shutdown.</summary>
        public const byte Close = 0x05;
    }

    /// <summary>
    /// HELLO frame flags.
    /// </summary>
    [Flags]
    public enum HelloFlags : byte
    {
        /// <summary>No flags set.</summary>
        None = 0,

        /// <summary>The server has a real PTY allocated.</summary>
        Pty = 1 << 0,

        /// <summary>The server will replay buffered terminal state after HELLO.</summary>
        Replay = 1 << 1,
    }

    /// <summary>
    /// EXIT frame reason codes.
    /// </summary>
    public enum ExitReason : byte
    {
        /// <summary>Process exited normally.</summary>
        Exited = 0x00,

        /// <summary>Process was killed.</summary>
        Killed = 0x01,

        /// <summary>Server is detaching from the PTY (process may continue).</summary>
        Detached = 0x02,

        /// <summary>Server encountered an unrecoverable error.</summary>
        Error = 0x03,
    }

    /// <summary>
    /// Fixed payload sizes for structured message types.
    /// </summary>
    public static class PayloadSize
    {
        /// <summary>HELLO: version(1) + cols(2) + rows(2) + flags(1) = 6 bytes.</summary>
        public const int Hello = 6;

        /// <summary>RESIZE: cols(2) + rows(2) = 4 bytes.</summary>
        public const int Resize = 4;

        /// <summary>EXIT: exitCode(4) + reason(1) = 5 bytes.</summary>
        public const int Exit = 5;

        /// <summary>CLOSE: no payload.</summary>
        public const int Close = 0;
    }
}
