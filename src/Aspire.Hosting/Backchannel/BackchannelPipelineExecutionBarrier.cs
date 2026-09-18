// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Backchannel;

/// <summary>
/// Coordinates pipeline execution with the CLI backchannel activity stream.
/// </summary>
internal sealed class BackchannelPipelineExecutionBarrier
{
    private readonly TaskCompletionSource _executionAllowed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitForExecutionAllowedAsync(CancellationToken cancellationToken)
        => _executionAllowed.Task.WaitAsync(cancellationToken);

    public void AllowExecution()
        => _executionAllowed.TrySetResult();
}
