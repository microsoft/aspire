// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dotnet;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class TestProgramModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TestProgramUsesDotnetProjectsWithAbsoluteFixturePaths(bool includeIntegrationServices)
    {
        using var program = TestProgram.Create<TestProgramModelTests>(includeIntegrationServices: includeIntegrationServices);
        program.Build();

        (IResourceBuilder<DotnetProjectResource> Builder, IProjectMetadata Metadata)[] projects =
        [
            (program.ServiceABuilder, new Projects.ServiceA()),
            (program.ServiceBBuilder, new Projects.ServiceB()),
            (program.ServiceCBuilder, new Projects.ServiceC()),
            (program.WorkerABuilder, new Projects.WorkerA())
        ];

        foreach (var (builder, metadata) in projects)
        {
            var project = Assert.IsType<DotnetProjectResource>(builder.Resource);
            Assert.IsAssignableFrom<IDotnetProgramResource>(project);
            Assert.True(Path.IsPathFullyQualified(project.GetProjectMetadata().ProjectPath));
            Assert.Equal(metadata.ProjectPath, project.GetProjectMetadata().ProjectPath);
        }

        Assert.Equal(
            [program.ServiceABuilder, program.ServiceBBuilder, program.ServiceCBuilder],
            program.ServiceProjectBuilders);

        if (includeIntegrationServices)
        {
            var integrationService = Assert.IsType<DotnetProjectResource>(program.IntegrationServiceABuilder?.Resource);
            Assert.Equal(new Projects.IntegrationServiceA().ProjectPath, integrationService.GetProjectMetadata().ProjectPath);
        }
        else
        {
            Assert.Null(program.IntegrationServiceABuilder);
        }
    }
}
