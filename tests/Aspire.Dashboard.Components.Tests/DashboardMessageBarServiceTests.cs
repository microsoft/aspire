// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests;

public class DashboardMessageBarServiceTests
{
    [Fact]
    public async Task ShowAsync_ResultCompletesBeforeVisible_Throws()
    {
        var result = new TestMessageBarResult(MessageBarCloseReason.Programmatic);
        var opened = new TaskCompletionSource<IMessageBarInstance>(TaskCreationOptions.RunContinuationsAsynchronously);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => DashboardMessageBarService.WaitForVisibleAsync(
            opened.Task,
            Task.FromResult<MessageBarResult>(result)).WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal("The message bar closed before becoming visible.", exception.Message);
    }
}

internal sealed class TestMessageBarResult(MessageBarCloseReason reason, object? data = null) : MessageBarResult(reason, data);