// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Azure.Common;
using Aspire.Azure.Security.KeyVault;
using Azure.Core;
using Azure.Core.Extensions;
using Azure.Security.KeyVault.Certificates;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Representation of an <see cref="AzureComponent{TSettings, TClient, TClientOptions}"/> configured as a <see cref="CertificateClient"/>
/// </summary>
internal sealed class AzureKeyVaultCertificatesComponent : AbstractAzureKeyVaultComponent<CertificateClient, CertificateClientOptions>
{
    internal override CertificateClient CreateComponentClient(Uri vaultUri, CertificateClientOptions options, TokenCredential cred)
        => new(vaultUri, cred, options);

    protected override IHealthCheck CreateHealthCheck(CertificateClient client, AzureSecurityKeyVaultSettings settings)
        => new AzureKeyVaultCertificatesHealthCheck(client);

    protected override void BindClientOptionsToConfiguration(IAzureClientBuilder<CertificateClient, CertificateClientOptions> clientBuilder, IConfiguration configuration)
#pragma warning disable IDE0200 // Remove unnecessary lambda expression - needed so the ConfigBinder Source Generator works
        => clientBuilder.ConfigureOptions(options => configuration.Bind(options));
#pragma warning restore IDE0200 // Remove unnecessary lambda expression

    protected override void BindSettingsToConfiguration(AzureSecurityKeyVaultSettings settings, IConfiguration configuration)
        => configuration.Bind(settings);
}
