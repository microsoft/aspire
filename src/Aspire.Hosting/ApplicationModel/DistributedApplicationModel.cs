// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a distributed application.
/// </summary>
[DebuggerDisplay("Resources = {Resources.Count}")]
[AspireExport]
public class DistributedApplicationModel
{
    private readonly IResourceCollection _resourceOwners;

    /// <summary>
    /// Initializes a new instance of the <see cref="DistributedApplicationModel"/> class with the specified resource collection.
    /// </summary>
    /// <param name="resources">The resources used to initiate the model.</param>
    public DistributedApplicationModel(IResourceCollection resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        _resourceOwners = resources;
        Resources = new EffectiveResourceCollection(resources);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DistributedApplicationModel"/> class with the specified resource collection.
    /// </summary>
    /// <param name="resources">
    /// The resources used to initiate the model.
    /// </param>
    public DistributedApplicationModel(IEnumerable<IResource> resources) : this(new ResourceCollection(resources)) { }

    /// <summary>
    /// Gets the effective resources associated with the distributed application.
    /// </summary>
    /// <remarks>
    /// A selected projection is returned in place of its canonical owner. Use
    /// <see cref="DistributedApplicationModelExtensions.GetResourceOwners(DistributedApplicationModel)"/>
    /// when identity-sensitive code needs the underlying model members.
    /// </remarks>
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public IResourceCollection Resources { get; }

    internal IEnumerable<IResource> ResourceOwners => _resourceOwners;
}
