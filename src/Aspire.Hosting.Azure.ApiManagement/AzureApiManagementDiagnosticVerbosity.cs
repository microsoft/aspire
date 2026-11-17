// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Specifies the verbosity of an API Management diagnostic.
/// </summary>
public enum AzureApiManagementDiagnosticVerbosity
{
    /// <summary>
    /// Records only errors.
    /// </summary>
    Error,

    /// <summary>
    /// Records errors and informational events.
    /// </summary>
    Information,

    /// <summary>
    /// Records detailed diagnostic events.
    /// </summary>
    Verbose,
}
