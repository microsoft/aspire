// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Marks a resource whose completion contributes to the lifetime and outcome of an <c>aspire test</c> run.
/// </summary>
/// <remarks>
/// The annotation has no effect on normal application runs. Test resources must publish a terminal
/// resource state and an exit code; zero indicates success. Failure to start also indicates failure.
/// </remarks>
public sealed class TestAnnotation : IResourceAnnotation
{
}
