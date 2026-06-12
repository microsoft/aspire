// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Xunit;

namespace Aspire.Dashboard.Tests;

public sealed class DashboardErrorModeTests
{
    [Fact]
    public void ErrorMode_NoFailures_NotInErrorMode()
    {
        var validationFailures = Array.Empty<string>();

        var errorMode = new DashboardErrorMode(validationFailures);

        Assert.False(errorMode.IsErrorMode);
        Assert.False(errorMode.IsDismissed);
        Assert.False(errorMode.ShouldBlock);
        Assert.Empty(errorMode.ValidationFailures);
    }

    [Fact]
    public void ErrorMode_WithFailures_IsInErrorMode()
    {
        var validationFailures = new List<string>
        {
            "Error 1",
            "Error 2"
        };

        var errorMode = new DashboardErrorMode(validationFailures);

        Assert.True(errorMode.IsErrorMode);
        Assert.False(errorMode.IsDismissed);
        Assert.True(errorMode.ShouldBlock);
        Assert.Equal(2, errorMode.ValidationFailures.Count);
    }

    [Fact]
    public void ErrorMode_Dismiss_StopsBlocking()
    {
        var validationFailures = new List<string> { "Error 1" };
        var errorMode = new DashboardErrorMode(validationFailures);

        errorMode.Dismiss();

        Assert.True(errorMode.IsErrorMode);
        Assert.True(errorMode.IsDismissed);
        Assert.False(errorMode.ShouldBlock);
    }

    [Fact]
    public void ErrorMode_MultipleDismiss_RemainsActive()
    {
        var validationFailures = new List<string> { "Error 1" };
        var errorMode = new DashboardErrorMode(validationFailures);

        errorMode.Dismiss();
        errorMode.Dismiss();

        Assert.True(errorMode.IsDismissed);
        Assert.False(errorMode.ShouldBlock);
    }

    [Fact]
    public void ErrorMode_NullValidationFailures_TreatedAsEmpty()
    {
        var errorMode = new DashboardErrorMode(null!);

        Assert.False(errorMode.IsErrorMode);
        Assert.Empty(errorMode.ValidationFailures);
    }
}
