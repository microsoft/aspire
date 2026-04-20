@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource foundry 'Microsoft.CognitiveServices/accounts@2025-09-01' = {
  name: take('foundry-${uniqueString(resourceGroup().id)}', 64)
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  kind: 'AIServices'
  properties: {
    customSubDomainName: toLower(take(concat('foundry', uniqueString(resourceGroup().id)), 24))
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
    allowProjectManagement: true
  }
  sku: {
    name: 'S0'
  }
  tags: {
    'aspire-resource-name': 'foundry'
  }
}

resource foundry_caphost 'Microsoft.CognitiveServices/accounts/capabilityHosts@2025-10-01-preview' = {
  name: 'foundry-caphost'
  properties: {
    capabilityHostKind: 'Agents'
    enablePublicHostingEnvironment: true
  }
  parent: foundry
}

resource deployment1 'Microsoft.CognitiveServices/accounts/deployments@2025-09-01' = {
  name: 'deployment1'
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4'
      version: '1.0'
    }
  }
  sku: {
    name: 'GlobalStandard'
    capacity: 1
  }
  parent: foundry
}

resource deployment2 'Microsoft.CognitiveServices/accounts/deployments@2025-09-01' = {
  name: 'deployment2'
  properties: {
    model: {
      format: 'Microsoft'
      name: 'Phi-4'
      version: '1.0'
    }
  }
  sku: {
    name: 'GlobalStandard'
    capacity: 1
  }
  parent: foundry
  dependsOn: [
    deployment1
  ]
}

resource my_model 'Microsoft.CognitiveServices/accounts/deployments@2025-09-01' = {
  name: 'my-model'
  properties: {
    model: {
      format: 'Microsoft'
      name: 'Phi-4'
      version: '1.0'
    }
  }
  sku: {
    name: 'GlobalStandard'
    capacity: 1
  }
  parent: foundry
  dependsOn: [
    deployment2
  ]
}

output aiFoundryApiEndpoint string = foundry.properties.endpoints['AI Foundry API']

output endpoint string = foundry.properties.endpoint

output name string = foundry.name

output id string = foundry.id