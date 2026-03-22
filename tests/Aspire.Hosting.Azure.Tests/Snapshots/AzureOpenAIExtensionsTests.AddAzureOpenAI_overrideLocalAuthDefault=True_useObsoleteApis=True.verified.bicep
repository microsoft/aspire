@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource openai 'Microsoft.CognitiveServices/accounts@2025-09-01' = {
  name: take('openai-${uniqueString(resourceGroup().id)}', 64)
  location: location
  kind: 'OpenAI'
  properties: {
    customSubDomainName: toLower(take(concat('openai', uniqueString(resourceGroup().id)), 24))
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: false
  }
  sku: {
    name: 'S0'
  }
  tags: {
    'aspire-resource-name': 'openai'
  }
}

resource mymodel 'Microsoft.CognitiveServices/accounts/deployments@2025-09-01' = {
  name: 'mymodel'
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-35-turbo'
      version: '0613'
    }
  }
  sku: {
    name: 'Basic'
    capacity: 4
  }
  parent: openai
}

resource embedding_model 'Microsoft.CognitiveServices/accounts/deployments@2025-09-01' = {
  name: 'embedding-model'
  properties: {
    model: {
      format: 'OpenAI'
      name: 'text-embedding-ada-002'
      version: '2'
    }
  }
  sku: {
    name: 'Basic'
    capacity: 4
  }
  parent: openai
  dependsOn: [
    mymodel
  ]
}

output connectionString string = 'Endpoint=${openai.properties.endpoint}'

output endpoint string = openai.properties.endpoint

output name string = openai.name