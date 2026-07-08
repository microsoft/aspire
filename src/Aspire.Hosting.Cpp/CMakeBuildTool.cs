// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Cpp;

/// <summary>
/// Describes a command-line tool required to configure, build, or package a C++ application.
/// </summary>
/// <param name="Command">The command that should be available on the local machine PATH.</param>
/// <param name="HelpLink">An optional URL shown to users when the command is missing.</param>
public sealed record CMakeBuildTool(string Command, string? HelpLink = null);

