@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource openai 'Microsoft.CognitiveServices/accounts@2025-09-01' = {
  name: take('openai-${uniqueString(resourceGroup().id)}', 64)
  location: location
  kind: 'OpenAI'
  properties: {
    customSubDomainName: toLower(take(concat('openai', uniqueString(resourceGroup().id)), 24))
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
  }
  sku: {
    name: 'S0'
  }
  tags: {
    'aspire-resource-name': 'openai'
  }
}

resource chat 'Microsoft.CognitiveServices/accounts/deployments@2025-09-01' = {
  name: 'chat'
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4o'
      version: '2024-05-13'
    }
  }
  sku: {
    name: 'Standard'
    capacity: 8
  }
  parent: openai
}

output connectionString string = 'Endpoint=${openai.properties.endpoint}'

output endpoint string = openai.properties.endpoint

output name string = openai.name