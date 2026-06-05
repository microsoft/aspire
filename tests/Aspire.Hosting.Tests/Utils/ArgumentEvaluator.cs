// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Utils;

public sealed class ArgumentEvaluator
{
    public static async ValueTask<List<string>> GetArgumentListAsync(IResource resource, IServiceProvider? serviceProvider = null)
    {
        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run)
            {
                ServiceProvider = serviceProvider,
            });

        var executionConfiguration = await ExecutionConfigurationBuilder.Create(resource)
            .WithArgumentsConfig()
            .BuildAsync(executionContext, NullLogger.Instance, CancellationToken.None)
            .ConfigureAwait(false);

        if (executionConfiguration.Exception is not null)
        {
            ExceptionDispatchInfo.Throw(executionConfiguration.Exception);
        }

        return executionConfiguration.Arguments.Select(a => a.Value).ToList();
    }
}
