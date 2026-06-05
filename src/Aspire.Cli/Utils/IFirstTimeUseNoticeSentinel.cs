// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

/// <summary>
/// Provides functionality to track and manage the first-time use notice sentinel for the Aspire CLI.
/// </summary>
internal interface IFirstTimeUseNoticeSentinel
{
    /// <summary>
    /// Determines whether the first-time use sentinel file exists.
    /// </summary>
    /// <returns><see langword="true"/> if the sentinel file exists; otherwise, <see langword="false"/>.</returns>
    bool Exists();

    /// <summary>
    /// Creates the first-time use sentinel file if it does not already exist.
    /// </summary>
    void CreateIfNotExists();
}
