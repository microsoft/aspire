// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

internal enum HiddenBehavior
{
    Always,
    OnCompletion
}

internal sealed class HiddenAnnotation(HiddenBehavior behavior) : IResourceAnnotation
{
    public HiddenBehavior Behavior { get; } = behavior;

    public List<int> SuccessfulExitCodes { get; init; } = [0];
}
