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