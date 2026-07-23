// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RabbitMQ.Provisioning;

internal static class RabbitMQExchangeBindingReconciler
{
    internal static IResourceBuilder<RabbitMQExchangeResource> WithExchangeBindings(
        this IResourceBuilder<RabbitMQExchangeResource> exchangeBuilder)
    {
        var exchange = exchangeBuilder.Resource;

        var bindingErrors = new ConcurrentBag<string>();
        var bindingsDone = false;
        var bindingsKey = $"{exchange.Name}_bindings_check";

        exchangeBuilder.ApplicationBuilder.Services.AddHealthChecks().AddAsyncCheck(
            bindingsKey,
            _ =>
            {
                if (!Volatile.Read(ref bindingsDone))
                {
                    return Task.FromResult(HealthCheckResult.Unhealthy(
                        $"Bindings for exchange '{exchange.ExchangeName}' are not yet applied."));
                }

                return Task.FromResult(bindingErrors.IsEmpty
                    ? HealthCheckResult.Healthy()
                    : HealthCheckResult.Unhealthy(
                        $"Binding failures for '{exchange.ExchangeName}': {string.Join("; ", bindingErrors)}"));
            });

        // StartCore publishes ResourceReadyEvent for the exchange after setting state to Running,
        // independently of health checks. This fires on both initial startup and every restart,
        // making it the correct trigger for (re-)applying bindings without any circular dependency
        // on the exchange's own health.
        exchangeBuilder.ApplicationBuilder.Eventing.Subscribe<ResourceReadyEvent>(exchange, async (evt, ct) =>
        {
            var notifications = evt.Services.GetRequiredService<ResourceNotificationService>();
            var logger = evt.Services.GetRequiredService<ResourceLoggerService>().GetLogger(exchange);

            try
            {
                var client = evt.Services.GetRequiredKeyedService<IRabbitMQProvisioningClient>(
                    exchange.VirtualHost.Parent.Name);

                // Clear any errors from a previous run before re-applying.
                while (bindingErrors.TryTake(out _)) { }

                await Task.WhenAll(exchange.Bindings.Select(async binding =>
                {
                    // Wait for the destination to be Running (declared on the broker) before binding.
                    // Running is sufficient — it means ReconcileAsync completed and the queue/exchange
                    // exists on the broker. We do not wait for Healthy to avoid a potential circular
                    // dependency if the destination's health check depends on this exchange's bindings.
                    await notifications.WaitForResourceAsync(
                        binding.Destination.Name, KnownResourceStates.Running, ct).ConfigureAwait(false);
                    try
                    {
                        await binding.Destination.BindAsync(
                            client,
                            exchange.VirtualHost.VirtualHostName,
                            exchange.ExchangeName,
                            binding.RoutingKey,
                            binding.MatchHeaders,
                            ct).ConfigureAwait(false);
                    }
                    catch (Exception bindEx)
                    {
                        bindingErrors.Add(
                            $"Binding to '{binding.Destination.ProvisionedName}': {bindEx.Message}");
                        logger.LogError(bindEx,
                            "Failed to apply binding from '{Exchange}' to '{Destination}'.",
                            exchange.ExchangeName, binding.Destination.ProvisionedName);
                    }
                })).ConfigureAwait(false);

                Volatile.Write(ref bindingsDone, true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        });

        // Reset bindingsDone when the exchange is stopped so the health check correctly
        // reports "not applied" while the exchange is stopped or being restarted.
        exchangeBuilder.ApplicationBuilder.Eventing.Subscribe<ResourceStoppedEvent>(exchange, (evt, ct) =>
        {
            Volatile.Write(ref bindingsDone, false);
            while (bindingErrors.TryTake(out _)) { }
            return Task.CompletedTask;
        });

        return exchangeBuilder.WithHealthCheck(bindingsKey);
    }
}
