// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Ats;

/// <summary>
/// Options that control which reference information is injected into environment variables.
/// </summary>
[AspireDto]
internal sealed class ReferenceEnvironmentInjectionOptions
{
    /// <summary>
    /// Injects the connection string environment variable.
    /// </summary>
    public bool ConnectionString { get; set; }

    /// <summary>
    /// Injects individual connection property environment variables.
    /// </summary>
    public bool ConnectionProperties { get; set; }

    /// <summary>
    /// Injects service discovery environment variables.
    /// </summary>
    public bool ServiceDiscovery { get; set; }

    /// <summary>
    /// Injects endpoint environment variables.
    /// </summary>
    public bool Endpoints { get; set; }

    /// <summary>
    /// Converts the DTO into the corresponding flags enum.
    /// </summary>
    /// <returns>The selected injection flags.</returns>
    public ReferenceEnvironmentInjectionFlags ToFlags()
    {
        var flags = ReferenceEnvironmentInjectionFlags.None;

        if (ConnectionString)
        {
            flags |= ReferenceEnvironmentInjectionFlags.ConnectionString;
        }

        if (ConnectionProperties)
        {
            flags |= ReferenceEnvironmentInjectionFlags.ConnectionProperties;
        }

        if (ServiceDiscovery)
        {
            flags |= ReferenceEnvironmentInjectionFlags.ServiceDiscovery;
        }

        if (Endpoints)
        {
            flags |= ReferenceEnvironmentInjectionFlags.Endpoints;
        }

        return flags;
    }
}
