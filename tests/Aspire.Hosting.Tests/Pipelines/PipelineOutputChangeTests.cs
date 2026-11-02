// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Hosting.Pipelines;

#pragma warning disable ASPIREPIPELINES005 // Tests opt into the experimental output diffing API.

namespace Aspire.Hosting.Tests.Pipelines;

public class PipelineOutputChangeTests
{
    [Theory]
    [InlineData("deployment.yaml")]
    [InlineData("generated/deployment.yaml")]
    [InlineData("../custom-coverage.yaml")]
    public void RelativePathsArePreserved(string path)
    {
        var change = new PipelineOutputChange(PipelineOutputChangeKind.Changed, path);

        Assert.Equal(path, change.Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("generated\0file")]
    public void InvalidPathsAreRejected(string path)
    {
        Assert.Throws<ArgumentException>(() => new PipelineOutputChange(PipelineOutputChangeKind.Added, path));
    }

    [Fact]
    public void RootedPathsAreRejected()
    {
        var path = Path.GetFullPath("deployment.yaml");
        Assert.True(Path.IsPathRooted(path));

        Assert.Throws<ArgumentException>(() => new PipelineOutputChange(PipelineOutputChangeKind.Removed, path));
    }

    [Fact]
    public void DiffingExportsOnlyConsumerContracts()
    {
        var types = GetDiffingTypes().Select(type => type.Name).Order(StringComparer.Ordinal);

        Assert.Equal(new[]
        {
            "IPipelineOutputComparer`1", "IPipelineOutputSnapshotter`1", "PipelineOutputChange", "PipelineOutputChangeKind", "PipelineOutputDiffMode",
            "PipelineOutputDiffOptions", "PipelineOutputDiffRule", "PipelineOutputDiffingExtensions"
        }, types);
    }

    [Fact]
    public void DiffingConsumerContractsAreExperimental()
    {
        var members = GetDiffingTypes().Cast<MemberInfo>()
            .Concat(GetDiffingExtensions())
            .ToArray();
        Assert.NotEmpty(members);

        Assert.All(members, member =>
        {
            var attribute = Assert.IsType<ExperimentalAttribute>(Attribute.GetCustomAttribute(member, typeof(ExperimentalAttribute)));
            Assert.Equal("ASPIREPIPELINES005", attribute.DiagnosticId);
            Assert.Equal("https://aka.ms/aspire/diagnostics/{0}", attribute.UrlFormat);
        });
    }

    private static IEnumerable<MethodInfo> GetDiffingExtensions()
    {
        var methods = typeof(PipelineOutputDiffingExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static);
        Assert.Equal(4, methods.Length);
        Assert.Equal(2, methods.Count(method => method.IsGenericMethodDefinition));
        Assert.All(methods, method => Assert.Equal("WithOutputDiffing", method.Name));

        return methods;
    }

    private static IEnumerable<Type> GetDiffingTypes() => typeof(PipelineOutputChange).Assembly.GetExportedTypes()
        .Where(type => type.Namespace == typeof(PipelineOutputChange).Namespace &&
            (type.Name.StartsWith("PipelineOutput", StringComparison.Ordinal) ||
                type.Name.StartsWith("IPipelineOutput", StringComparison.Ordinal)) && type != typeof(IPipelineOutputService));
}

#pragma warning restore ASPIREPIPELINES005
