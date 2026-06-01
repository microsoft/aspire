// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.GenAI;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Bunit;
using Google.Protobuf.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class GenAIVisualizerDialogTests : DashboardTestContext
{
    private static readonly DateTime s_testTime = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Render_NoGenAIAttributes_Success()
    {
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var resource = new OtlpResource("app", "instance", uninstrumentedPeer: false, context);

        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = CreateOtlpScope(context);

        var cut = SetUpDialog(out var dialogService);
        await GenAIVisualizerDialog.OpenDialogAsync(
            dialogService: dialogService,
            span: CreateOtlpSpan(resource, trace, scope, spanId: "abc", parentSpanId: null, startDate: s_testTime),
            selectedLogEntryId: null,
            telemetryRepository: Services.GetRequiredService<TelemetryRepository>(),
            errorRecorder: new TestTelemetryErrorRecorder(),
            resources: [],
            getContextGenAISpans: () => []
            );

        var instance = cut.FindComponent<GenAIVisualizerDialog>().Instance;

        Assert.Null(instance.Content.DisplayErrorMessage);
        Assert.Empty(instance.Content.Items);
        Assert.Equal("app", instance.Content.SourceName);
        Assert.Equal("unknown-peer", instance.Content.PeerName);
    }

    [Fact]
    public async Task Render_HasGenAIMessages_Success()
    {
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var resource = new OtlpResource("app", "instance", uninstrumentedPeer: false, context);

        var systemInstruction = JsonSerializer.Serialize(new List<MessagePart>
        {
            new TextPart { Content = "System!" }
        }, GenAIMessagesContext.Default.ListMessagePart);

        var inputMessages = JsonSerializer.Serialize(new List<ChatMessage>
        {
            new ChatMessage
            {
                Role = "user",
                Parts = [new TextPart { Content = "User!" }]
            },
            new ChatMessage
            {
                Role = "assistant",
                Parts = [new ToolCallRequestPart { Name = "generate_names", Arguments = JsonNode.Parse(@"{""count"":2}") }]
            },
            new ChatMessage
            {
                Role = "user",
                Parts = [new ToolCallResponsePart { Response = JsonNode.Parse(@"[""Jack"",""Jane""]") }]
            }
        }, GenAIMessagesContext.Default.ListChatMessage);

        var outputMessages = JsonSerializer.Serialize(new List<ChatMessage>
        {
            new ChatMessage
            {
                Role = "assistant",
                Parts = [new TextPart { Content = "Output!" }]
            }
        }, GenAIMessagesContext.Default.ListChatMessage);

        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = CreateOtlpScope(context);
        var span = CreateOtlpSpan(resource, trace, scope, spanId: "abc", parentSpanId: null, startDate: s_testTime, attributes: [
            KeyValuePair.Create(GenAIHelpers.GenAISystemInstructions, systemInstruction),
            KeyValuePair.Create(GenAIHelpers.GenAIInputMessages, inputMessages),
            KeyValuePair.Create(GenAIHelpers.GenAIOutputInstructions, outputMessages)
        ]);

        var cut = SetUpDialog(out var dialogService);
        await GenAIVisualizerDialog.OpenDialogAsync(
            dialogService: dialogService,
            span: span,
            selectedLogEntryId: null,
            telemetryRepository: Services.GetRequiredService<TelemetryRepository>(),
            errorRecorder: new TestTelemetryErrorRecorder(),
            resources: [],
            getContextGenAISpans: () => []
            );

        var instance = cut.FindComponent<GenAIVisualizerDialog>().Instance;

        Assert.Equal(5, instance.Content.Items.Count);
    }

    [Fact]
    public async Task UpdateTelemetry_DifferentTrace_ContentInstanceUnchanged()
    {
        // Arrange - Setup dialog infrastructure and repository
        var cut = SetUpDialog(out var dialogService);
        var repository = Services.GetRequiredService<TelemetryRepository>();
        
        // Add initial trace to repository for the dialog to display
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "app", instanceId: "instance"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "trace1", spanId: "span1", startTime: s_testTime, endTime: s_testTime.AddSeconds(1))
                        }
                    }
                }
            }
        });

        // Get the resource and trace
        var resources = repository.GetResources();
        var resource = resources[0];
        var tracesResult = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resource.ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        var trace = tracesResult.PagedResult.Items[0];
        var span = trace.Spans[0];

        // Open dialog
        await GenAIVisualizerDialog.OpenDialogAsync(
            dialogService: dialogService,
            span: span,
            selectedLogEntryId: null,
            telemetryRepository: repository,
            errorRecorder: new TestTelemetryErrorRecorder(),
            resources: resources,
            getContextGenAISpans: () => []
        );

        var instance = cut.FindComponent<GenAIVisualizerDialog>().Instance;
        var originalContent = instance.Content;

        // Act - Add a DIFFERENT trace to the repository
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "app", instanceId: "instance"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "trace2", spanId: "span2-1", startTime: s_testTime, endTime: s_testTime.AddSeconds(1))
                        }
                    }
                }
            }
        });

        // Wait a moment for potential subscription callbacks to fire
        await Task.Delay(100);

        // Assert - Content instance should remain the same since a different trace was updated
        var currentContent = cut.FindComponent<GenAIVisualizerDialog>().Instance.Content;
        Assert.Same(originalContent, currentContent);
    }

    [Fact]
    public async Task UpdateTelemetry_SameTrace_ContentInstanceChanged()
    {
        // Arrange - Setup dialog infrastructure and repository
        var cut = SetUpDialog(out var dialogService);
        var repository = Services.GetRequiredService<TelemetryRepository>();
        
        // Add initial trace to repository for the dialog to display
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "app", instanceId: "instance"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "trace1", spanId: "span1", startTime: s_testTime, endTime: s_testTime.AddSeconds(1))
                        }
                    }
                }
            }
        });

        // Get the resource and trace
        var resources = repository.GetResources();
        var resource = resources[0];
        var tracesResult = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resource.ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        var trace = tracesResult.PagedResult.Items[0];
        var span = trace.Spans[0];

        // Create a function that retrieves the current list of spans from the trace
        List<OtlpSpan> GetContextGenAISpans()
        {
            var currentTrace = repository.GetTraces(new GetTracesRequest
            {
                ResourceKey = resource.ResourceKey,
                StartIndex = 0,
                Count = 10,
                Filters = []
            }).PagedResult.Items.FirstOrDefault(t => t.TraceId == trace.TraceId);
            
            return currentTrace?.Spans.ToList() ?? [];
        }

        // Open dialog with the function that can retrieve updated spans
        await GenAIVisualizerDialog.OpenDialogAsync(
            dialogService: dialogService,
            span: span,
            selectedLogEntryId: null,
            telemetryRepository: repository,
            errorRecorder: new TestTelemetryErrorRecorder(),
            resources: resources,
            getContextGenAISpans: GetContextGenAISpans
        );

        var instance = cut.FindComponent<GenAIVisualizerDialog>().Instance;
        var originalContent = instance.Content;

        // Act - Add a new span to the SAME trace that the dialog is displaying
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "app", instanceId: "instance"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "trace1",
                                spanId: "span2",
                                startTime: s_testTime.AddSeconds(1),
                                endTime: s_testTime.AddSeconds(2),
                                parentSpanId: "span1")
                        }
                    }
                }
            }
        });

        // Assert - Wait for the dialog to update its Content property
        cut.WaitForAssertion(() =>
        {
            var currentContent = cut.FindComponent<GenAIVisualizerDialog>().Instance.Content;
            Assert.NotSame(originalContent, currentContent);
        });
    }

    private IRenderedFragment SetUpDialog(out DashboardDialogService dialogService)
    {
        FluentUISetupHelpers.SetupDialogInfrastructure(this);
        FluentUISetupHelpers.SetupFluentTab(this);
        FluentUISetupHelpers.SetupFluentOverflow(this);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));

        var cut = FluentUISetupHelpers.RenderDialogProvider(this);

        dialogService = Services.GetRequiredService<DashboardDialogService>();
        return cut;
    }
}
