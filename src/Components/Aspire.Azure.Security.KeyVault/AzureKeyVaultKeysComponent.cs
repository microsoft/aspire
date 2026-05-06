// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Azure.Security.KeyVault;
using Azure.Core;
using Azure.Core.Extensions;
using Azure.Security.KeyVault.Keys;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.Hosting;

internal sealed class AzureKeyVaultKeysComponent : AbstractAzureKeyVaultComponent<KeyClient, KeyClientOptions>
{
    protected override IHealthCheck CreateHealthCheck(KeyClient client, AzureSecurityKeyVaultSettings settings)
        => new AzureKeyVaultKeysHealthCheck(client);

    internal override KeyClient CreateComponentClient(Uri vaultUri, KeyClientOptions options, TokenCredential cred)
        => new(vaultUri, cred, options);

    protected override void BindClientOptionsToConfiguration(IAzureClientBuilder<KeyClient, KeyClientOptions> clientBuilder, IConfiguration configuration)
#pragma warning disable IDE0200 // Remove unnecessary lambda expression - needed so the ConfigBinder Source Generator works
        => clientBuilder.ConfigureOptions(options => configuration.Bind(options));
#pragma warning restore IDE0200 // Remove unnecessary lambda expression

    protected override void BindSettingsToConfiguration(AzureSecurityKeyVaultSettings settings, IConfiguration configuration)
        => configuration.Bind(settings);
}
