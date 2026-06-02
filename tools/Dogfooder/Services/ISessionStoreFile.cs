// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.State;

namespace Aspire.Dogfooder.Services;

/// <summary>
/// Persistence indirection for <see cref="DogfoodSession"/>s. Splits the
/// JSON-on-disk concerns out of <c>DogfoodSessionStore</c> so the future
/// self-test can swap in an in-memory implementation without touching the
/// store's mutation API.
/// </summary>
internal interface ISessionStoreFile
{
    Task<IReadOnlyList<DogfoodSession>> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(IReadOnlyList<DogfoodSession> sessions, CancellationToken cancellationToken);
}
