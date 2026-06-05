@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sku string = 'Standard'

resource sb 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: take('sb-${uniqueString(resourceGroup().id)}', 50)
  location: location
  properties: {
    disableLocalAuth: true
  }
  sku: {
    name: sku
  }
  tags: {
    'aspire-resource-name': 'sb'
  }
}

resource queue1 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  name: 'queue1'
  parent: sb
}

resource queue2 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  name: 'queue2'
  parent: sb
}

resource t1 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  name: 't1'
  parent: sb
}

resource s3 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  name: 's3'
  parent: t1
}

resource t2 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  name: 't2'
  parent: sb
}

output serviceBusEndpoint string = sb.properties.serviceBusEndpoint

output serviceBusHostName string = split(replace(sb.properties.serviceBusEndpoint, 'https://', ''), ':')[0]

output name string = sb.name
