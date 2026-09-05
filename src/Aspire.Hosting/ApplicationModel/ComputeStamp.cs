// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a single regional copy ("stamp") of a compute resource: the pairing of the resource with
/// one of the compute environments it is deployed to.
/// </summary>
/// <remarks>
/// <para>
/// A compute resource bound to several compute environments is deployed once per environment. Each of
/// those deployments is a stamp, and each stamp gets its own infrastructure names and its own host address.
/// This mirrors the Azure deployment stamp pattern, where identical copies of a workload are deployed to
/// multiple regions behind a single global entry point.
/// See <see href="https://learn.microsoft.com/azure/architecture/patterns/deployment-stamp"/>.
/// </para>
/// <para>
/// Use <see cref="ResourceExtensions.GetComputeStamps(IResource)"/> to enumerate the stamps of a resource.
/// </para>
/// </remarks>
public sealed class ComputeStamp
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ComputeStamp"/> class.
    /// </summary>
    /// <param name="environment">The compute environment this stamp is deployed to.</param>
    /// <param name="name">The name of the stamp.</param>
    /// <param name="qualifiesNames">Whether infrastructure names generated for this stamp are suffixed with <paramref name="name"/>.</param>
    /// <remarks>
    /// Integration authors normally obtain stamps from <see cref="ResourceExtensions.GetComputeStamps(IResource)"/>.
    /// This constructor exists so an integration can synthesize a single implicit stamp for a resource that
    /// is not explicitly bound to a compute environment.
    /// </remarks>
    public ComputeStamp(IComputeEnvironmentResource environment, string name, bool qualifiesNames)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrEmpty(name);

        Environment = environment;
        Name = name;
        QualifiesNames = qualifiesNames;
    }

    /// <summary>
    /// Gets the compute environment this stamp is deployed to.
    /// </summary>
    public IComputeEnvironmentResource Environment { get; }

    /// <summary>
    /// Gets the name of the stamp. Defaults to the name of <see cref="Environment"/>.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets a value indicating whether infrastructure names generated for this stamp are suffixed with
    /// <see cref="Name"/>.
    /// </summary>
    /// <remarks>
    /// This is <see langword="false"/> for the common case of a resource bound to exactly one compute
    /// environment without an explicit stamp name, so that names generated for single-region applications
    /// are unchanged and already deployed infrastructure is not recreated.
    /// </remarks>
    public bool QualifiesNames { get; }
}
