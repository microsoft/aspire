// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding required command annotations to resources.
/// </summary>
public static class RequiredCommandResourceExtensions
{
    /// <summary>
    /// Declares that a resource requires a specific command/executable to be available on the local machine PATH before it can start.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="command">The command string (file name or path) that should be validated.</param>
    /// <param name="helpLink">An optional help link URL to guide users when the command is missing.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// The command is considered valid if either:
    /// 1. It is an absolute or relative path (contains a directory separator) that points to an existing file, or
    /// 2. It is discoverable on the current process PATH (respecting PATHEXT on Windows).
    /// If the command is not found, a warning message will be logged but the resource will be allowed to attempt to start.
    /// </remarks>
    [AspireExport(Description = "Adds a required command dependency")]
    public static IResourceBuilder<T> WithRequiredCommand<T>(
        this IResourceBuilder<T> builder,
        string command,
        string? helpLink = null) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(command);

#pragma warning disable ASPIRECOMMAND001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        builder.WithAnnotation(new RequiredCommandAnnotation(command)
        {
            HelpLink = helpLink
        });
#pragma warning restore ASPIRECOMMAND001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        return builder;
    }

    /// <summary>
    /// Declares that a resource requires a specific command/executable to be available on the local machine PATH before it can start,
    /// with custom validation logic.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="command">The command string (file name or path) that should be validated.</param>
    /// <param name="validationCallback">A callback that validates the resolved command path. Receives a <see cref="RequiredCommandValidationContext"/> and returns a <see cref="RequiredCommandValidationResult"/>.</param>
    /// <param name="helpLink">An optional help link URL to guide users when the command is missing or fails validation.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// <para>This method is not available in polyglot app hosts. Use the overload without validation callback instead.</para>
    /// The command is first resolved to a full path. If found, the validation callback is invoked with the context containing the resolved path and service provider.
    /// The callback should return a <see cref="RequiredCommandValidationResult"/> indicating whether the command is valid.
    /// If the command is not found or fails validation, a warning message will be logged but the resource will be allowed to attempt to start.
    /// </remarks>
    [Experimental("ASPIRECOMMAND001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "RequiredCommandValidationContext exposes IServiceProvider — not usable from polyglot hosts.")]
    public static IResourceBuilder<T> WithRequiredCommand<T>(
        this IResourceBuilder<T> builder,
        string command,
        Func<RequiredCommandValidationContext, Task<RequiredCommandValidationResult>> validationCallback,
        string? helpLink = null) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(validationCallback);

        builder.WithAnnotation(new RequiredCommandAnnotation(command)
        {
            ValidationCallback = validationCallback,
            HelpLink = helpLink
        });

        return builder;
    }
}
