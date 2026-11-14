// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Azure.Security.KeyVault;
using Azure.Core;
using Azure.Core.Extensions;
using Azure.Security.KeyVault.Secrets;
using HealthChecks.Azure.KeyVault.Secrets;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.Hosting;

internal sealed class AzureKeyVaultSecretsComponent : AbstractAzureKeyVaultComponent<SecretClient, SecretClientOptions>
{
    protected override void BindClientOptionsToConfiguration(IAzureClientBuilder<SecretClient, SecretClientOptions> clientBuilder, IConfiguration configuration)
#pragma warning disable IDE0200 // Remove unnecessary lambda expression - needed so the ConfigBinder Source Generator works
        => clientBuilder.ConfigureOptions(options => configuration.Bind(options));
#pragma warning restore IDE0200 // Remove unnecessary lambda expression

    protected override void BindSettingsToConfiguration(AzureSecurityKeyVaultSettings settings, IConfiguration configuration)
        => configuration.Bind(settings);

    protected override IHealthCheck CreateHealthCheck(SecretClient client, AzureSecurityKeyVaultSettings settings)
        => new AzureKeyVaultSecretsHealthCheck(client, settings.HealthCheckOptions);

    internal override SecretClient CreateComponentClient(Uri vaultUri, SecretClientOptions options, TokenCredential cred)
        => new(vaultUri, cred, options);
}
