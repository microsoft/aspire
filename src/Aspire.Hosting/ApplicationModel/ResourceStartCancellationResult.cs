// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// The outcome of <see cref="ResourceCommandService.TryCancelPendingStartAsync(string, CancellationToken)"/>.
/// </summary>
[Experimental("ASPIREWATCH001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum ResourceStartCancellationResult
{
    /// <summary>
    /// A start of the instance was canceled before it was launched, for example while it waited for its dependencies.
    /// The instance is <see cref="KnownResourceStates.NotStarted"/>, unless a start of it that began after the
    /// cancellation determines its state.
    /// </summary>
    Canceled,

    /// <summary>
    /// No start of the instance was pending, and the instance isn't running, so there's nothing to stop.
    /// </summary>
    NotStarted,

    /// <summary>
    /// A start of the instance was already launched, so it can't be canceled. Stop the instance to stop what it launched.
    /// </summary>
    Started,
}
