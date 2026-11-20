// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.EntityFrameworkCore.Storage;

namespace Aspire.Npgsql.EntityFrameworkCore.PostgreSQL.Tests;

public class CustomExecutionStrategy : ExecutionStrategy
{
    public CustomExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : base(dependencies, DefaultMaxRetryCount, DefaultMaxDelay)
    {
    }

    protected override bool ShouldRetryOn(Exception exception) => false;
}
