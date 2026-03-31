// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an annotation that indicates the resource name should not be validated against naming rules when
/// the resource is added to the application model.
/// </summary>
/// <remarks>
/// This is useful for internal resources (such as installers or rebuilders) that append suffixes to user-provided
/// resource names and may exceed the 64-character name limit, but are never deployed.
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}")]
public sealed class SuppressNameValidationAnnotation : IResourceAnnotation
{
}
