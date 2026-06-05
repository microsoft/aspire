@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sku string = 'Standard'

resource servicebus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: take('servicebus-${uniqueString(resourceGroup().id)}', 50)
  location: location
  properties: {
    disableLocalAuth: true
  }
  sku: {
    name: sku
  }
  tags: {
    'aspire-resource-name': 'servicebus'
  }
}

resource queue1 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  name: 'queue1'
  properties: {
    lockDuration: 'PT5M'
    maxDeliveryCount: 5
  }
  parent: servicebus
}

resource topic1 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  name: 'topic1'
  properties: {
    enablePartitioning: true
  }
  parent: servicebus
}

resource subscription2 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  name: 'subscription2'
  parent: topic1
}

resource topic2 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  name: 'topic2'
  parent: servicebus
}

resource subscription1 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  name: 'subscription1'
  properties: {
    lockDuration: 'PT5M'
    requiresSession: true
  }
  parent: topic2
}

resource topic3 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  name: 'topic3'
  parent: servicebus
}

resource sub1 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  name: 'sub1'
  parent: topic3
}

resource sub2 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  name: 'sub2'
  parent: topic3
}

output serviceBusEndpoint string = servicebus.properties.serviceBusEndpoint

output serviceBusHostName string = split(replace(servicebus.properties.serviceBusEndpoint, 'https://', ''), ':')[0]

output name string = servicebus.name