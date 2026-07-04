// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Go;

/// <summary>
/// Annotation that configures the Go application to start a headless Delve debug server,
/// enabling remote debugging from GoLand or any DAP-compatible client.
/// </summary>
internal sealed class GoDelveServerAnnotation(GoDelveServerOptions options) : IResourceAnnotation
{
    public int Port { get; } = options.Port;
    public bool AcceptMulticlient { get; } = options.AcceptMulticlient;
    public bool? OnlySameUser { get; } = options.OnlySameUser;
    public bool ContinueOnStart { get; } = options.ContinueOnStart;
    public bool Log { get; } = options.Log;
    public string LogOutput { get; } = options.LogOutput;
}
