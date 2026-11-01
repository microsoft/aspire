// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Client;
using Aspire.Hosting;

namespace NativeIntegrations.ExternalApi;

/// <summary>Declares an external API without pretending to provision or execute the remote service.</summary>
public static class ExternalApiExtensions
{
    /// <summary>Adds an external API declaration with its validated HTTP(S) base URL.</summary>
    [AspireExport("addExternalApi")]
    public static async Task<ExternalApiResource> AddExternalApi(Composition composition, string name, string baseUrl)
    {
        var uri = new Uri(baseUrl, UriKind.Absolute);
        if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("An external API requires an HTTP(S) endpoint without credentials.", nameof(baseUrl));
        }
        var declaration = await composition.AddResourceAsync(name, "example/external-api").ConfigureAwait(false);
        await declaration.SetResourceConfigurationAsync(new ResourceConfiguration
        {
            Properties = [new ResourceConfigurationProperty { Name = "baseUrl", Value = uri.AbsoluteUri }]
        }).ConfigureAwait(false);

        return new ExternalApiResource(declaration);
    }
}

/// <summary>Retains the integration's own API while the declaration and secret values remain native-owned.</summary>
[AspireExport]
public sealed class ExternalApiResource(DeclaredResource declaration) : NativeIntegrationResource(declaration)
{
    /// <summary>Configures a server-owned secret value without reading the credential in this integration.</summary>
    [AspireExport("withApiKey")]
    public async Task<ExternalApiResource> WithApiKey(DeclaredValue apiKey)
    {
        await Declaration.SetSymbolicConfigurationAsync("apiKey", apiKey).ConfigureAwait(false);

        return this;
    }

    /// <summary>Reads this resource's native identity through delegated core authority.</summary>
    [AspireExport("getResourceName")]
    public async Task<string> GetResourceName()
    {
        var declaration = await Declaration.InspectResourceAsync().ConfigureAwait(false);

        return declaration.Name;
    }
}
