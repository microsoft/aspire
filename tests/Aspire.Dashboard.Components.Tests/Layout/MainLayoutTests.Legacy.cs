// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Legacy.Layout;
using Aspire.Dashboard.Components.Resize;
using Aspire.Tests.Shared;
using Bunit;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

public partial class MainLayoutTests
{
    [Fact]
    public async Task LegacyHeader_AIAgentsOpensDedicatedDialog()
    {
        var openedDialogIds = new List<string?>();
        var dialogService = new TestDialogService(onShowDialog: (_, parameters) =>
        {
            openedDialogIds.Add(parameters.Id);
            return Task.CompletedTask;
        });
        SetupMainLayoutServices(dialogService: dialogService);
        var cut = Render<LegacyMainLayout>(builder => builder.Add(p => p.ViewportInformation,
            new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false)));

        await cut.InvokeAsync(() => cut.Find($"#{LegacyMainLayout.AIAgentsButtonId}").Click());

        Assert.Equal(["AIAgentsDialog"], openedDialogIds);
    }
}
