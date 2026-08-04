// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace Aspire.Hosting.Dcp;

internal static class DcpPipelineBuilder
{
    public static ResiliencePipeline<bool> BuildDeleteRetryPipeline(ILogger logger)
    {
        var ensureDeleteRetryStrategy = new RetryStrategyOptions<bool>()
        {
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromMilliseconds(200),
            UseJitter = true,
            MaxRetryAttempts = 10, // Cumulative time for all attempts amounts to about 15 seconds
            MaxDelay = TimeSpan.FromSeconds(3),
            ShouldHandle = args => ValueTask.FromResult(!args.Outcome.Result),
            OnRetry = (retry) =>
            {
                logger.LogDebug("Retrying check for deleted resource. Attempt: {Attempt}. Error message: {ErrorMessage}", retry.AttemptNumber, retry.Outcome.Exception?.Message);
                return ValueTask.CompletedTask;
            }
        };

        var execution = new ResiliencePipelineBuilder<bool>().AddRetry(ensureDeleteRetryStrategy).Build();
        return execution;
    }

    public static ResiliencePipeline BuildObjectWatchRetryPipeline(DcpOptions dcpOptions, ILogger logger, TimeSpan? timeout = null)
    {
        var withTimeout = new TimeoutStrategyOptions()
        {
            Timeout = timeout ?? dcpOptions.ServiceStartupWatchTimeout
        };

        var tryTwice = new RetryStrategyOptions()
        {
            BackoffType = DelayBackoffType.Constant,
            MaxDelay = TimeSpan.FromSeconds(1),
            UseJitter = true,
            MaxRetryAttempts = 1,
            ShouldHandle = new PredicateBuilder().Handle<Exception>(),
            OnRetry = (retry) =>
            {
                logger.LogDebug(
                    retry.Outcome.Exception,
                    "Watching for service port allocation ended with an error after {WatchDurationMs} (iteration {Iteration})",
                    retry.Duration.TotalMilliseconds,
                    retry.AttemptNumber
                );
                return ValueTask.CompletedTask;
            }
        };

        var execution = new ResiliencePipelineBuilder().AddRetry(tryTwice).AddTimeout(withTimeout).Build();
        return execution;
    }

    public static ResiliencePipeline BuildWatchResourcePipeline(ILogger logger)
    {
        var retryUntilCancelled = new RetryStrategyOptions()
        {
            ShouldHandle = new PredicateBuilder().HandleInner<EndOfStreamException>(),
            BackoffType = DelayBackoffType.Exponential,
            MaxRetryAttempts = int.MaxValue,
            UseJitter = true,
            MaxDelay = TimeSpan.FromSeconds(30),
            OnRetry = (retry) =>
            {
                logger.LogDebug(
                    retry.Outcome.Exception,
                    "Long poll watch operation was ended by server after {LongPollDurationInMs} milliseconds (iteration {Iteration}).",
                    retry.Duration.TotalMilliseconds,
                    retry.AttemptNumber
                    );
                return ValueTask.CompletedTask;
            }
        };

        var pipeline = new ResiliencePipelineBuilder().AddRetry(retryUntilCancelled).Build();
        return pipeline;
    }
}
