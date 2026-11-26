// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.EntityFrameworkCore;

namespace Aspire.Microting.EntityFrameworkCore.MySql.Tests;

public class WorkaroundToReadProtectedField : MySqlRetryingExecutionStrategy
{
    public WorkaroundToReadProtectedField(DbContext context) : base(context)
    {
    }

    public int RetryCount => base.MaxRetryCount;
}
