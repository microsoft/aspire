@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource kusto 'Microsoft.Kusto/clusters@2025-02-14' = {
  name: take('kusto${uniqueString(resourceGroup().id)}', 24)
  tags: {
    'aspire-resource-name': 'kusto'
  }
  location: location
  sku: {
    name: 'Standard_E2a_v4'
    capacity: 2
    tier: 'Standard'
  }
}

resource testdb 'Microsoft.Kusto/clusters/databases@2025-02-14' = {
  name: 'testdb'
  location: location
  parent: kusto
  kind: 'ReadWrite'
}

output clusterUri string = kusto.properties.uri

output name string = kusto.name