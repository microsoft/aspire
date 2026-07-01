@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource aifmyfoundry 'Microsoft.CognitiveServices/accounts@2025-09-01' = {
  name: toLower('aifmyfoundry-${uniqueString(resourceGroup().id)}')
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  kind: 'AIServices'
  properties: {
    customSubDomainName: toLower('aifmyfoundry-${uniqueString(resourceGroup().id)}')
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
    allowProjectManagement: true
  }
  sku: {
    name: 'S0'
  }
  tags: {
    'aspire-resource-name': 'aifmyfoundry'
  }
}

resource aifmyfoundry_caphost 'Microsoft.CognitiveServices/accounts/capabilityHosts@2025-10-01-preview' = {
  name: 'foundry-caphost'
  properties: {
    capabilityHostKind: 'Agents'
    enablePublicHostingEnvironment: true
  }
  parent: aifmyfoundry
}

resource chat 'Microsoft.CognitiveServices/accounts/deployments@2025-09-01' = {
  name: 'chat'
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4.1'
      version: '2025-04-14'
    }
  }
  sku: {
    name: 'GlobalStandard'
    capacity: 1
  }
  parent: aifmyfoundry
}

output aiFoundryApiEndpoint string = aifmyfoundry.properties.endpoints['AI Foundry API']

output endpoint string = aifmyfoundry.properties.endpoint

output name string = aifmyfoundry.name

output id string = aifmyfoundry.id