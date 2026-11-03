// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests.PublicApiTests;

public class SignalRPublicApiTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void CtorAzureSignalREmulatorResourceShouldThrowWhenInnerResourceIsNull()
    {
        AzureSignalRResource innerResource = null!;

        var action = () => new AzureSignalREmulatorResource(innerResource);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(innerResource), exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AddAzureSignalRShouldThrowWhenBuilderIsNull(int overrideIndex)
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "emulator";
        var serviceMode = new AzureSignalRServiceMode();

        Action action = overrideIndex switch
        {
            0 => () => builder.AddAzureSignalR(name),
            1 => () => builder.AddAzureSignalR(name, serviceMode),
            _ => throw new InvalidOperationException()
        };

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public void AddAzureSignalRShouldThrowWhenBuilderIsNullOrEmpty(int overrideIndex, bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var name = isNull ? null! : string.Empty;
        var serviceMode = new AzureSignalRServiceMode();

        Action action = overrideIndex switch
        {
            0 => () => builder.AddAzureSignalR(name),
            1 => () => builder.AddAzureSignalR(name, serviceMode),
            _ => throw new InvalidOperationException()
        };

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void RunAsEmulatorShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureSignalRResource> builder = null!;

        var action = () => builder.RunAsEmulator();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureSignalRResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        Action<AzureResourceInfrastructure> configureInfrastructure = (_) => { };

        var action = () => new AzureSignalRResource(name, configureInfrastructure);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void CtorAzureSignalRResourceShouldThrowWhenConfigureInfrastructureIsNull()
    {
        const string name = "signal-r";
        Action<AzureResourceInfrastructure> configureInfrastructure = null!;

        var action = () => new AzureSignalRResource(name, configureInfrastructure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configureInfrastructure), exception.ParamName);
    }
}
