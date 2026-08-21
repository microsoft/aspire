// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Maui;
using Aspire.Hosting.Maui.Annotations;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for customizing the build and launch arguments of MAUI platform resources.
/// </summary>
public static class MauiBuildArgumentsExtensions
{
    /// <summary>
    /// Registers a callback that can inspect or modify the arguments used for the serialized
    /// compile (<c>dotnet build</c>) that runs before the app is launched.
    /// </summary>
    /// <typeparam name="T">The type of the MAUI platform resource.</typeparam>
    /// <param name="builder">The MAUI platform resource builder.</param>
    /// <param name="callback">
    /// A callback invoked with a <see cref="MauiBuildArgumentsCallbackContext"/> whose
    /// <see cref="MauiBuildArgumentsCallbackContext.Arguments"/> can be mutated to influence the
    /// <c>dotnet build</c> command. The arguments contain the full command (verb, project path,
    /// target framework, configuration, and any additional MSBuild properties).
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Multiple callbacks can be registered; they are invoked in registration order and share the
    /// same mutable argument list.
    /// </remarks>
    /// <example>
    /// Add an MSBuild property to the compile:
    /// <code lang="csharp">
    /// maui.AddAndroidEmulator("emulator")
    ///     .WithMauiBuildArguments(context => context.Arguments.Add("-p:MyProperty=Value"));
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<T> WithMauiBuildArguments<T>(
        this IResourceBuilder<T> builder,
        Func<MauiBuildArgumentsCallbackContext, Task> callback)
        where T : IMauiPlatformResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithAnnotation(new MauiBuildArgumentsCallbackAnnotation(MauiBuildStep.Build, callback));
    }

    /// <summary>
    /// Registers a synchronous callback that can inspect or modify the arguments used for the
    /// serialized compile (<c>dotnet build</c>) that runs before the app is launched.
    /// </summary>
    /// <typeparam name="T">The type of the MAUI platform resource.</typeparam>
    /// <param name="builder">The MAUI platform resource builder.</param>
    /// <param name="callback">
    /// A callback invoked with a <see cref="MauiBuildArgumentsCallbackContext"/> whose
    /// <see cref="MauiBuildArgumentsCallbackContext.Arguments"/> can be mutated to influence the
    /// <c>dotnet build</c> command.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Multiple callbacks can be registered; they are invoked in registration order and share the
    /// same mutable argument list.
    /// </remarks>
    /// <example>
    /// Add an MSBuild property to the compile:
    /// <code lang="csharp">
    /// maui.AddAndroidEmulator("emulator")
    ///     .WithMauiBuildArguments(context => context.Arguments.Add("-p:MyProperty=Value"));
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Convenience overload. Use the asynchronous overload instead.")]
    public static IResourceBuilder<T> WithMauiBuildArguments<T>(
        this IResourceBuilder<T> builder,
        Action<MauiBuildArgumentsCallbackContext> callback)
        where T : IMauiPlatformResource
    {
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithMauiBuildArguments(context =>
        {
            callback(context);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Registers a callback that can inspect or modify the arguments used for the launch command
    /// that starts the already-built app (<c>dotnet build --no-restore /t:Run -p:NoBuild=true</c>).
    /// </summary>
    /// <typeparam name="T">The type of the MAUI platform resource.</typeparam>
    /// <param name="builder">The MAUI platform resource builder.</param>
    /// <param name="callback">
    /// A callback invoked with a <see cref="MauiBuildArgumentsCallbackContext"/> whose
    /// <see cref="MauiBuildArgumentsCallbackContext.Arguments"/> can be mutated to influence the
    /// launch command. The arguments contain the verb and options that replace DCP's default
    /// <c>run</c> verb; the project path and <c>--configuration</c> are appended by the host.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Multiple callbacks can be registered; they are invoked in registration order and share the
    /// same mutable argument list. Launch callbacks are applied once before the app starts (the launch
    /// command is rendered ahead of the first start), so edits do not accumulate across restarts.
    /// </remarks>
    /// <example>
    /// Add an MSBuild property to the launch command:
    /// <code lang="csharp">
    /// maui.AddAndroidEmulator("emulator")
    ///     .WithMauiLaunchArguments(context => context.Arguments.Add("-p:MyProperty=Value"));
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<T> WithMauiLaunchArguments<T>(
        this IResourceBuilder<T> builder,
        Func<MauiBuildArgumentsCallbackContext, Task> callback)
        where T : IMauiPlatformResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithAnnotation(new MauiBuildArgumentsCallbackAnnotation(MauiBuildStep.Launch, callback));
    }

    /// <summary>
    /// Registers a synchronous callback that can inspect or modify the arguments used for the launch
    /// command that starts the already-built app (<c>dotnet build --no-restore /t:Run -p:NoBuild=true</c>).
    /// </summary>
    /// <typeparam name="T">The type of the MAUI platform resource.</typeparam>
    /// <param name="builder">The MAUI platform resource builder.</param>
    /// <param name="callback">
    /// A callback invoked with a <see cref="MauiBuildArgumentsCallbackContext"/> whose
    /// <see cref="MauiBuildArgumentsCallbackContext.Arguments"/> can be mutated to influence the
    /// launch command.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Multiple callbacks can be registered; they are invoked in registration order and share the
    /// same mutable argument list. Launch callbacks are applied once before the app starts (the launch
    /// command is rendered ahead of the first start), so edits do not accumulate across restarts.
    /// </remarks>
    /// <example>
    /// Add an MSBuild property to the launch command:
    /// <code lang="csharp">
    /// maui.AddAndroidEmulator("emulator")
    ///     .WithMauiLaunchArguments(context => context.Arguments.Add("-p:MyProperty=Value"));
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Convenience overload. Use the asynchronous overload instead.")]
    public static IResourceBuilder<T> WithMauiLaunchArguments<T>(
        this IResourceBuilder<T> builder,
        Action<MauiBuildArgumentsCallbackContext> callback)
        where T : IMauiPlatformResource
    {
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithMauiLaunchArguments(context =>
        {
            callback(context);
            return Task.CompletedTask;
        });
    }
}
