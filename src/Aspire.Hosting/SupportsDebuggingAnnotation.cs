// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an annotation that specifies that the resource can be debugged by the Aspire Extension.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, RequiredExtensionId = {LaunchConfigurationType,nq}")]
[Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
internal sealed class SupportsDebuggingAnnotation : IResourceAnnotation
{
    private SupportsDebuggingAnnotation(bool enabled, string? launchConfigurationType, Action<Executable, string>? launchConfigurationAnnotator)
    {
        Enabled = enabled;
        LaunchConfigurationType = launchConfigurationType;
        LaunchConfigurationAnnotator = launchConfigurationAnnotator;
    }

    /// <summary>
    /// Gets a value indicating whether debugging is enabled for the resource. When <see langword="false"/>
    /// the annotation is an explicit opt-out (created via <see cref="Disabled"/>, e.g. by <c>WithTerminal()</c>) that forces the
    /// resource to run as a plain process even inside an IDE debug session. A disabled annotation carries
    /// no launch configuration, so <see cref="LaunchConfigurationType"/> and
    /// <see cref="LaunchConfigurationAnnotator"/> are <see langword="null"/>.
    /// <para>
    /// This opt-out exists for usability: DCP cannot currently run a process under the debugger and a
    /// pseudo-terminal (PTY) at the same time, and attaching the debugger breaks the PTY flow. If we let
    /// the debugger attach, the user would just see an empty terminal with no output, which is confusing.
    /// We therefore prefer to keep the terminal (PTY) working; the user can attach the debugger themselves
    /// afterwards if they need to. Remove this once DCP supports both simultaneously:
    /// https://github.com/microsoft/dcp/issues/189.
    /// </para>
    /// </summary>
    public bool Enabled { get; }

    public string? LaunchConfigurationType { get; }
    public Action<Executable, string>? LaunchConfigurationAnnotator { get; }

    internal static SupportsDebuggingAnnotation Create<T>(string launchConfigurationType, Func<string, T> launchProfileProducer)
    {
        return new SupportsDebuggingAnnotation(enabled: true, launchConfigurationType, (exe, mode) =>
        {
            exe.AnnotateAsObjectList(Executable.LaunchConfigurationsAnnotation, launchProfileProducer(mode));
        });
    }

    // Creates an explicit opt-out annotation that forces plain process execution even inside a debug
    // session. Used for resources (e.g. terminal-attached ones via WithTerminal()) where attaching the
    // debugger would break the pseudo-terminal (PTY) flow and leave the user staring at an empty terminal
    // with no output. We prefer a working PTY over an auto-attached debugger; the user can attach the
    // debugger themselves afterwards. Temporary until DCP can run a process under the debugger and a PTY
    // simultaneously: https://github.com/microsoft/dcp/issues/189.
    internal static SupportsDebuggingAnnotation Disabled()
    {
        return new SupportsDebuggingAnnotation(enabled: false, launchConfigurationType: null, launchConfigurationAnnotator: null);
    }
}