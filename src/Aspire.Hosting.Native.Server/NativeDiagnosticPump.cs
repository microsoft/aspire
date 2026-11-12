// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Diagnostics;

namespace Aspire.Hosting.Native.Server;

/// <summary>Consumes one independent channel off the operation producer and drains on shutdown.</summary>
internal sealed class NativeDiagnosticPump : IAsyncDisposable
{
    private readonly OperationEventSubscription _subscription;
    private readonly Task _consumer;
    private readonly NativeServerOptions _options;

    public NativeDiagnosticPump(Action<OperationEvent> consume, Action<Exception> error, NativeServerOptions options)
    {
        options.Validate();
        _options = options;
        _subscription = NativeDiagnostics.Events.Subscribe();
        _consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var operation in _subscription.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    consume(operation);
                }
            }
            catch (Exception exception)
            {
                // Subscriber failures are isolated from host operations, and the
                // queue completes even when the consumer itself fails.
                _subscription.Dispose();
                error(exception);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        // A redirected stderr pipe may stop draining. It must not keep host
        // teardown alive indefinitely after its bounded subscription completes.
        await _consumer.WaitAsync(_options.CleanupTimeout, _options.TimeProvider).ConfigureAwait(false);
    }
}
