// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Core;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Provides access to the <see cref="TokenCredential"/> that Aspire uses
/// to authenticate against Azure when provisioning resources and calling Azure APIs.
/// </summary>
/// <remarks>
/// <para>
/// This service is registered as a singleton when Azure provisioning is enabled
/// (e.g., by <c>AddAzureProvisioning</c>).
/// </para>
/// <para>
/// Integrations and app host code can resolve this service from <see cref="System.IServiceProvider"/>
/// to obtain a <see cref="TokenCredential"/> instance configured by Aspire's
/// Azure provisioning options (for example, the configured tenant id and credential source).
/// The concrete credential type returned by <see cref="TokenCredential"/> is an implementation
/// detail and may change between releases; callers should treat the value as an opaque
/// <see cref="TokenCredential"/>.
/// </para>
/// </remarks>
public interface ITokenCredentialProvider
{
    /// <summary>
    /// Gets the <see cref="TokenCredential"/> to use for Azure authentication.
    /// </summary>
    TokenCredential TokenCredential { get; }
}
