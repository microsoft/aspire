// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using static Microsoft.CodeAnalysis.Testing.DiagnosticResult;

namespace Aspire.Hosting.Analyzers.Tests;

public class ContainerResourceProjectionAnalyzerTests
{
    [Fact]
    public async Task RunAsContainerImageOnContainerResourceReportsDiagnostic()
    {
        var diagnostic = AppHostAnalyzer.Diagnostics.s_containerResourceCannotBeProjected;

        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            builder.AddContainer("cache", "redis")
                .RunAsContainerImage("contoso/other:1.0");
            """,
            [
                CompilerError(diagnostic.Id)
                    .WithLocation(6, 6)
                    .WithMessage("'ContainerResource' is already a container resource, so 'RunAsContainerImage' cannot be used on it. Configure the container directly instead.")
            ]);

        await test.RunAsync();
    }

    [Fact]
    public async Task PublishAsContainerImageOnContainerResourceReportsDiagnostic()
    {
        var diagnostic = AppHostAnalyzer.Diagnostics.s_containerResourceCannotBeProjected;

        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            builder.AddContainer("cache", "redis")
                .PublishAsContainerImage("contoso/other:1.0");
            """,
            [
                CompilerError(diagnostic.Id)
                    .WithLocation(6, 6)
                    .WithMessage("'ContainerResource' is already a container resource, so 'PublishAsContainerImage' cannot be used on it. Configure the container directly instead.")
            ]);

        await test.RunAsync();
    }

    [Fact]
    public async Task DerivedContainerResourceReportsDiagnostic()
    {
        var diagnostic = AppHostAnalyzer.Diagnostics.s_containerResourceCannotBeProjected;

        // The owner does not have to be ContainerResource itself; anything deriving from it is already a container.
        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            var builder = DistributedApplication.CreateBuilder(args);

            var cache = builder.AddResource(new CustomContainerResource("cache"));
            cache.RunAsContainerImage("contoso/other:1.0");

            public sealed class CustomContainerResource(string name) : ContainerResource(name);
            """,
            [
                CompilerError(diagnostic.Id)
                    .WithLocation(7, 7)
                    .WithMessage("'CustomContainerResource' is already a container resource, so 'RunAsContainerImage' cannot be used on it. Configure the container directly instead.")
            ]);

        await test.RunAsync();
    }

    [Fact]
    public async Task ContainerResourceTypeParameterReportsDiagnostic()
    {
        var diagnostic = AppHostAnalyzer.Diagnostics.s_containerResourceCannotBeProjected;

        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            static void Configure<T>(IResourceBuilder<T> builder)
                where T : ContainerResource
            {
                builder.RunAsContainerImage("contoso/other:1.0");
            }
            """,
            [
                CompilerError(diagnostic.Id)
                    .WithLocation(7, 13)
                    .WithMessage("'T' is already a container resource, so 'RunAsContainerImage' cannot be used on it. Configure the container directly instead.")
            ]);

        await test.RunAsync();
    }

    [Fact]
    public async Task NonContainerResourceReportsNoDiagnostic()
    {
        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            builder.AddExecutable("worker", "worker", ".")
                .PublishAsContainerImage("contoso/worker:1.0");
            """,
            []);

        await test.RunAsync();
    }
}
