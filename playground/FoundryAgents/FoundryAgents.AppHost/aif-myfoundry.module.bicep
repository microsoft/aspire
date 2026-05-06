@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource aif_myfoundry 'Microsoft.CognitiveServices/accounts@2025-09-01' = {
  name: take('aifmyfoundry-${uniqueString(resourceGroup().id)}', 64)
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  kind: 'AIServices'
  properties: {
    customSubDomainName: toLower(take(concat('aif-myfoundry', uniqueString(resourceGroup().id)), 24))
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
    allowProjectManagement: true
  }
  sku: {
    name: 'S0'
  }
  tags: {
    'aspire-resource-name': 'aif-myfoundry'
  }
}

resource aif_myfoundry_caphost 'Microsoft.CognitiveServices/accounts/capabilityHosts@2025-10-01-preview' = {
  name: 'foundry-caphost'
  properties: {
    capabilityHostKind: 'Agents'
    enablePublicHostingEnvironment: true
  }
  parent: aif_myfoundry
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
  parent: aif_myfoundry
}

output aiFoundryApiEndpoint string = aif_myfoundry.properties.endpoints['AI Foundry API']

output endpoint string = aif_myfoundry.properties.endpoint

output name string = aif_myfoundry.name

output id string = aif_myfoundry.id