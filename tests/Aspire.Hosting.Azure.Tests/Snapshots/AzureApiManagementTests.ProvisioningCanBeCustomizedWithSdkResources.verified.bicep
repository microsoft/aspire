@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param apim_identity_outputs_id string

resource apim 'Microsoft.ApiManagement/service@2025-03-01-preview' = {
  name: take('apim${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    publisherEmail: 'api-owners@example.com'
    publisherName: 'Contoso APIs'
    notificationSenderEmail: 'notifications@example.com'
    virtualNetworkType: 'None'
  }
  sku: {
    name: 'Developer'
    capacity: 1
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${apim_identity_outputs_id}': { }
    }
  }
  tags: {
    'aspire-resource-name': 'apim'
  }
}

output gatewayUrl string = apim.properties.gatewayUrl

output name string = apim.name

output id string = apim.id