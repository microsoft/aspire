// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Overrides the remote invocation timeout for a callback parameter of an exported method.
/// </summary>
/// <remarks>
/// Without this attribute, callbacks use the configured remote-host callback timeout.
/// An infinite timeout removes the fixed deadline, but does not disable cancellation or
/// connection-lifetime enforcement. For annotated callbacks without a cancellation token
/// parameter, the first argument with a public <c>CancellationToken</c> property supplies
/// invocation cancellation.
/// </remarks>
/// <example>
/// <code lang="C#">
/// [AspireExport]
/// public static Task ConfigureAsync(
///     [AspireCallbackTimeout(120_000)] Func&lt;CancellationToken, Task&gt; callback,
///     CancellationToken cancellationToken)
/// {
///     return callback(cancellationToken);
/// }
/// </code>
/// </example>
/// <param name="millisecondsTimeout">
/// A positive timeout in milliseconds, or <see cref="Timeout.Infinite"/> to wait without a fixed deadline.
/// </param>
/// <exception cref="ArgumentOutOfRangeException">
/// The timeout is neither positive nor <see cref="Timeout.Infinite"/>.
/// </exception>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class AspireCallbackTimeoutAttribute(int millisecondsTimeout) : Attribute
{
    /// <summary>
    /// Gets the timeout in milliseconds, or <see cref="Timeout.Infinite"/> for no fixed deadline.
    /// </summary>
    public int MillisecondsTimeout { get; } = ValidateTimeout(millisecondsTimeout);

    private static int ValidateTimeout(int millisecondsTimeout)
    {
        if (millisecondsTimeout != Timeout.Infinite)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(millisecondsTimeout);
        }

        return millisecondsTimeout;
    }
}
