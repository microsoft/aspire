// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace AspireWithBlazor.ClientServiceDefaults.Telemetry.Serializer;

/// <summary>
/// Protobuf wire types as defined in the Protocol Buffers specification.
/// </summary>
internal enum ProtobufWireType
{
    /// <summary>
    /// Variable-length integer (int32, int64, uint32, uint64, sint32, sint64, bool, enum).
    /// </summary>
    VARINT = 0,

    /// <summary>
    /// 64-bit fixed length (fixed64, sfixed64, double).
    /// </summary>
    I64 = 1,

    /// <summary>
    /// Length-delimited (string, bytes, embedded messages, packed repeated fields).
    /// </summary>
    LEN = 2,

    /// <summary>
    /// 32-bit fixed length (fixed32, sfixed32, float).
    /// </summary>
    I32 = 5,
}
