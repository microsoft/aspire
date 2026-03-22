// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class ResourceSourceViewModelTests
{
    [Theory]
    [MemberData(nameof(ResourceSourceViewModel_ReturnsCorrectValue_TestData))]
    public void ResourceSourceViewModel_ReturnsCorrectValue(TestData testData, ExpectedData? expected)
    {
        var properties = new Dictionary<string, ResourcePropertyViewModel>();
        AddStringProperty(KnownProperties.Executable.Path, testData.ExecutablePath);
        AddStringProperty(KnownProperties.Project.Path, testData.ProjectPath);
        AddStringProperty(KnownProperties.Container.Image, testData.ContainerImage);
        AddStringProperty(KnownProperties.Resource.Source, testData.SourceProperty);

        if (testData.ExecutableArguments is not null)
        {
            properties.TryAdd(KnownProperties.Executable.Args, new ResourcePropertyViewModel(KnownProperties.Executable.Args, Value.ForList(testData.ExecutableArguments.Select(Value.ForString).ToArray()), false, null, 0));
        }

        if (testData.AppArgs is not null)
        {
            properties.TryAdd(KnownProperties.Resource.AppArgs, new ResourcePropertyViewModel(KnownProperties.Resource.AppArgs, Value.ForList(testData.AppArgs.Select(Value.ForString).ToArray()), false, null, 0));
        }

        if (testData.AppArgsSensitivity is not null)
        {
            properties.TryAdd(KnownProperties.Resource.AppArgsSensitivity, new ResourcePropertyViewModel(KnownProperties.Resource.AppArgsSensitivity, Value.ForList(testData.AppArgsSensitivity.Select(b => Value.ForNumber(Convert.ToInt32(b))).ToArray()), false, null, 0));
        }

        var resource = ModelTestHelpers.CreateResource(
            resourceType: testData.ResourceType,
            properties: properties);

        var actual = ResourceSourceViewModel.GetSourceViewModel(resource);
        if (expected is null)
        {
            Assert.Null(actual);
        }
        else
        {
            Assert.NotNull(actual);
            Assert.Equal(expected.Value, actual.Value);
            Assert.Equal(expected.ValueToVisualize, actual.ValueToVisualize);
            Assert.Equal(expected.Tooltip, actual.Tooltip);

            if (expected.ContentAfterValue is null)
            {
                Assert.Null(actual.ContentAfterValue);
            }
            else
            {
                Assert.NotNull(actual.ContentAfterValue);
                Assert.Equal(expected.ContentAfterValue.Count, actual.ContentAfterValue.Count);
                for (var i = 0; i < expected.ContentAfterValue.Count; i++)
                {
                    Assert.Equal(expected.ContentAfterValue[i].Value, actual.ContentAfterValue[i].Value);
                    Assert.Equal(expected.ContentAfterValue[i].IsShown, actual.ContentAfterValue[i].IsShown);
                }
            }
        }

        void AddStringProperty(string propertyName, string? propertyValue)
        {
            properties.TryAdd(propertyName, new ResourcePropertyViewModel(propertyName, propertyValue is null ? Value.ForNull() : Value.ForString(propertyValue), false, null, 0));
        }
    }

    public static TheoryData<TestData, ExpectedData?> ResourceSourceViewModel_ReturnsCorrectValue_TestData()
    {
        var data = new TheoryData<TestData, ExpectedData?>();

        // Project with app arguments
        data.Add(new TestData(
                ResourceType: "Project",
                ExecutablePath: "path/to/executable",
                ExecutableArguments: ["arg1", "arg2"],
                AppArgs: ["arg2"],
                AppArgsSensitivity: [false],
                ProjectPath: "path/to/project",
                ContainerImage: null,
                SourceProperty: null),
            new ExpectedData(
                Value: "project",
                ContentAfterValue: [new ExpectedLaunchArgument("arg2", true)],
                ValueToVisualize: "path/to/project arg2",
                Tooltip: "path/to/project arg2"));

        var maskingText = DashboardUIHelpers.GetMaskingText(6).Text;
        // Project with app arguments, as well as a secret (format argument)
        data.Add(new TestData(
                ResourceType: "Project",
                ExecutablePath: "path/to/executable",
                ExecutableArguments: ["arg1", "arg2"],
                AppArgs: ["arg2", "--key", "secret", "secret2", "notsecret"],
                AppArgsSensitivity: [false, false, true, true, false],
                ProjectPath: "path/to/project",
                ContainerImage: null,
                SourceProperty: null),
            new ExpectedData(
                Value: "project",
                ContentAfterValue: [new ExpectedLaunchArgument("arg2", true), new ExpectedLaunchArgument("--key", true), new ExpectedLaunchArgument("secret", false), new ExpectedLaunchArgument("secret2", false), new ExpectedLaunchArgument("notsecret", true)],
                ValueToVisualize: "path/to/project arg2 --key secret secret2 notsecret",
                Tooltip: $"path/to/project arg2 --key {maskingText} {maskingText} notsecret"));

        // Project without executable arguments
        data.Add(new TestData(
                ResourceType: "Project",
                ExecutablePath: null,
                ExecutableArguments: null,
                AppArgs: null,
                AppArgsSensitivity: null,
                ProjectPath: "path/to/project",
                ContainerImage: null,
                SourceProperty: null),
            new ExpectedData(
                Value: "project",
                ContentAfterValue: null,
                ValueToVisualize: "path/to/project",
                Tooltip: "path/to/project"));

        // Executable with arguments
        data.Add(new TestData(
                ResourceType: "Executable",
                ExecutablePath: "path/to/executable",
                ExecutableArguments: ["arg1", "arg2"],
                AppArgs: null,
                AppArgsSensitivity: null,
                ProjectPath: null,
                ContainerImage: null,
                SourceProperty: null),
            new ExpectedData(
                Value: "executable",
                ContentAfterValue: [new ExpectedLaunchArgument("arg1", true), new ExpectedLaunchArgument("arg2", true)],
                ValueToVisualize: "path/to/executable arg1 arg2",
                Tooltip: "path/to/executable arg1 arg2"));

        // Container image
        data.Add(new TestData(
                ResourceType: "Container",
                ExecutablePath: null,
                ExecutableArguments: null,
                AppArgs: null,
                AppArgsSensitivity: null,
                ProjectPath: null,
                ContainerImage: "my-container-image",
                SourceProperty: null),
            new ExpectedData(
                Value: "my-container-image",
                ContentAfterValue: null,
                ValueToVisualize: "my-container-image",
                Tooltip: "my-container-image"));

        // Resource source property
        data.Add(new TestData(
                ResourceType: "CustomResourceType",
                ExecutablePath: null,
                ExecutableArguments: null,
                AppArgs: null,
                AppArgsSensitivity: null,
                ProjectPath: null,
                ContainerImage: null,
                SourceProperty: "source-value"),
            new ExpectedData(
                Value: "source-value",
                ContentAfterValue: null,
                ValueToVisualize: "source-value",
                Tooltip: "source-value"));

        // Executable path without arguments
        data.Add(new TestData(
                ResourceType: "Executable",
                ExecutablePath: "path/to/executable",
                ExecutableArguments: null,
                AppArgs: null,
                AppArgsSensitivity: null,
                ProjectPath: null,
                ContainerImage: null,
                SourceProperty: null),
            new ExpectedData(
                Value: "executable",
                ContentAfterValue: null,
                ValueToVisualize: "path/to/executable",
                Tooltip: "path/to/executable"));

        return data;
    }

    public sealed record TestData(
        string ResourceType,
        string? ExecutablePath,
        string[]? ExecutableArguments,
        string[]? AppArgs,
        bool[]? AppArgsSensitivity,
        string? ProjectPath,
        string? ContainerImage,
        string? SourceProperty);

    public sealed record ExpectedLaunchArgument(string Value, bool IsShown);

    public sealed record ExpectedData(
        string Value,
        List<ExpectedLaunchArgument>? ContentAfterValue,
        string ValueToVisualize,
        string Tooltip);
}
