// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a VS Code server ready action applied to a project resource.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Action = {ServerReadyAction.Action,nq}")]
internal sealed class VSCodeServerReadyActionAnnotation(VSCodeServerReadyAction serverReadyAction) : IResourceAnnotation
{
    public VSCodeServerReadyAction ServerReadyAction { get; } = serverReadyAction ?? throw new ArgumentNullException(nameof(serverReadyAction));
}
