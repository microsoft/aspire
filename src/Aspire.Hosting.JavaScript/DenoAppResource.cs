// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// A resource that represents a Deno application.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="command">The command to execute.</param>
/// <param name="workingDirectory">The working directory to use for the command.</param>
[AspireExport(ExposeProperties = true)]
[Experimental("ASPIREDENO001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class DenoAppResource(string name, string command, string workingDirectory)
    : JavaScriptAppResource(name, command, workingDirectory), IResourceWithServiceDiscovery, IContainerFilesDestinationResource;
