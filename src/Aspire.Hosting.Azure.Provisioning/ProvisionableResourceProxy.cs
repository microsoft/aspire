// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.Provisioning;

/// <summary>
/// Represents a resource declaration in an Azure Provisioning infrastructure model.
/// </summary>
[AspireExport]
public class ProvisionableResourceProxy
{
    /// <summary>
    /// Initializes a proxy for generated provisioning integration code.
    /// </summary>
    /// <param name="value">The Azure Provisioning resource to wrap.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    protected ProvisionableResourceProxy(ProvisionableResource value)
    {
        Inner = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Gets the identifier used for this resource in the generated Bicep module.
    /// </summary>
    [AspireExport]
    internal string BicepIdentifier => Inner.BicepIdentifier;

    /// <summary>
    /// Gets the wrapped Azure Provisioning resource.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [AspireExportIgnore(Reason = "Used by generated provisioning proxy code.")]
    public ProvisionableResource Inner { get; }
}
