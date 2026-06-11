// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a container or project application launch argument.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Argument = {Argument}, IsSensitive = {IsSensitive}, EffectiveArgumentIndex = {EffectiveArgumentIndex}")]
internal sealed class AppLaunchArgumentAnnotation(string argument, bool isSensitive, int? effectiveArgumentIndex = null)
{
    /// <summary>
    /// The evaluated launch argument.
    /// </summary>
    public string Argument { get; } = argument;

    /// <summary>
    /// Whether the argument contains a secret.
    /// </summary>
    public bool IsSensitive { get; } = isSensitive;

    /// <summary>
    /// The index of the corresponding effective launch argument, if one exists.
    /// </summary>
    public int? EffectiveArgumentIndex { get; } = effectiveArgumentIndex;
}
