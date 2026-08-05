// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Proto.Common.V1;

namespace Aspire.Dashboard.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[Config(typeof(Config))]
public class SpanFilteringBenchmarks
{
    private static readonly FieldTelemetryFilter[] s_noFilters = [];
    private static readonly Func<OtlpResourceView, string> s_getResourceName = static source => source.Resource.ResourceName;

    private readonly FieldTelemetryFilter[] _durationFilters =
    [
        new()
        {
            Field = KnownTraceFields.DurationField,
            Condition = FilterCondition.GreaterThanOrEqual,
            Value = "50"
        }
    ];

    private List<SpanWaterfallViewModel> _balancedTree = [];
    private List<SpanWaterfallViewModel> _collapsedTree = [];
    private List<SpanWaterfallViewModel> _deepChain = [];

    [GlobalSetup]
    public void Setup()
    {
        _balancedTree = CreateBalancedTree(branchingFactor: 4, depth: 7);
        _collapsedTree = CreateBalancedTree(branchingFactor: 4, depth: 7, collapseEveryNthDepth: 3);
        _deepChain = CreateDeepChain(count: 10_000);
    }

    [Benchmark(Description = "Trace detail: no filters")]
    public int NoFilters()
    {
        return Count(TraceDetail.TraceDetailPageViewModel.ApplySpanFilters(_balancedTree, filter: string.Empty, typeFilter: null, s_noFilters, s_getResourceName));
    }

    [Benchmark(Description = "Trace detail: duration >= 50ms")]
    public int DurationOnly()
    {
        return Count(TraceDetail.TraceDetailPageViewModel.ApplySpanFilters(_balancedTree, filter: string.Empty, typeFilter: null, _durationFilters, s_getResourceName));
    }

    [Benchmark(Description = "Trace detail: no-match text filter")]
    public int ContextFilterNoMatchDeepChain()
    {
        return Count(TraceDetail.TraceDetailPageViewModel.ApplySpanFilters(_deepChain, filter: "missing-span-name", typeFilter: null, s_noFilters, s_getResourceName));
    }

    [Benchmark(Description = "Trace detail: root text filter")]
    public int ContextFilterRootMatch()
    {
        return Count(TraceDetail.TraceDetailPageViewModel.ApplySpanFilters(_balancedTree, filter: "root-span", typeFilter: null, s_noFilters, s_getResourceName));
    }

    [Benchmark(Description = "Trace detail: hidden descendants")]
    public int ContextFilterCollapsedTree()
    {
        return Count(TraceDetail.TraceDetailPageViewModel.ApplySpanFilters(_collapsedTree, filter: "leaf-match", typeFilter: null, s_noFilters, s_getResourceName));
    }

    private static List<SpanWaterfallViewModel> CreateDeepChain(int count)
    {
        var factory = new SpanFactory();
        var spans = new List<SpanWaterfallViewModel>(count);
        SpanWaterfallViewModel? parent = null;

        for (var i = 0; i < count; i++)
        {
            var span = factory.CreateViewModel(
                spanId: $"chain-{i}",
                name: i == 0 ? "root-span" : $"chain-span-{i}",
                parentSpanId: parent?.Span.SpanId,
                durationMilliseconds: i % 10 == 0 ? 100 : 5,
                depth: i + 1);

            parent?.Children.Add(span);
            spans.Add(span);
            parent = span;
        }

        return spans;
    }

    private static List<SpanWaterfallViewModel> CreateBalancedTree(int branchingFactor, int depth, int? collapseEveryNthDepth = null)
    {
        var factory = new SpanFactory();
        var spans = new List<SpanWaterfallViewModel>();
        var root = factory.CreateViewModel(
            spanId: "balanced-0",
            name: "root-span",
            parentSpanId: null,
            durationMilliseconds: 100,
            depth: 1);

        spans.Add(root);

        AddChildren(root);

        return spans;

        void AddChildren(SpanWaterfallViewModel parent)
        {
            if (parent.Depth >= depth)
            {
                return;
            }

            for (var i = 0; i < branchingFactor; i++)
            {
                var index = spans.Count;
                var childDepth = parent.Depth + 1;
                var child = factory.CreateViewModel(
                    spanId: $"balanced-{index}",
                    name: childDepth == depth && i == branchingFactor - 1 ? "leaf-match" : $"balanced-span-{index}",
                    parentSpanId: parent.Span.SpanId,
                    durationMilliseconds: index % 5 == 0 ? 100 : 5,
                    depth: childDepth);

                parent.Children.Add(child);
                spans.Add(child);
                AddChildren(child);
            }

            if (collapseEveryNthDepth is { } collapsedDepth && parent.Depth % collapsedDepth == 0)
            {
                // Apply collapsed state after descendants exist so IsHidden cascades
                // the same way it does when users collapse a populated trace tree.
                parent.IsCollapsed = true;
            }
        }
    }

    private static int Count(IEnumerable<SpanWaterfallViewModel> spans)
    {
        var count = 0;
        foreach (var _ in spans)
        {
            count++;
        }

        return count;
    }

    private sealed class SpanFactory
    {
        private readonly OtlpResourceView _resourceView;
        private readonly OtlpScope _scope = OtlpScope.Empty;
        private readonly DateTime _startTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private readonly OtlpTrace _trace = new(new byte[] { 0x1 }, DateTime.MinValue);

        public SpanFactory()
        {
            var context = new OtlpContext
            {
                Logger = NullLogger.Instance,
                Options = new TelemetryLimitOptions()
            };
            var resource = new OtlpResource("benchmark-app", instanceId: null, uninstrumentedPeer: false, context);
            _resourceView = new OtlpResourceView(resource, new RepeatedField<KeyValue>());
        }

        public SpanWaterfallViewModel CreateViewModel(string spanId, string name, string? parentSpanId, double durationMilliseconds, int depth)
        {
            var startTime = _startTime.AddTicks(depth);
            var span = new OtlpSpan(_resourceView, _trace, _scope)
            {
                SpanId = spanId,
                ParentSpanId = parentSpanId,
                Name = name,
                Kind = OtlpSpanKind.Server,
                StartTime = startTime,
                EndTime = startTime.AddMilliseconds(durationMilliseconds),
                Status = OtlpSpanStatusCode.Unset,
                StatusMessage = null,
                State = null,
                Attributes = [],
                Events = [],
                Links = [],
                BackLinks = []
            };

            return new SpanWaterfallViewModel
            {
                Children = [],
                Span = span,
                LeftOffset = 0,
                Width = durationMilliseconds,
                Depth = depth,
                LabelIsRight = true,
                UninstrumentedPeer = null,
                SpanLogs = []
            };
        }
    }

    private sealed class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithWarmupCount(2)
                .WithIterationCount(5)
                .WithInvocationCount(16)
                .WithUnrollFactor(1));

            AddDiagnoser(MemoryDiagnoser.Default);
        }
    }
}
