@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource openai 'Microsoft.CognitiveServices/accounts@2025-09-01' = {
  name: take('openai-${uniqueString(resourceGroup().id)}', 64)
  location: location
  kind: 'OpenAI'
  properties: {
    customSubDomainName: toLower(take(concat('openai', uniqueString(resourceGroup().id)), 24))
    publicNetworkAccess: 'Disabled'
    disableLocalAuth: true
  }
  sku: {
    name: 'S0'
  }
  tags: {
    'aspire-resource-name': 'openai'
  }
}

output connectionString string = 'Endpoint=${openai.properties.endpoint}'

output endpoint string = openai.properties.endpoint

output name string = openai.name

output id string = openai.id