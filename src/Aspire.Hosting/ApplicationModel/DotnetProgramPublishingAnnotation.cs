// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Marks a .NET program resource whose container image is published through the .NET SDK.
/// </summary>
internal sealed class DotnetProgramPublishingAnnotation : IResourceAnnotation;
