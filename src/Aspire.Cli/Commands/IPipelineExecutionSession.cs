// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;

namespace Aspire.Cli.Commands;

/// <summary>
/// Coordinates command-specific work around a pipeline AppHost execution.
/// </summary>
internal interface IPipelineExecutionSession : IAsyncDisposable
{
    /// <summary>
    /// Gets the physical primary output path passed to the AppHost.
    /// </summary>
    string OutputPath { get; }

    /// <summary>
    /// Gets hidden configuration arguments appended to the AppHost invocation.
    /// </summary>
    IReadOnlyList<string> AdditionalAppHostArguments { get; }

    /// <summary>
    /// Validates the frozen pipeline plan and authorizes execution.
    /// </summary>
    Task PreflightAndAuthorizeAsync(IAppHostCliBackchannel backchannel, CancellationToken cancellationToken);

    /// <summary>
    /// Captures the final pipeline state before the AppHost is stopped.
    /// </summary>
    Task CaptureFinalStateAsync(IAppHostCliBackchannel backchannel, CancellationToken cancellationToken);

    /// <summary>
    /// Completes command-specific processing after the AppHost has stopped successfully.
    /// </summary>
    Task<CommandResult> CompleteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Represents a publish verification preflight or reconciliation infrastructure failure.
/// </summary>
internal sealed class PublishVerificationException(string message) : Exception(message);
