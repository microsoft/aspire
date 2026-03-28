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
    /// Clears the cached result so that the next call to <see cref="EvaluateOnceAsync"/> will re-execute the callback. 
    ///</summary>
    /// <remarks>
    /// Use <see cref="ForgetCachedResult"/> when a resource decorated with this callback annotation is restarted.
    /// </remarks>
    void ForgetCachedResult();
}
