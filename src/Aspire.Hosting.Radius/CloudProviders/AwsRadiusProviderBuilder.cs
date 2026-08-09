// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Radius.CloudProviders;

/// <summary>
/// In-flight builder backing <see cref="IAwsRadiusProviderBuilder"/>. The
/// <see cref="Credential"/> slot is mutated by each <c>With*</c> call (last
/// write wins) and the final value is hoisted into
/// <see cref="AwsRadiusProviderConfig"/> by <c>WithAwsProvider</c>.
/// </summary>
internal sealed class AwsRadiusProviderBuilder : IAwsRadiusProviderBuilder
{
    private readonly ILogger _logger;

    internal AwsRadiusProviderBuilder(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    internal AwsRadiusCredential? Credential { get; private set; }

    public IAwsRadiusProviderBuilder WithAccessKey(
        IResourceBuilder<ParameterResource> accessKeyId,
        IResourceBuilder<ParameterResource> secretAccessKey)
    {
        ArgumentNullException.ThrowIfNull(accessKeyId);
        ArgumentNullException.ThrowIfNull(secretAccessKey);

        LogOverrideIfNeeded(nameof(WithAccessKey));
        Credential = new AwsRadiusCredential.AccessKey(accessKeyId, secretAccessKey);
        return this;
    }

    public IAwsRadiusProviderBuilder WithIrsa(string iamRoleArn)
    {
        CloudProviderValidation.ValidateIamRoleArn(iamRoleArn, nameof(iamRoleArn));

        LogOverrideIfNeeded(nameof(WithIrsa));
        Credential = new AwsRadiusCredential.Irsa(iamRoleArn);
        return this;
    }

    private void LogOverrideIfNeeded(string newMode)
    {
        if (Credential is not null)
        {
            _logger.LogDebug(
                "AWS credential overridden inside WithAwsProvider callback: '{Previous}' replaced by '{New}'.",
                Credential.GetType().Name,
                newMode);
        }
    }
}
