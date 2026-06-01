// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Radius.CloudProviders;

/// <summary>
/// Captures the full AWS cloud-provider configuration attached to a
/// <see cref="RadiusEnvironmentResource"/>: the account/region scope and
/// the selected credential mode.
/// </summary>
/// <param name="AccountId">12-digit AWS account ID.</param>
/// <param name="Region">AWS region code (e.g. <c>us-west-2</c>).</param>
/// <param name="Credential">Selected credential mode (Access Key or IRSA).</param>
public sealed record AwsRadiusProviderConfig(
    string AccountId,
    string Region,
    AwsRadiusCredential Credential);

/// <summary>
/// Discriminated base for the AWS credential mode chosen via the
/// <c>WithAwsProvider</c> callback. Use one of the sealed subtypes.
/// </summary>
public abstract record AwsRadiusCredential
{
    private AwsRadiusCredential()
    {
    }

    /// <summary>
    /// AWS IAM access key credential. Both the access key id and secret
    /// access key are bound by parameter resources to keep secret material
    /// out of the publish artifact.
    /// </summary>
    /// <param name="AccessKeyId">Parameter carrying the AWS access key id.</param>
    /// <param name="SecretAccessKey">Parameter carrying the AWS secret access key.</param>
    public sealed record AccessKey(
        IResourceBuilder<ParameterResource> AccessKeyId,
        IResourceBuilder<ParameterResource> SecretAccessKey) : AwsRadiusCredential;

    /// <summary>
    /// AWS IAM Roles for Service Accounts (IRSA) credential. No long-lived
    /// secret is materialized; the role is assumed at deploy time via the
    /// hosting Kubernetes cluster's OIDC provider.
    /// </summary>
    /// <param name="IamRoleArn">Fully-qualified IAM role ARN to assume.</param>
    public sealed record Irsa(
        string IamRoleArn) : AwsRadiusCredential;
}
