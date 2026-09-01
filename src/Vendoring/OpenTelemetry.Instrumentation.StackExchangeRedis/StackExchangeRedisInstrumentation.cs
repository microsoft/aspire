// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Options;
using OpenTelemetry.Internal;
using StackExchange.Redis;

namespace OpenTelemetry.Instrumentation.StackExchangeRedis;

/// <summary>
/// StackExchange.Redis instrumentation.
/// </summary>
internal sealed class StackExchangeRedisInstrumentation : IDisposable
{
    private readonly IOptionsMonitor<StackExchangeRedisInstrumentationOptions> options;

    internal StackExchangeRedisInstrumentation(
        IOptionsMonitor<StackExchangeRedisInstrumentationOptions> options)
    {
        this.options = options;
    }

    internal List<StackExchangeRedisConnectionInstrumentation> InstrumentedConnections { get; } = [];

    /// <summary>
    /// Adds an <see cref="IConnectionMultiplexer"/> to the instrumentation.
    /// </summary>
    /// <param name="connection"><see cref="IConnectionMultiplexer"/>.</param>
    /// <returns><see cref="IDisposable"/> to cancel the registration.</returns>
    public IDisposable AddConnection(IConnectionMultiplexer connection)
        => this.AddConnection(Options.DefaultName, connection);

    /// <summary>
    /// Adds an <see cref="IConnectionMultiplexer"/> to the instrumentation.
    /// </summary>
    /// <param name="name">Name to use when retrieving options.</param>
    /// <param name="connection"><see cref="IConnectionMultiplexer"/>.</param>
    /// <returns><see cref="IDisposable"/> to cancel the registration.</returns>
    public IDisposable AddConnection(string name, IConnectionMultiplexer connection)
    {
        Guard.ThrowIfNull(name);
        Guard.ThrowIfNull(connection);

        var options = this.options.Get(name);

        lock (this.InstrumentedConnections)
        {
            var instrumentation = new StackExchangeRedisConnectionInstrumentation(connection, name, options);

            this.InstrumentedConnections.Add(instrumentation);

            return new StackExchangeRedisConnectionInstrumentationRegistration(() =>
            {
                lock (this.InstrumentedConnections)
                {
                    if (this.InstrumentedConnections.Remove(instrumentation))
                    {
                        instrumentation.Dispose();
                    }
                }
            });
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (this.InstrumentedConnections)
        {
            foreach (var instrumentation in this.InstrumentedConnections)
            {
                instrumentation.Dispose();
            }

            this.InstrumentedConnections.Clear();
        }
    }

    private sealed class StackExchangeRedisConnectionInstrumentationRegistration : IDisposable
    {
        private readonly Action disposalAction;

        public StackExchangeRedisConnectionInstrumentationRegistration(
            Action disposalAction)
        {
            this.disposalAction = disposalAction;
        }

        public void Dispose()
        {
            this.disposalAction();
        }
    }
}
