// <copyright file="PolicyExpirationService.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

namespace ChaosProxy.Container.Policy;

/// <summary>
/// Background service that periodically sweeps expired policies from the
/// <see cref="ActivePolicyStore"/>. Per D6, TTL is a safety net against orphaned
/// policies - the primary lifecycle is explicit POST + DELETE driven by the harness.
/// </summary>
internal sealed class PolicyExpirationService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly ActivePolicyStore _store;
    private readonly ILogger<PolicyExpirationService> _logger;

    public PolicyExpirationService(ActivePolicyStore store, ILogger<PolicyExpirationService> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
                var removed = _store.SweepExpired();
                if (removed > 0)
                {
                    _logger.LogInformation("Swept {RemovedCount} expired chaos policies", removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Policy expiration sweep failed");
            }
        }
    }
}
