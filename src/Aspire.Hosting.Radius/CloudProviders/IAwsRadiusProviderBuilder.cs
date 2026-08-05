// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Radius.CloudProviders;

/// <summary>
/// Builder surface exposed inside the <c>WithAwsProvider</c> callback for
/// selecting an AWS credential mode. Exactly one <c>With*</c> method must
/// be called; a repeat call replaces the previous selection.
/// </summary>
public interface IAwsRadiusProviderBuilder
{
    /// <summary>
    /// Configures an IAM access key credential. Both the access key id and
    /// secret access key values are provided via
    /// <see cref="ParameterResource"/>s so plaintext never appears in the
    /// publish artifact. Reference them with
    /// <c>builder.AddParameter("awsAccessKeyId", secret: true)</c> /
    /// <c>builder.AddParameter("awsSecretAccessKey", secret: true)</c>.
    /// </summary>
    /// <param name="accessKeyId">Parameter carrying the AWS access key id.</param>
    /// <param name="secretAccessKey">Parameter carrying the AWS secret access key.</param>
    /// <returns>This builder for chaining.</returns>
    IAwsRadiusProviderBuilder WithAccessKey(
        IResourceBuilder<ParameterResource> accessKeyId,
        IResourceBuilder<ParameterResource> secretAccessKey);

    /// <summary>
    /// Configures an IAM Roles for Service Accounts (IRSA) credential. No
    /// long-lived secret is materialized; the role is assumed at deploy
    /// time via the hosting Kubernetes cluster's OIDC provider.
    /// </summary>
    /// <param name="iamRoleArn">Fully-qualified IAM role ARN to assume.</param>
    /// <returns>This builder for chaining.</returns>
    IAwsRadiusProviderBuilder WithIrsa(string iamRoleArn);
}
