// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Aspire.Shared.TerminalHost;

/// <summary>
/// Source-generated serialization for the terminal host control protocol.
/// </summary>
[JsonSerializable(typeof(TerminalHostSessionInfo))]
[JsonSerializable(typeof(TerminalHostInfoResponse))]
[JsonSerializable(typeof(CommonErrorData))]
// StreamJsonRpc uses object for untyped results (including shutdown's null response) and error-data fallback.
[JsonSerializable(typeof(object))]
internal partial class TerminalHostControlJsonSerializerContext : JsonSerializerContext
{
    [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:RequiresUnreferencedCode", Justification = "Using the Json source generator.")]
    [UnconditionalSuppressMessage("AotAnalysis", "IL3050:RequiresDynamicCode", Justification = "Using the Json source generator.")]
    internal static SystemTextJsonFormatter CreateRpcMessageFormatter()
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(Default, new RequestIdTypeInfoResolver())
        };
        return formatter;
    }
}
