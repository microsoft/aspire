// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Bunit;
using Google.Protobuf.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.FluentUI.AspNetCore.Components;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class TracesTests : DashboardTestContext
{
    [Fact]
    public void Render_FocusesAccessibleScrollContainerOnInitialRender()
    {
        SetupTracesServices();

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = RenderComponent<Traces>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        var scrollContainer = cut.Find("#tracesScrollContainer");
        var loc = Services.GetRequiredService<IStringLocalizer<Dashboard.Resources.Traces>>();

        Assert.Equal("0", scrollContainer.GetAttribute("tabindex"));
        Assert.Equal("region", scrollContainer.GetAttribute("role"));
        Assert.Equal(loc[nameof(Dashboard.Resources.Traces.TracesHeader)].Value, scrollContainer.GetAttribute("aria-label"));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(JSInterop.Invocations, invocation =>
                invocation.Identifier == "focusElement" &&
                invocation.Arguments.Count == 2 &&
                string.Equals(invocation.Arguments[0]?.ToString(), "tracesScrollContainer", StringComparison.Ordinal) &&
                string.Equals(invocation.Arguments[1]?.ToString(), bool.TrueString, StringComparison.OrdinalIgnoreCase));
        });
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void Render_AtTraceLimit_LimitMessageOnlyDisplayedForLiveRun(bool isReadOnly, int expectedMessageCount)
    {
        var messageCount = 0;
        var messageService = new TestMessageService(_ =>
        {
            messageCount++;
            return Task.FromResult(new Message());
        });

        SetupTracesServices();
        Services.AddSingleton<IMessageService>(messageService);
        Services.AddSingleton<IOptions<DashboardOptions>>(Options.Create(new DashboardOptions
        {
            TelemetryLimits = { MaxTraceCount = 1 }
        }));

        var timestamp = DateTime.UnixEpoch;
        var telemetryRepository = Services.GetRequiredService<InMemoryTelemetryRepository>();
        telemetryRepository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan(traceId: "trace", spanId: "span", startTime: timestamp, endTime: timestamp.AddSeconds(1)) }
                    }
                }
            }
        });
        if (isReadOnly)
        {
            telemetryRepository.MakeReadOnly();
        }

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        Services.GetRequiredService<DimensionManager>().InvokeOnViewportInformationChanged(viewport);
        RenderComponent<Traces>(builder => builder.AddCascadingValue(viewport));

        Assert.Equal(expectedMessageCount, messageCount);
    }

    private void SetupTracesServices()
    {
        FluentUISetupHelpers.SetupFluentOverflow(this);
        FluentUISetupHelpers.SetupFluentDivider(this);
        FluentUISetupHelpers.SetupFluentInputLabel(this);
        FluentUISetupHelpers.SetupFluentDataGrid(this);
        FluentUISetupHelpers.SetupFluentList(this);
        FluentUISetupHelpers.SetupFluentSearch(this);
        FluentUISetupHelpers.SetupFluentKeyCode(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentToolbar(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);

        JSInterop.SetupVoid("initializeContinuousScroll").SetVoidResult();
        JSInterop.SetupVoid("resetContinuousScrollPosition").SetVoidResult();
        JSInterop.SetupVoid("focusElement", _ => true);

        FluentUISetupHelpers.AddCommonDashboardServices(this);
        Services.AddSingleton<ILogger<Traces>>(NullLogger<Traces>.Instance);
        Services.AddTransient<TracesViewModel>();
    }
}
