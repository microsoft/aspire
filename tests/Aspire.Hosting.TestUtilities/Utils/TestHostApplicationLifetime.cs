// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Tests.Utils;

public sealed class TestHostApplicationLifetime : IHostApplicationLifetime
{
    private readonly Func<CancellationToken>? _applicationStoppedProvider;

    public TestHostApplicationLifetime()
    {
    }

    public TestHostApplicationLifetime(Func<CancellationToken> applicationStoppedProvider)
    {
        ArgumentNullException.ThrowIfNull(applicationStoppedProvider);
        _applicationStoppedProvider = applicationStoppedProvider;
    }

    public CancellationToken ApplicationStarted { get; }
    public CancellationToken ApplicationStopped => _applicationStoppedProvider?.Invoke() ?? default;
    public CancellationToken ApplicationStopping { get; }

    public void StopApplication()
    {
        throw new NotImplementedException();
    }
}
