// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Lifetime modes for resources that can outlive the app host process.
/// </summary>
public enum Lifetime
{
    /// <summary>
    /// Create the resource when the app host process starts and dispose of it when the app host process shuts down.
    /// </summary>
    Session,

    /// <summary>
    /// Attempt to re-use a previously created resource if one exists. Do not destroy the resource on app host process shutdown.
    /// </summary>
    Persistent,
}
