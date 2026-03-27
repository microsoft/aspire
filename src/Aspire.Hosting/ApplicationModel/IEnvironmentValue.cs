// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a value that can be used with <c>WithEnvironment</c>.
/// </summary>
/// <remarks>
/// Environment values provide both the runtime value used during local execution and
/// the manifest expression used during publish operations.
/// </remarks>
[AspireExport]
public interface IEnvironmentValue : IValueProvider, IManifestExpressionProvider
{
}
