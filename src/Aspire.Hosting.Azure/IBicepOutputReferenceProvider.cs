// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a value that is published as a <see cref="BicepOutputReference"/>, but whose output is not known
/// when the value is created.
/// </summary>
/// <remarks>
/// Azure publishers treat a value that implements this interface as the output it returns, as if the value were that
/// <see cref="BicepOutputReference"/> itself. This lets a value, such as a client ID of an identity that is created while
/// the application model is prepared for publishing, be handed out before the output exists.
/// </remarks>
[Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IBicepOutputReferenceProvider
{
    /// <summary>
    /// Gets the output that this value is published as.
    /// </summary>
    /// <returns>The output, or <see langword="null"/> if it is not available yet.</returns>
    BicepOutputReference? GetOutputReference();
}
