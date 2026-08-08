// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Marks a resource as a test resource whose completion bounds the lifetime of an
/// <c>aspire test</c> run.
/// </summary>
/// <remarks>
/// <para>
/// When the app host is launched via <c>aspire test</c>, the run stays alive until every resource
/// annotated with <see cref="TestAnnotation"/> reaches a terminal state
/// (<see cref="KnownResourceStates.Finished"/>, <see cref="KnownResourceStates.Exited"/>, or
/// <see cref="KnownResourceStates.FailedToStart"/>). Once they have all finished, the app host is
/// shut down automatically.
/// </para>
/// <para>
/// This is the low-level primitive behind the testing loop. Higher-level helpers (for example
/// test-project, pytest, or Playwright integrations) attach this annotation on the caller's behalf.
/// Result harvesting and progress reporting are layered on top of this marker in later iterations.
/// </para>
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}")]
public sealed class TestAnnotation : IResourceAnnotation
{
}
