// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Tests.Utils;

public sealed class TestHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted { get; }
    public CancellationToken ApplicationStopped { get; }
    public CancellationToken ApplicationStopping { get; }

    public void StopApplication()
    {
        throw new NotImplementedException();
    }
}
