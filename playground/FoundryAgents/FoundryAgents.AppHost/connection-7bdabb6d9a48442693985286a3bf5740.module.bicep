@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param projmyproject_outputs_name string

param search_outputs_name string

resource projmyproject 'Microsoft.CognitiveServices/accounts/projects@2025-09-01' existing = {
  name: projmyproject_outputs_name
}

resource search 'Microsoft.Search/searchServices@2023-11-01' existing = {
  name: search_outputs_name
}

resource connection_7bdabb6d9a48442693985286a3bf5740 'Microsoft.CognitiveServices/accounts/projects/connections@2026-03-01' = {
  name: 'connection-7bdabb6d9a48442693985286a3bf5740'
  properties: {
    category: 'CognitiveSearch'
    metadata: {
      ApiType: 'Azure'
      ResourceId: search.id
      location: search.location
    }
    target: 'https://${search_outputs_name}.search.windows.net'
    authType: 'AAD'
  }
  parent: projmyproject
}

output name string = 'connection-7bdabb6d9a48442693985286a3bf5740'

output id string = connection_7bdabb6d9a48442693985286a3bf5740.id