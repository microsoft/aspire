// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Maui.Lifecycle;
using Aspire.Hosting.Maui.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Hosting.Maui;

internal static class MauiHostingExtensions
{
    /// <summary>
    /// Registers MAUI-specific lifecycle hooks and services.
    /// </summary>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "Use AddMauiProject() instead.")]
    public static void AddMauiHostingServices(this IDistributedApplicationBuilder builder)
    {
        // Prerequisites must be validated before build queue or device-selection subscribers start
        // work that assumes the local MAUI/Android/Xcode toolchains are usable.
        builder.Services.TryAddEventingSubscriber<MauiPrerequisiteCheckEventSubscriber>();
        builder.Services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IMauiPrerequisiteChecker, MauiWorkloadChecker>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IMauiPrerequisiteChecker, AndroidSdkChecker>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IMauiPrerequisiteChecker, XcodeChecker>());

        // Register the build queue subscriber to serialize builds per-project
        builder.Services.TryAddEventingSubscriber<MauiBuildQueueEventSubscriber>();

        // Register the Android environment variable eventing subscriber
        builder.Services.TryAddEventingSubscriber<MauiAndroidEnvironmentSubscriber>();

        // Register the iOS environment variable eventing subscriber
        builder.Services.TryAddEventingSubscriber<MauiiOSEnvironmentSubscriber>();
    }
}
