// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a distributed application.
/// </summary>
/// <param name="resources">The resource collection used to initiate the model.</param>
[DebuggerDisplay("Resources = {Resources.Count}")]
[AspireExport]
public class DistributedApplicationModel(IResourceCollection resources)
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DistributedApplicationModel"/> class with the specified resource collection.
    /// </summary>
    /// <param name="resources">
    /// The resources used to initiate the model.
    /// </param>
    public DistributedApplicationModel(IEnumerable<IResource> resources) : this(new ResourceCollection(resources)) { }

    /// <summary>
    /// Gets the collection of resources associated with the distributed application.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public IResourceCollection Resources { get; } = resources ?? throw new ArgumentNullException(nameof(resources));
}
