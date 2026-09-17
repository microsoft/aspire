// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a resource annotation whose callback should be evaluated at most once,
/// with the result cached for subsequent retrievals.
/// </summary>
/// <typeparam name="TContext">The type of the context passed to the callback.</typeparam>
/// <typeparam name="TResult">The type of the result produced by the callback.</typeparam>
internal interface ICallbackResourceAnnotation<TContext, TResult>
{
    /// <summary>
    /// Evaluates the callback if it has not been evaluated yet, caching the result.
    /// Subsequent calls return the cached result regardless of the context passed.
    /// </summary>
    /// <param name="context">The context for the callback evaluation. Only used on the first call.</param>
    /// <returns>The cached result of the callback evaluation.</returns>
    Task<TResult> EvaluateOnceAsync(TContext context);

    /// <summary>
    /// Peeks at the already-cached callback result without ever executing the callback.
    /// </summary>
    /// <param name="result">
    /// When this method returns <see langword="true"/>, the task previously produced by
    /// <see cref="EvaluateOnceAsync"/>; otherwise <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a cached result exists; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// This is a read-only peek: unlike <see cref="EvaluateOnceAsync"/> it never invokes the callback and never
    /// populates the cache. It exists so that read-only consumers (such as <c>aspire describe</c> observing live
    /// resource snapshots) can inspect values that DCP has already resolved without racing DCP's own
    /// cache lifecycle. Invoking the callback from such a consumer would run it with the consumer's cancellation
    /// token and could cache a canceled or faulted task that DCP would later reuse on the resource's execution path.
    /// </remarks>
    bool TryGetCachedResult(out Task<TResult>? result);

    /// <summary>
    /// Clears the cached result so that the next call to <see cref="EvaluateOnceAsync"/> will re-execute the callback. 
    ///</summary>
    /// <remarks>
    /// Use <see cref="ForgetCachedResult"/> when a resource decorated with this callback annotation is restarted.
    /// </remarks>
    void ForgetCachedResult();
}
