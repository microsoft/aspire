// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

/// <summary>
/// Tests for iOS device/simulator ID cross-validation.
/// When a GUID is passed to AddiOSDevice, or a non-GUID to AddiOSSimulator, 
/// BeforeResourceStartedEvent should throw with guidance to use the correct method.
/// </summary>
public class MauiiOSValidationTests
{
    [Fact]
    public async Task AddiOSDevice_WithGuidDeviceId_ThrowsOnBeforeStart()
    {
        // A GUID looks like a simulator UDID, not a physical device UDID
        var simulatorLikeId = "E25BBE37-69BA-4720-B6FD-D54C97791E79";
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-ios");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var device = maui.AddiOSDevice("my-device", simulatorLikeId);

            await using var app = appBuilder.Build();

            var exception = await Assert.ThrowsAsync<DistributedApplicationException>(async () =>
            {
                await app.Services.GetRequiredService<IDistributedApplicationEventing>()
                    .PublishAsync(new BeforeResourceStartedEvent(device.Resource, app.Services), CancellationToken.None);
            });

            Assert.Contains("appears to be an iOS Simulator UDID", exception.Message);
            Assert.Contains("AddiOSSimulator", exception.Message);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task AddiOSSimulator_WithNonGuidSimulatorId_ThrowsOnBeforeStart()
    {
        // A non-GUID looks like a physical device UDID, not a simulator UDID
        var deviceLikeId = "00008030-001234567890123A";
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-ios");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var simulator = maui.AddiOSSimulator("my-simulator", deviceLikeId);

            await using var app = appBuilder.Build();

            var exception = await Assert.ThrowsAsync<DistributedApplicationException>(async () =>
            {
                await app.Services.GetRequiredService<IDistributedApplicationEventing>()
                    .PublishAsync(new BeforeResourceStartedEvent(simulator.Resource, app.Services), CancellationToken.None);
            });

            Assert.Contains("does not appear to be an iOS Simulator UDID", exception.Message);
            Assert.Contains("AddiOSDevice", exception.Message);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task AddiOSDevice_WithValidDeviceId_DoesNotThrowOnBeforeStart()
    {
        // A typical physical device UDID (non-GUID format)
        var validDeviceId = "00008030-001234567890123A";
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-ios");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var device = maui.AddiOSDevice("my-device", validDeviceId);

            await using var app = appBuilder.Build();

            // Should not throw — this is a valid device UDID format
            await app.Services.GetRequiredService<IDistributedApplicationEventing>()
                .PublishAsync(new BeforeResourceStartedEvent(device.Resource, app.Services), CancellationToken.None);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task AddiOSSimulator_WithValidGuidSimulatorId_DoesNotThrowOnBeforeStart()
    {
        // A standard GUID format which is expected for simulator UDIDs
        var validSimulatorId = "E25BBE37-69BA-4720-B6FD-D54C97791E79";
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-ios");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var simulator = maui.AddiOSSimulator("my-simulator", validSimulatorId);

            await using var app = appBuilder.Build();

            // Should not throw — this is a valid simulator UDID format
            await app.Services.GetRequiredService<IDistributedApplicationEventing>()
                .PublishAsync(new BeforeResourceStartedEvent(simulator.Resource, app.Services), CancellationToken.None);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task AddiOSDevice_WithNoDeviceId_DoesNotThrowOnBeforeStart()
    {
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-ios");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var device = maui.AddiOSDevice();

            await using var app = appBuilder.Build();

            // No device ID validation when no ID is provided
            await app.Services.GetRequiredService<IDistributedApplicationEventing>()
                .PublishAsync(new BeforeResourceStartedEvent(device.Resource, app.Services), CancellationToken.None);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task AddiOSSimulator_WithNoSimulatorId_DoesNotThrowOnBeforeStart()
    {
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-ios");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var simulator = maui.AddiOSSimulator();

            await using var app = appBuilder.Build();

            // No simulator ID validation when no ID is provided
            await app.Services.GetRequiredService<IDistributedApplicationEventing>()
                .PublishAsync(new BeforeResourceStartedEvent(simulator.Resource, app.Services), CancellationToken.None);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }
}
