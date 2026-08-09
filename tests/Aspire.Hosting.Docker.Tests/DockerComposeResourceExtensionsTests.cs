// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Docker.Resources;
using Aspire.Hosting.Docker.Resources.ComposeNodes;

namespace Aspire.Hosting.Docker.Tests;

public class DockerComposeResourceExtensionsTests
{
    [Fact]
    public void AddNetworkAddsTopLevelComposeNetwork()
    {
        var composeFile = new ComposeFile();

        var result = composeFile.AddNetwork("validation-network", driver: "bridge", external: true, network =>
        {
            network.Labels["purpose"] = "validation";
        });

        Assert.Same(composeFile, result);
        var network = Assert.Single(composeFile.Networks).Value;
        Assert.Equal("validation-network", network.Name);
        Assert.Equal("bridge", network.Driver);
        Assert.True(network.External);
        Assert.Equal("validation", network.Labels["purpose"]);
    }

    [Fact]
    public void AddServiceAddsComposeService()
    {
        var composeFile = new ComposeFile();

        var result = composeFile.AddService("worker", image: "busybox", service =>
        {
            service.Command.Add("sleep");
        });

        Assert.Same(composeFile, result);
        var service = Assert.Single(composeFile.Services).Value;
        Assert.Equal("worker", service.Name);
        Assert.Equal("busybox", service.Image);
        Assert.Equal("sleep", Assert.Single(service.Command));
    }

    [Fact]
    public void AddVolumeAddsTopLevelComposeVolume()
    {
        var composeFile = new ComposeFile();

        var result = composeFile.AddVolume("validation-data", driver: "local", external: true, volume =>
        {
            volume.DriverOpts["type"] = "none";
            volume.Labels["purpose"] = "validation";
        });

        Assert.Same(composeFile, result);
        var volume = Assert.Single(composeFile.Volumes).Value;
        Assert.Equal("validation-data", volume.Name);
        Assert.Equal("local", volume.Driver);
        Assert.True(volume.External);
        Assert.Equal("none", volume.DriverOpts["type"]);
        Assert.Equal("validation", volume.Labels["purpose"]);
    }

    [Fact]
    public void AddConfigAddsTopLevelComposeConfig()
    {
        var composeFile = new ComposeFile();

        var result = composeFile.AddConfig("validation-config", file: "./app.conf", content: "enabled=true", external: true, config =>
        {
            config.Labels["purpose"] = "validation";
        });

        Assert.Same(composeFile, result);
        var config = Assert.Single(composeFile.Configs).Value;
        Assert.Equal("validation-config", config.Name);
        Assert.Equal("./app.conf", config.File);
        Assert.Equal("enabled=true", config.Content);
        Assert.True(config.External);
        Assert.Equal("validation", config.Labels["purpose"]);
    }

    [Fact]
    public void AddSecretAddsTopLevelComposeSecret()
    {
        var composeFile = new ComposeFile();

        var result = composeFile.AddSecret("validation-secret", file: "./secret.txt", external: true, secret =>
        {
            secret.Labels["purpose"] = "validation";
        });

        Assert.Same(composeFile, result);
        var secret = Assert.Single(composeFile.Secrets).Value;
        Assert.Equal("validation-secret", secret.Name);
        Assert.Equal("./secret.txt", secret.File);
        Assert.True(secret.External);
        Assert.Equal("validation", secret.Labels["purpose"]);
    }

    [Fact]
    public void AddVolumeAddsServiceVolumeMount()
    {
        var service = new Service { Name = "api" };

        var result = service.AddVolume("validation-data", "/container/compose-data", type: "bind", isReadOnly: true, volume =>
        {
            volume.Labels["purpose"] = "validation";
        });

        Assert.Same(service, result);
        var volume = Assert.Single(service.Volumes);
        Assert.Equal("validation-data", volume.Name);
        Assert.Equal("validation-data", volume.Source);
        Assert.Equal("/container/compose-data", volume.Target);
        Assert.Equal("bind", volume.Type);
        Assert.True(volume.ReadOnly);
        Assert.Equal("validation", volume.Labels["purpose"]);
    }

}
