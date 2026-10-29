// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Utils;

public static class ShutdownDiagnosticCapture
{
    public static IDisposable Attach(IServiceCollection services, Action<string> write)
    {
        var capture = ShutdownDiagnostics.Capture(write);
        // Host stops services in reverse registration order. Preserve every real service's order
        // and type; decorators would change BackgroundService and lifecycle-service semantics.
        services.Insert(0, ServiceDescriptor.Singleton<IHostedService>(_ => new ShutdownPhaseProbe("last")));
        services.AddSingleton<IHostedService>(_ => new ShutdownPhaseProbe("first"));
        return capture;
    }

    private sealed class ShutdownPhaseProbe(string boundary) : IHostedLifecycleService, IDisposable
    {
        private IDisposable? _stopTokenObservation;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken cancellationToken)
        {
            _stopTokenObservation = ShutdownDiagnostics.Observe($"host.stop-token.{boundary}", cancellationToken);
            ShutdownDiagnostics.Mark($"host.StoppingAsync.{boundary}", cancellationToken);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            ShutdownDiagnostics.Mark($"host.StopAsync.{boundary}", cancellationToken);
            return Task.CompletedTask;
        }

        public Task StoppedAsync(CancellationToken cancellationToken)
        {
            ShutdownDiagnostics.Mark($"host.StoppedAsync.{boundary}", cancellationToken);
            return Task.CompletedTask;
        }

        public void Dispose() => _stopTokenObservation?.Dispose();
    }
}
