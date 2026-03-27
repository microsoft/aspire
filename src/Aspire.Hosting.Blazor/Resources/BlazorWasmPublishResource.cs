// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// A companion container resource used during publish to build a Blazor WASM app
/// and expose its output for copying into the gateway container.
/// Implements IResourceWithContainerFiles to participate in the container files pipeline.
/// </summary>
internal class BlazorWasmPublishResource(string name)
    : ContainerResource(name), IResourceWithContainerFiles;
