// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.EntityFrameworkCore.Storage;
using Microting.EntityFrameworkCore.MySql.Storage.Internal;

namespace Aspire.Microting.EntityFrameworkCore.MySql.Tests;

#pragma warning disable EF1001 // Internal EF Core API usage.
public class CustomExecutionStrategy : MySqlExecutionStrategy
{
    public CustomExecutionStrategy(ExecutionStrategyDependencies dependencies) : base(dependencies)
    {
    }
}
#pragma warning restore EF1001 // Internal EF Core API usage.
