// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Marks a resource whose completion contributes to the lifetime and outcome of an <c>aspire test</c> run.
/// </summary>
/// <remarks>
/// The annotation has no effect on normal application runs. Test resources must publish a terminal
/// resource state and an exit code; zero indicates success. Failure to start also indicates failure.
/// An optional setup callback receives the invocation's output destination before resources start.
/// </remarks>
public sealed class TestAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Creates an annotation marking a test resource, optionally configuring it before startup.
    /// </summary>
    /// <param name="callback">The test setup callback, or <see langword="null"/> to mark the resource without additional setup.</param>
    public TestAnnotation(Func<TestRunContext, Task>? callback = null)
    {
        Callback = callback;
    }

    /// <summary>
    /// Gets the callback invoked before resource startup during <c>aspire test</c>.
    /// </summary>
    public Func<TestRunContext, Task>? Callback { get; }
}
