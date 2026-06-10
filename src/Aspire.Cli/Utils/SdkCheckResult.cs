// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

/// <summary>
/// Outcome values for the SDK check operation.
/// </summary>
internal enum SdkCheckResult
{
    /// <summary>
    /// A valid SDK was already installed.
    /// </summary>
    AlreadyInstalled,

    /// <summary>
    /// The SDK is missing or does not meet the minimum required version.
    /// </summary>
    NotInstalled
}
