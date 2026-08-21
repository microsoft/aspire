// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Maui;

/// <summary>
/// Identifies the stage of the MAUI resource startup pipeline that a build-argument
/// callback participates in.
/// </summary>
public enum MauiBuildStep
{
    /// <summary>
    /// The serialized compile (<c>dotnet build</c>) that runs before the app is launched.
    /// Arguments passed at this stage form the complete <c>dotnet build</c> command used to compile
    /// the project for the target platform (verb, project path, target framework, configuration, and
    /// any additional MSBuild properties).
    /// </summary>
    Build,

    /// <summary>
    /// The launch command that starts the already-built app
    /// (<c>dotnet build --no-restore /t:Run -p:NoBuild=true</c>). Arguments passed at this stage are the
    /// verb and options that replace DCP's default <c>run</c> verb; the project path and
    /// <c>--configuration</c> are appended by the host and are not part of the editable arguments.
    /// </summary>
    Launch
}
