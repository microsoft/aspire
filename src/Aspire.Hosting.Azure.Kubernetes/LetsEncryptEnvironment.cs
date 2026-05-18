// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// Selects which Let's Encrypt ACME directory the auto-provisioned
/// <c>ClusterIssuer</c> created by
/// <see cref="AzureKubernetesSimplifiedDeploymentExtensions.WithSimplifiedDeployment"/>
/// will point at.
/// </summary>
/// <remarks>
/// Production is the default because the entire point of <c>WithSimplifiedDeployment</c>
/// is to land in the "happy path" — and the happy path for a production AKS deploy
/// is a real, browser-trusted certificate. Staging exists only for development
/// loops where the strict Let's Encrypt rate limits (≈5 certs / hostname / week
/// against production) would otherwise be a problem during repeated deploys.
/// </remarks>
public enum LetsEncryptEnvironment
{
    /// <summary>
    /// Let's Encrypt production directory at
    /// <c>https://acme-v02.api.letsencrypt.org/directory</c>. Issues certificates
    /// from the publicly trusted ISRG root. Rate-limited to roughly 5 certificates
    /// per hostname per week.
    /// </summary>
    Production = 0,

    /// <summary>
    /// Let's Encrypt staging directory at
    /// <c>https://acme-staging-v02.api.letsencrypt.org/directory</c>. Issues
    /// certificates from an untrusted staging root, so browsers will warn — but
    /// the rate limits are far more generous (≈30,000 certs / hostname / week),
    /// which is the right tradeoff for development environments that redeploy
    /// frequently.
    /// </summary>
    Staging = 1,
}
