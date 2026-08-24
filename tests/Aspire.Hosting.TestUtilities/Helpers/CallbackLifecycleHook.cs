// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Lifecycle;

namespace Aspire.Hosting.Tests.Helpers;

#pragma warning disable CS0618 // Type or member is obsolete
internal sealed class CallbackLifecycleHook : IDistributedApplicationLifecycleHook
#pragma warning restore CS0618 // Type or member is obsolete
{
    private readonly Func<DistributedApplicationModel, CancellationToken, Task> _beforeStartCallback;

    public CallbackLifecycleHook(Func<DistributedApplicationModel, CancellationToken, Task> beforeStartCallback)
    {
        _beforeStartCallback = beforeStartCallback;
    }

    public Task BeforeStartAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken = default)
    {
        if (_beforeStartCallback != null)
        {
            return _beforeStartCallback(appModel, cancellationToken);
        }

        return Task.CompletedTask;
    }
}
