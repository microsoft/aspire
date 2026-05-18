// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript.Internal;

/// <summary>
/// Lightweight "the user called WithWorkspaceRoot" intent annotation.
/// Carries only the resolved workspace root path; no I/O or filesystem
/// validation has been performed yet.
/// </summary>
/// <remarks>
/// This is written eagerly when <c>WithWorkspaceRoot</c> is called. It allows
/// downstream API-time consumers (the package-manager <c>WithNpm</c> /
/// <c>WithYarn</c> / <c>WithPnpm</c> / <c>WithBun</c> methods) to know that
/// the resource opted into workspace mode, without forcing the AppHost
/// constructor to do any disk reads or fail with "your repo is misconfigured"
/// errors.
/// <para/>
/// The fully validated, computed counterpart is <see cref="JavaScriptWorkspaceAnnotation"/>,
/// which is written by <see cref="Workspace.WorkspaceConfigurationValidator"/>
/// during publish-mode Dockerfile generation or run-mode installer startup.
/// </remarks>
internal sealed class JavaScriptWorkspaceConfigAnnotation : IResourceAnnotation
{
    public JavaScriptWorkspaceConfigAnnotation(string rootPath, string appDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        ArgumentException.ThrowIfNullOrEmpty(appDirectory);
        RootPath = rootPath;
        AppDirectory = appDirectory;
    }

    /// <summary>
    /// Absolute, normalized path to the workspace root the user requested.
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// Absolute, normalized path to the application directory captured at API time.
    /// Stored here so the validator can compute relative paths even after the
    /// resource has been wrapped (for example, after <c>PublishAsDockerFile</c>
    /// converts the executable into a <c>ContainerResource</c>).
    /// </summary>
    public string AppDirectory { get; }
}
