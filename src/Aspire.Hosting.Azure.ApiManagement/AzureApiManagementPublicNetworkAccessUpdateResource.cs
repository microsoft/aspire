// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

internal sealed class AzureApiManagementPublicNetworkAccessStateResource : AzureBicepResource
{
    private const string Template =
        """
        param apimName string

        resource apim 'Microsoft.ApiManagement/service@2025-03-01-preview' existing = {
          name: apimName
        }

        #disable-next-line use-resource-symbol-reference
        var current = reference(apim.id, '2025-03-01-preview', 'Full')
        var customHostnameConfigurations = map(
          filter(
            current.properties.?hostnameConfigurations ?? [],
            configuration => configuration.certificateSource != 'BuiltIn'),
          configuration => {
            type: configuration.type
            hostName: configuration.hostName
            keyVaultId: configuration.?keyVaultId
            identityClientId: configuration.?identityClientId
            defaultSslBinding: configuration.defaultSslBinding
            negotiateClientCertificate: configuration.negotiateClientCertificate
          })

        output state string = string({
          name: last(split(apim.id, '/'))
          location: current.location
          sku: current.sku
          tags: current.?tags ?? {}
          identity: contains(current, 'identity') ? union(
            {
              type: current.identity.type
            },
            contains(current.identity.type, 'UserAssigned') ? {
              userAssignedIdentities: toObject(
                items(current.identity.?userAssignedIdentities ?? {}),
                identity => identity.key,
                identity => {})
            } : {}) : null
          properties: {
            publisherEmail: current.properties.publisherEmail
            publisherName: current.properties.publisherName
            notificationSenderEmail: current.properties.notificationSenderEmail
            hostnameConfigurations: customHostnameConfigurations
            virtualNetworkType: current.properties.virtualNetworkType
            virtualNetworkConfiguration: current.properties.virtualNetworkConfiguration
            customProperties: current.properties.customProperties
          }
        })
        """;

    public AzureApiManagementPublicNetworkAccessStateResource(
        string name,
        AzureApiManagementResource apiManagement,
        AzurePrivateEndpointResource privateEndpoint)
        : base(name, templateString: Template)
    {
        ApiManagement = apiManagement;
        PrivateEndpoint = privateEndpoint;
        Parameters["apimName"] = apiManagement.NameOutputReference;

        // Capturing state after the private endpoint is approved prevents the following update
        // from disabling public access before the private route is available.
        References.Add(privateEndpoint);
    }

    public AzureApiManagementResource ApiManagement { get; }

    public AzurePrivateEndpointResource PrivateEndpoint { get; }

    public BicepOutputReference State => new("state", this);
}

internal sealed class AzureApiManagementPublicNetworkAccessUpdateResource : AzureBicepResource
{
    private const string Template =
        """
        param state string

        var current = json(state)

        resource apim 'Microsoft.ApiManagement/service@2025-03-01-preview' = {
          name: current.name
          location: current.location
          tags: current.tags
          sku: current.sku
          identity: current.identity
          properties: union(current.properties, {
            publicNetworkAccess: 'Disabled'
          })
        }
        """;

    public AzureApiManagementPublicNetworkAccessUpdateResource(
        string name,
        AzureApiManagementPublicNetworkAccessStateResource state)
        : base(name, templateString: Template)
    {
        State = state;
        Parameters["state"] = state.State;
    }

    public AzureApiManagementPublicNetworkAccessStateResource State { get; }
}
