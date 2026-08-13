// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Extension methods for inspecting whether a resource will be launched by an IDE or extension host
/// for debugging rather than started as a plain process by Aspire.
/// </summary>
[Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class DebugSupportExtensions
{
    /// <summary>
    /// Determines whether the resource will be launched by the IDE for debugging in the current session.
    /// </summary>
    /// <param name="resource">The resource to inspect.</param>
    /// <param name="configuration">The app host configuration, used to detect the active debug session and its capabilities.</param>
    /// <param name="supportsDebuggingAnnotation">When this method returns <see langword="true"/>, the annotation describing how the resource is launched.</param>
    /// <returns><see langword="true"/> when the IDE owns launching this resource; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// <para>
    /// Integrations use this to decide whether to build a runnable command line for the resource. When the
    /// IDE launches the resource, arguments such as <c>dotnet run --project …</c> or <c>go run …</c> are
    /// supplied by the IDE instead and must not be produced by the integration.
    /// </para>
    /// <para>
    /// A resource is only considered debuggable when it carries a <see cref="SupportsDebuggingAnnotation"/>,
    /// a debug session is active, the resource is not forced to process execution, it does not have a
    /// persistent lifetime, and the IDE advertised support for the annotation's launch configuration type.
    /// </para>
    /// <para>
    /// Exception: when the active debug session did not advertise any launch configuration types at all
    /// (for example Visual Studio, which does not send a capability list), a resource whose launch
    /// configuration type is <see cref="KnownLaunchConfigurationTypes.Project"/> is treated as implicitly
    /// supported rather than falling back to plain process execution.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Debug support inspection is a local .NET helper and is not part of the ATS surface.")]
    public static bool SupportsDebugging(this IResource resource, IConfiguration configuration, [NotNullWhen(true)] out SupportsDebuggingAnnotation? supportsDebuggingAnnotation)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(configuration);

        var supportedLaunchConfigurations = GetSupportedLaunchConfigurations(configuration);

        if (!resource.TryGetLastAnnotation(out supportsDebuggingAnnotation)
            || string.IsNullOrEmpty(configuration[DcpExecutor.DebugSessionPortVar])
            || resource.HasAnnotationOfType<ForceProcessExecutionAnnotation>()
            || resource.HasPersistentLifetime())
        {
            return false;
        }

        // When the IDE did not send DEBUG_SESSION_INFO (e.g. Visual Studio), fall back to the
        // legacy rule that "project" launch configuration support is implicit. VS launches all
        // project resources natively without advertising a capability list.
        if (supportedLaunchConfigurations is null)
        {
            return supportsDebuggingAnnotation.LaunchConfigurationType == KnownLaunchConfigurationTypes.Project;
        }

        // The IDE advertised an explicit capability list — honor it for every type, including
        // "project". An IDE that can launch project resources must include "project" in its list
        // (the VS Code extension does this when the C# extension is installed). Treating "project"
        // as implicitly supported here would route resources to an IDE that cannot launch them
        // and leave them stuck.
        return supportedLaunchConfigurations.Contains(supportsDebuggingAnnotation.LaunchConfigurationType);
    }

    /// <summary>
    /// Determines whether the launch configuration performs the resource's tool invocation itself, meaning the
    /// resource's launch tool arguments must not also be passed to the launched program.
    /// </summary>
    /// <param name="resource">The resource to inspect.</param>
    /// <param name="supportsDebuggingAnnotation">The launch configuration annotation to compare.</param>
    /// <returns><see langword="true"/> when the launch configuration supplies the tool invocation; otherwise, <see langword="false"/>.</returns>
    /// <remarks>
    /// This is <see langword="false"/> for a resource whose launch tool arguments declare no owning launch
    /// configuration type, because such a prefix is always passed to the program.
    /// </remarks>
    [AspireExportIgnore(Reason = "Debug support inspection is a local .NET helper and is not part of the ATS surface.")]
    public static bool HasLaunchToolArgsOwnedBy(this IResource resource, SupportsDebuggingAnnotation supportsDebuggingAnnotation)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(supportsDebuggingAnnotation);

        return resource.TryGetLastAnnotation<LaunchToolArgsCallbackAnnotation>(out var launchToolAnnotation)
            && launchToolAnnotation.OwningLaunchConfigurationType is string owner
            && string.Equals(owner, supportsDebuggingAnnotation.LaunchConfigurationType, StringComparison.Ordinal);
    }

    /// <summary>
    /// Creates the launch configuration that this resource sends to the IDE for the given launch mode.
    /// </summary>
    /// <param name="resource">The resource to inspect. It must carry a <see cref="SupportsDebuggingAnnotation"/>.</param>
    /// <param name="mode">The launch mode, one of the values on <see cref="ExecutableLaunchMode"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The launch configuration, typically an <see cref="ExecutableLaunchConfiguration"/>.</returns>
    /// <exception cref="InvalidOperationException">The resource does not declare debug launch support.</exception>
    /// <remarks>
    /// <para>
    /// Launch configuration is created by invoking the producer callback passed to
    /// <see cref="ResourceBuilderExtensions.WithDebugSupport{T, TLaunchConfiguration}(IResourceBuilder{T}, Func{string, TLaunchConfiguration}, string)"/>
    /// (or one of its asynchronous overloads), which owns the complete configuration; Aspire serializes the result as-is.
    /// The configuration is produced fresh on each call.
    /// </para>
    /// <para>
    /// This inspection API does not resolve the resource's environment variables. A producer that accepts a
    /// <see cref="LaunchConfigurationCallbackContext"/> receives an empty
    /// <see cref="LaunchConfigurationCallbackContext.EnvironmentVariables"/> collection. Aspire invokes that producer
    /// separately with resolved values when it creates the executable.
    /// </para>
    /// <para>
    /// This describes the launch configuration itself, not whether one is going to be used. Depending on how the
    /// application is started or how a resource is configured, Aspire may or may not run the resource under a debugger.
    /// Use <see cref="SupportsDebugging"/> to test for that.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Debug support inspection is a local .NET helper and is not part of the ATS surface.")]
    public static Task<object> CreateLaunchConfigurationAsync(
        this IResource resource,
        string mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(mode);

        var context = new LaunchConfigurationCallbackContext(
            mode,
            resource,
            new Dictionary<string, string>(),
            cancellationToken);

        return resource.CreateLaunchConfigurationAsync(context);
    }

    /// <summary>
    /// Creates the launch configuration that this resource sends to the IDE using a callback context.
    /// </summary>
    /// <param name="resource">The resource to inspect. It must carry a <see cref="SupportsDebuggingAnnotation"/>.</param>
    /// <param name="context">The callback context containing the resolved environment and launch data.</param>
    /// <returns>The launch configuration, typically an <see cref="ExecutableLaunchConfiguration"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="context"/> belongs to a different resource.</exception>
    /// <exception cref="InvalidOperationException">The resource does not declare debug launch support.</exception>
    /// <remarks>
    /// <para>
    /// Launch configuration is created by invoking the producer callback passed to
    /// <see cref="ResourceBuilderExtensions.WithDebugSupport{T, TLaunchConfiguration}(IResourceBuilder{T}, Func{LaunchConfigurationCallbackContext, Task{TLaunchConfiguration}}, string)"/>,
    /// which owns the complete configuration; Aspire serializes the result as-is.
    /// </para>
    /// <para>
    /// This method never resolves environment variables. Aspire creates <paramref name="context"/>
    /// when the active debug-support annotation is producing a launch configuration for an executable creation.
    /// </para>
    /// <para>
    /// This overload is internal because only Aspire constructs callback contexts containing resolved environment
    /// variables. Use the public overload when inspecting a launch configuration outside executable creation.
    /// </para>
    /// </remarks>
    internal static Task<object> CreateLaunchConfigurationAsync(
        this IResource resource,
        LaunchConfigurationCallbackContext context)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(context);

        if (!ReferenceEquals(resource, context.Resource))
        {
            throw new ArgumentException(
                $"The launch configuration callback context belongs to resource '{context.Resource.Name}', " +
                $"but launch configuration was requested for resource '{resource.Name}'.",
                nameof(context));
        }

        if (!resource.TryGetLastAnnotation<SupportsDebuggingAnnotation>(out var supportsDebuggingAnnotation))
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' does not declare debug launch support. " +
                $"Call {nameof(ResourceBuilderExtensions.WithDebugSupport)} on the resource first. " +
                $"Note that it only adds the annotation in run mode.");
        }

        return supportsDebuggingAnnotation.LaunchConfigurationProducer(context);
    }

    private static string[]? GetSupportedLaunchConfigurations(IConfiguration configuration)
    {
        try
        {
            if (configuration[KnownConfigNames.DebugSessionInfo] is { } debugSessionInfoJson && JsonSerializer.Deserialize<RunSessionInfo>(debugSessionInfoJson) is { } debugSessionInfo)
            {
                return debugSessionInfo.SupportedLaunchConfigurations;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
