// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.Native.Rpc;

/// <summary>Separates bidirectional message transport from capability dispatch and provider activation.</summary>
internal interface INativeRpcPeer : IDisposable
{
    CancellationToken Closed { get; }
    /// <summary>The borrowed envelope remains valid until dispatch completes.</summary>
    Task RunAsync(Func<JsonElement, Task<byte[]?>> dispatch, CancellationToken cancellationToken);
    Task<JsonNode?> RequestAsync(string method, JsonArray arguments, CancellationToken cancellationToken);
    Task SendAsync(byte[] payload, CancellationToken cancellationToken);
}

internal interface INativeRpcPeerFactory
{
    INativeRpcPeer Create(Stream stream);
}
