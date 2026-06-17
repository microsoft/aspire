// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a value that provides both a runtime value and a manifest expression.
/// </summary>
/// <remarks>
/// Expression values can be used anywhere both a runtime value and a publish-time
/// manifest expression are needed.
/// </remarks>
[AspireExport]
public interface IExpressionValue : IValueProvider, IManifestExpressionProvider
{
}
