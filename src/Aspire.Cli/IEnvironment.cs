// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared.Telemetry;

namespace Aspire.Cli;

/// <summary>
/// Abstracts environment queries so both <see cref="CliExecutionContext"/> and
/// <see cref="Acquisition.IdentityResolver"/> can read environment variables
/// and detect the host OS without a circular dependency between them.
/// </summary>
public interface IEnvironment : ITelemetryEnvironment
{
    /// <summary>
    /// Gets the runtime identifier for the current OS and architecture.
    /// </summary>
    string RuntimeIdentifier { get; }
}
