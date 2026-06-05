@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingResourceName string

resource search 'Microsoft.Search/searchServices@2023-11-01' existing = {
  name: existingResourceName
}

output connectionString string = 'Endpoint=https://${existingResourceName}.search.windows.net'

output endpoint string = 'https://${existingResourceName}.search.windows.net'

output name string = search.name
