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