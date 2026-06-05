// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Tests.Shared.DashboardModel;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class ExportHelpersTests
{
    [Fact]
    public void GetResourceAsJson_ReturnsExpectedJson()
    {
        // Arrange
        var resource = ModelTestHelpers.CreateResource(
            resourceName: "test-resource",
            displayName: "Test Resource",
            resourceType: "Container",
            state: KnownResourceState.Running,
            urls: [new UrlViewModel("http", new Uri("http://localhost:5000"), isInternal: false, isInactive: false, UrlDisplayPropertiesViewModel.Empty)],
            environment: [new EnvironmentVariableViewModel("MY_VAR", "my-value", fromSpec: false)],
            relationships: [new RelationshipViewModel("dependency", "Reference")]);

        var resourceByName = new Dictionary<string, ResourceViewModel>(StringComparer.OrdinalIgnoreCase) { [resource.Name] = resource };

        // Act
        var result = ExportHelpers.GetResourceAsJson(resource, resourceByName);

        // Assert
        Assert.Equal("Test Resource.json", result.FileName);
        Assert.NotNull(result.Content);
    }

    [Fact]
    public void GetEnvironmentVariablesAsEnvFile_ReturnsExpectedResult()
    {
        // Arrange
        var resource = ModelTestHelpers.CreateResource(
            resourceName: "test-resource",
            displayName: "Test Resource",
            resourceType: "Container",
            state: KnownResourceState.Running,
            environment: [
                new EnvironmentVariableViewModel("MY_VAR", "my-value", fromSpec: true),
                new EnvironmentVariableViewModel("RUNTIME_VAR", "runtime-value", fromSpec: false)
            ]);

        var resourceByName = new Dictionary<string, ResourceViewModel>(StringComparer.OrdinalIgnoreCase) { [resource.Name] = resource };

        // Act
        var result = ExportHelpers.GetEnvironmentVariablesAsEnvFile(resource, resourceByName);

        // Assert
        Assert.Equal("Test Resource.env", result.FileName);
        Assert.Equal(
            """
            MY_VAR=my-value

            """,
            result.Content,
            ignoreLineEndingDifferences: true);
    }
}
