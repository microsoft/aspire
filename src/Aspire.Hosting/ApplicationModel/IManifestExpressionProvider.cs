// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// An interface that allows an object to express how it should be represented in a manifest.
/// </summary>
public interface IManifestExpressionProvider
{
    /// <summary>
    /// Gets the expression that represents a value in manifest.
    /// </summary>
    string ValueExpression { get; }
}
