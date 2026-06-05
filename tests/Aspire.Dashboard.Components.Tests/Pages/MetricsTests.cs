// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Web;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Bunit;
using Google.Protobuf.Collections;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using OpenTelemetry.Proto.Metrics.V1;
using Xunit;
using static Aspire.Dashboard.Components.Pages.Metrics;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public partial class MetricsTests : DashboardTestContext
{
    private static readonly DateTime s_testTime = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ChangeResource_MeterAndInstrumentOnNewResource_InstrumentSet()
    {
        ChangeResourceAndAssertInstrument(
            app1InstrumentName: "test1",
            app2InstrumentName: "test1",
            expectedMeterNameAfterChange: "test-meter",
            expectedInstrumentNameAfterChange: "test1");
    }

    [Fact]
    public void ChangeResource_MeterAndInstrumentNotOnNewResources_InstrumentCleared()
    {
        ChangeResourceAndAssertInstrument(
            app1InstrumentName: "test1",
            app2InstrumentName: "test2",
            expectedMeterNameAfterChange: null,
            expectedInstrumentNameAfterChange: null);
    }

    [Fact]
    public async Task InitialLoad_SingleResource_RedirectToResource()
    {
        // Arrange
        MetricsSetupHelpers.SetupMetricsPage(this);

        var navigationManager = Services.GetRequiredService<NavigationManager>();

        var targetLocationTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var changeHandler = navigationManager.RegisterLocationChangingHandler(c =>
        {
            targetLocationTcs.SetResult(c.TargetLocation);
            return ValueTask.CompletedTask;
        });

        var telemetryRepository = Services.GetRequiredService<TelemetryRepository>();
        telemetryRepository.AddMetrics(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test-instrument", startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        // Act
        var cut = RenderComponent<Metrics>(builder =>
        {
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        // Assert
        Assert.NotNull(targetLocationTcs);
        Assert.Equal("/metrics/resource/TestApp?duration=5", await targetLocationTcs.Task.DefaultTimeout());
    }

    [Fact]
    public void InitialLoad_HasSessionState_RedirectUsingState()
    {
        // Arrange
        var testSessionStorage = new TestSessionStorage
        {
            OnGetAsync = key =>
            {
                if (key == BrowserStorageKeys.MetricsPageState)
                {
                    var state = new MetricsPageState
                    {
                        ResourceName = "TestApp2",
                        MeterName = "test-meter",
                        InstrumentName = "test-instrument",
                        DurationMinutes = 720,
                        ViewKind = MetricViewKind.Table.ToString()
                    };
                    return (true, state);
                }
                else
                {
                    throw new InvalidOperationException("Unexpected key: " + key);
                }
            }
        };

        MetricsSetupHelpers.SetupMetricsPage(this, sessionStorage: testSessionStorage);

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.MetricsUrl());

        Uri? loadRedirect = null;
        navigationManager.LocationChanged += (s, a) =>
        {
            loadRedirect = new Uri(a.Location);
        };

        var telemetryRepository = Services.GetRequiredService<TelemetryRepository>();
        telemetryRepository.AddMetrics(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test-instrument", startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            },
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp2"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test-instrument", startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        // Act
        var cut = RenderComponent<Metrics>(builder =>
        {
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        // Assert
        Assert.NotNull(loadRedirect);
        Assert.Equal("/metrics/resource/TestApp2", loadRedirect.AbsolutePath);

        var query = HttpUtility.ParseQueryString(loadRedirect.Query);
        Assert.Equal("test-meter", query["meter"]);
        Assert.Equal("test-instrument", query["instrument"]);
        Assert.Equal("720", query["duration"]);
        Assert.Equal(MetricViewKind.Table.ToString(), query["view"]);
    }

    [Fact]
    public void MetricsTree_MetricsAdded_TreeUpdated()
    {
        // Arrange
        MetricsSetupHelpers.SetupMetricsPage(this);

        var telemetryRepository = Services.GetRequiredService<TelemetryRepository>();
        telemetryRepository.AddMetrics(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter1"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test-instrument1-1", startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        // Act 1
        // Initial page load
        var cut = RenderComponent<Metrics>(builder =>
        {
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
            builder.Add(m => m.ResourceName, "TestApp");
        });

        // Assert 2
        cut.WaitForState(() => cut.Instance.PageViewModel.Instruments?.Count == 1);

        cut.WaitForAssertion(() =>
        {
            // Assert in wait to make sure rendering has caught up to data update.
            var tree1 = cut.FindComponent<FluentTreeView>();
            var items1 = tree1.FindComponents<FluentTreeItem>();

            foreach (var instrument in cut.Instance.PageViewModel.Instruments!)
            {
                Assert.Single(items1, i => i.Instance.Data as OtlpInstrumentSummary == instrument);
                Assert.Single(items1, i => i.Instance.Data as string == instrument.Parent.Name);
            }
        });

        // Act 2
        // New instruments added
        telemetryRepository.AddMetrics(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter1"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test-instrument1-2", startTime: s_testTime.AddMinutes(1))
                        }
                    },
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter2"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test-instrument2-1", startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        // Assert 2
        cut.WaitForState(() => cut.Instance.PageViewModel.Instruments?.Count == 3);

        cut.WaitForAssertion(() =>
        {
            // Assert in wait to make sure rendering has caught up to data update.
            var tree2 = cut.FindComponent<FluentTreeView>();
            var items2 = tree2.FindComponents<FluentTreeItem>();

            foreach (var instrument in cut.Instance.PageViewModel.Instruments!)
            {
                Assert.Single(items2, i => i.Instance.Data as OtlpInstrumentSummary == instrument);
                Assert.Single(items2, i => i.Instance.Data as string == instrument.Parent.Name);
            }
        });
    }

    private void ChangeResourceAndAssertInstrument(string app1InstrumentName, string app2InstrumentName, string? expectedMeterNameAfterChange, string? expectedInstrumentNameAfterChange)
    {
        // Arrange
        MetricsSetupHelpers.SetupMetricsPage(this);

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(DashboardUrls.MetricsUrl(resource: "TestApp", meter: "test-meter", instrument: app1InstrumentName, duration: 720, view: MetricViewKind.Table.ToString()));

        var telemetryRepository = Services.GetRequiredService<TelemetryRepository>();
        telemetryRepository.AddMetrics(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: app1InstrumentName, startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            },
            new ResourceMetrics
            {
                Resource = CreateResource(name: "TestApp2"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: app2InstrumentName, startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        // Act 1
        var cut = RenderComponent<Metrics>(builder =>
        {
            builder.Add(m => m.ResourceName, "TestApp");
            builder.AddCascadingValue(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        });

        navigationManager.LocationChanged += (sender, e) =>
        {
            var expectedUrl = DashboardUrls.MetricsUrl(resource: "TestApp2", meter: expectedMeterNameAfterChange, instrument: expectedInstrumentNameAfterChange, duration: 720, view: "Table");
            Assert.EndsWith(expectedUrl, e.Location);

            cut.SetParametersAndRender(builder =>
            {
                builder.Add(m => m.ResourceName, "TestApp2");
            });
        };

        var viewModel = cut.Instance.PageViewModel;

        // Assert 1
        Assert.Equal("test-meter", viewModel.SelectedMeter);
        Assert.Equal(app1InstrumentName, viewModel.SelectedInstrument!.Name);

        // Act 2
        var resourceSelect = cut.FindComponent<ResourceSelect>();
        var innerSelect = resourceSelect.Find("fluent-select");
        innerSelect.Change("TestApp2");

        cut.WaitForAssertion(() => Assert.Equal("TestApp2", viewModel.SelectedResource.Name));

        Assert.Equal(expectedInstrumentNameAfterChange, viewModel.SelectedInstrument?.Name);
        Assert.Equal(expectedMeterNameAfterChange, viewModel.SelectedMeter);

        Assert.Equal(MetricViewKind.Table, viewModel.SelectedViewKind);
        Assert.Equal(TimeSpan.FromMinutes(720), viewModel.SelectedDuration.Id);
    }
}
