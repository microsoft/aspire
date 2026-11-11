// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CodeDom.Compiler;
using Xunit;

namespace Aspire.Hosting.Testing.Tests;

public class DistributedApplicationEntryPointInvokerTests
{
    [Fact]
    public void ResolveEntryPointThrowsForGeneratedProjectMetadataType()
    {
        var entryPointType = typeof(GeneratedAppHostProjectMetadata);
        IProjectMetadata projectMetadata = new GeneratedAppHostProjectMetadata();
        var assembly = entryPointType.Assembly;

        var exception = Assert.Throws<InvalidOperationException>(
            () => DistributedApplicationEntryPointInvoker.ResolveEntryPoint(entryPointType));

        Assert.Equal(
            $"The specified entry point type '{entryPointType.FullName}' is generated project metadata for '{projectMetadata.ProjectPath}', " +
            $"but it was resolved from the Microsoft.Testing.Platform test application '{assembly.GetName().Name}'. " +
            "This can happen when an Aspire.AppHost.Sdk test project generates a Projects.* type that shadows the AppHost's generated marker. " +
            "Test projects should use Microsoft.NET.Sdk instead of Aspire.AppHost.Sdk and reference the AppHost project so the entry point type belongs to the AppHost executable assembly. " +
            $"If the AppHost is discovered dynamically, load its assembly and pass a type from that assembly to {nameof(DistributedApplicationTestingBuilder)}.{nameof(DistributedApplicationTestingBuilder.CreateAsync)}(Type).",
            exception.Message);
    }

    [Fact]
    public void ResolveEntryPointThrowsForMicrosoftTestingPlatformApplication()
    {
        var entryPointType = typeof(DistributedApplicationEntryPointInvokerTests);
        var assembly = entryPointType.Assembly;

        var exception = Assert.Throws<InvalidOperationException>(
            () => DistributedApplicationEntryPointInvoker.ResolveEntryPoint(entryPointType));

        Assert.Equal(
            $"The assembly '{assembly.GetName().Name}' is a Microsoft.Testing.Platform test application. " +
            $"Invoking its entry point from {nameof(DistributedApplicationFactory)} would recursively run the test application. " +
            "Test projects should use Microsoft.NET.Sdk instead of Aspire.AppHost.Sdk and reference the AppHost project so the entry point type belongs to the AppHost executable assembly. " +
            "Alternatively, use " +
            $"{nameof(DistributedApplicationTestingBuilder)}.{nameof(DistributedApplicationTestingBuilder.Create)} to construct the application without invoking an entry point.",
            exception.Message);
    }

    [Fact]
    public void ResolveEntryPointAcceptsAppHostApplication()
    {
        var entryPoint = DistributedApplicationEntryPointInvoker.ResolveEntryPoint(
            typeof(Projects.TestingAppHost1_AppHost));

        Assert.NotNull(entryPoint);
    }

    [Fact]
    public void ResolveEntryPointAcceptsProjectMetadataTypeFromAppHostApplication()
    {
        var entryPoint = DistributedApplicationEntryPointInvoker.ResolveEntryPoint(
            typeof(Projects.TestingAppHost1_MyWorker));

        Assert.NotNull(entryPoint);
    }

    [GeneratedCode("Aspire.Hosting", null)]
    private sealed class GeneratedAppHostProjectMetadata : IProjectMetadata
    {
        public string ProjectPath => "/path/to/Test.AppHost.csproj";
    }
}
