// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Defines custom <c>WithEnvironment</c> dispatch behavior for an environment value type.
/// </summary>
/// <typeparam name="TSelf">The concrete value type that provides the custom dispatch behavior.</typeparam>
/// <remarks>
/// This contract is used by the internal ATS-visible <c>withEnvironment</c> dispatcher
/// to route environment values to type-specific logic at runtime. Implementations may
/// customize dispatch when a value needs behavior that differs from the default
/// <see cref="IEnvironmentValue"/> handling.
/// </remarks>
[Experimental("ASPIREATS001")]
public interface IValueWithCustomWithEnvironment<TSelf> : IEnvironmentValue
    where TSelf : IEnvironmentValue, IValueWithCustomWithEnvironment<TSelf>
{
    /// <summary>
    /// Applies an environment value to <paramref name="builder"/> using value-specific behavior.
    /// </summary>
    /// <param name="builder">The destination resource builder.</param>
    /// <param name="name">The environment variable name.</param>
    /// <param name="value">The environment value.</param>
    /// <typeparam name="TDestination">The destination resource type.</typeparam>
    /// <returns>The destination <see cref="IResourceBuilder{T}"/> when handled; otherwise, <see langword="null"/>.</returns>
    static abstract IResourceBuilder<TDestination>? TryWithEnvironment<TDestination>(
        IResourceBuilder<TDestination> builder,
        string name,
        TSelf value)
        where TDestination : IResourceWithEnvironment;
}
