// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Go;

internal sealed class GoPrivateAnnotation : IResourceAnnotation
{
    public required string[] PrivatePatterns { get; init; }
    public required string AuthHost { get; init; }
    public required string UsernameArgName { get; init; }
    public required string TokenSecretId { get; init; }
}
