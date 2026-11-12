// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Tests.Pipelines;

[Trait("Partition", "4")]
public class CorePipelineApiTests
{
    [Theory]
    [InlineData(typeof(IDistributedApplicationPipeline))]
    [InlineData(typeof(PipelineStep))]
    [InlineData(typeof(PipelineContext))]
    [InlineData(typeof(PipelineStepContext))]
    [InlineData(typeof(PipelineStepFactoryContext))]
    [InlineData(typeof(PipelineConfigurationContext))]
    [InlineData(typeof(PipelineOptions))]
    [InlineData(typeof(PipelineStepAnnotation))]
    [InlineData(typeof(PipelineConfigurationAnnotation))]
    [InlineData(typeof(PipelineStepFactoryExtensions))]
    [InlineData(typeof(PipelineStepExtensions))]
    [InlineData(typeof(DistributedApplicationPipelineExtensions))]
    [InlineData(typeof(WellKnownPipelineSteps))]
    [InlineData(typeof(WellKnownPipelineTags))]
    [InlineData(typeof(IPipelineActivityReporter))]
    [InlineData(typeof(IReportingStep))]
    [InlineData(typeof(IReportingTask))]
    [InlineData(typeof(NullPublishingActivityReporter))]
    [InlineData(typeof(PublishCompletionOptions))]
    [InlineData(typeof(PublishingExtensions))]
    [InlineData(typeof(PipelineSummary))]
    [InlineData(typeof(PipelineSummaryItem))]
    [InlineData(typeof(MarkdownString))]
    [InlineData(typeof(ReportingStep))]
    [InlineData(typeof(ReportingTask))]
    [InlineData(typeof(NullPublishingStep))]
    [InlineData(typeof(NullPublishingTask))]
    [InlineData(typeof(PipelineEditor))]
    public void CorePipelineType_IsStable(Type type)
    {
        Assert.Null(type.GetCustomAttribute<ExperimentalAttribute>());
    }

    [Fact]
    public void BuilderPipelineProperty_IsStable()
    {
        var property = typeof(IDistributedApplicationBuilder).GetProperty(nameof(IDistributedApplicationBuilder.Pipeline))!;

        Assert.Null(property.GetCustomAttribute<ExperimentalAttribute>());
    }
}
