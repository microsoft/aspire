// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Backchannel;

internal sealed class AppHostStartupState
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AppHostStartupState(IConfiguration configuration)
    {
        if (string.IsNullOrEmpty(configuration["REMOTE_APP_HOST_SOCKET_PATH"]))
        {
            _ready.TrySetResult();
        }
    }

    public bool IsReady => _ready.Task.IsCompletedSuccessfully;

    public void MarkReady() => _ready.TrySetResult();

    public async Task WaitForReadyAsync(CancellationToken cancellationToken)
    {
        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
