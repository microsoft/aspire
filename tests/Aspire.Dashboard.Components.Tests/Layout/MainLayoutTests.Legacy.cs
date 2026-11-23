// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Dashboard.Components.Legacy.Layout;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task LegacyOnInitialize_StartsBrowserStorageAndBrowserInfoReadsConcurrently()
    {
        var expectedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            BrowserStorageKeys.TimeFormat,
            BrowserStorageKeys.NavMenuExpanded,
            BrowserStorageKeys.UnsecuredEndpointMessageDismissedKey,
            BrowserStorageKeys.UnsecuredTelemetryMessageDismissedKey
        };
        var requestedKeys = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var allReadsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void RecordKey(string key)
        {
            requestedKeys.TryAdd(key, 0);
            if (expectedKeys.All(requestedKeys.ContainsKey))
            {
                allReadsStarted.TrySetResult();
            }
        }

        var localStorage = new TestLocalStorage
        {
            OnBeforeGetAsync = key =>
            {
                RecordKey(key);
                return allReadsStarted.Task;
            },
            OnBeforeGetUnprotectedAsync = key =>
            {
                RecordKey(key);
                return allReadsStarted.Task;
            },
            OnGetAsync = key => key == BrowserStorageKeys.TimeFormat ? (true, TimeFormat.TwentyFourHour) : (false, null),
            OnGetUnprotectedAsync = key => key switch
            {
                BrowserStorageKeys.NavMenuExpanded => (true, false),
                BrowserStorageKeys.UnsecuredEndpointMessageDismissedKey => (true, true),
                BrowserStorageKeys.UnsecuredTelemetryMessageDismissedKey => (true, true),
                _ => (false, null)
            }
        };

        SetupMainLayoutServices(localStorage: localStorage);
        var browserInfoPlanned = JSInterop.Setup<BrowserInfo>("window.getBrowserInfo");

        var cut = Render<LegacyMainLayout>(builder => builder.Add(p => p.ViewportInformation,
            new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false)));

        await allReadsStarted.Task.DefaultTimeout();
        Assert.Single(JSInterop.Invocations["window.getBrowserInfo"]);

        browserInfoPlanned.SetResult(new BrowserInfo { TimeZone = "UTC", Is24HourTime = false, UserAgent = "test-agent" });

        var timeProvider = Services.GetRequiredService<BrowserTimeProvider>();
        cut.WaitForAssertion(() => Assert.Equal(TimeFormat.TwentyFourHour, timeProvider.ConfiguredTimeFormat));
    }
}
