// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Aspire.Hosting.Analyzers.Tests;

public class AuthorshipAnnotationAnalyzerTests
{
    [Fact]
    public async Task RunAsEmulatorIsAnnotatedWithSebJustBuiltThis()
    {
        var test = AnalyzerTest.Create<AuthorshipAnnotationAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            var eventHubs = builder.AddAzureEventHubs("eventhubs");
            eventHubs.RunAsEmulator();
            """,
            [new DiagnosticResult(AuthorshipAnnotationAnalyzer.s_sebJustBuiltThis).WithLocation(6, 1)]);

        await test.RunAsync();
    }

    [Fact]
    public async Task UnannotatedEventHubsApiIsNotReported()
    {
        var test = AnalyzerTest.Create<AuthorshipAnnotationAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            var eventHubs = builder.AddAzureEventHubs("eventhubs");
            eventHubs.AddHub("hub");
            """,
            []);

        await test.RunAsync();
    }

    [Fact]
    public async Task SameNamedMethodOnAnotherTypeIsNotReported()
    {
        var test = AnalyzerTest.Create<AuthorshipAnnotationAnalyzer>("""
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            var builder = DistributedApplication.CreateBuilder(args);

            var eventHubs = builder.AddAzureEventHubs("eventhubs");
            Other.RunAsEmulator(eventHubs);

            public static class Other
            {
                public static IResourceBuilder<T> RunAsEmulator<T>(IResourceBuilder<T> builder) where T : IResource => builder;
            }
            """,
            []);

        await test.RunAsync();
    }
}
