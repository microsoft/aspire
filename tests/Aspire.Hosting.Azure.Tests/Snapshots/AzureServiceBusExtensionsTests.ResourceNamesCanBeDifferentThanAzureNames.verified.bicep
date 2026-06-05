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
  name: 'queueName'
  properties: {
    defaultMessageTimeToLive: 'PT1S'
  }
  parent: sb
}

resource topic1 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  name: 'topicName'
  properties: {
    defaultMessageTimeToLive: 'PT1S'
  }
  parent: sb
}

resource subscription1 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  name: 'subscriptionName'
  parent: topic1
}

resource rule1 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2024-01-01' = {
  name: 'rule1'
  properties: {
    filterType: 'CorrelationFilter'
  }
  parent: subscription1
}

output serviceBusEndpoint string = sb.properties.serviceBusEndpoint

output serviceBusHostName string = split(replace(sb.properties.serviceBusEndpoint, 'https://', ''), ':')[0]

output name string = sb.name
