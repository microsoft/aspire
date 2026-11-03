// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Kubernetes.Tests;

public class KubernetesPersistentVolumeExtensionsTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void WithPersistentVolumeNameRejectsEmptyOrWhitespace(string name)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var volume = builder.AddKubernetesEnvironment("env").AddPersistentVolume("data");

        Assert.Throws<ArgumentException>("persistentVolumeName", () => volume.WithPersistentVolumeName(name));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void WithStorageClassRejectsNonEmptyWhitespace(string name)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var volume = builder.AddKubernetesEnvironment("env").AddPersistentVolume("data");

        Assert.Throws<ArgumentException>("storageClassName", () => volume.WithStorageClass(name));
    }

    [Fact]
    public void ConfigurationMethodsRejectNullArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var volume = builder.AddKubernetesEnvironment("env").AddPersistentVolume("data");

        Assert.Throws<ArgumentNullException>("persistentVolumeName", () => volume.WithPersistentVolumeName(null!));
        Assert.Throws<ArgumentNullException>("storageClassName", () => volume.WithStorageClass((string)null!));
        Assert.Throws<ArgumentNullException>("storageClassName", () => volume.WithStorageClass((IResourceBuilder<ParameterResource>)null!));
        Assert.Throws<ArgumentNullException>("configure", () => volume.WithConfiguration(null!));
        Assert.Throws<ArgumentNullException>("builder", () => KubernetesPersistentVolumeExtensions.WithoutStorageClass(null!));
        Assert.Throws<ArgumentNullException>("builder", () => KubernetesPersistentVolumeExtensions.WithPersistentVolumeName(null!, "existing"));
        Assert.Throws<ArgumentNullException>("builder", () => KubernetesPersistentVolumeExtensions.WithConfiguration(null!, _ => { }));
    }
}
