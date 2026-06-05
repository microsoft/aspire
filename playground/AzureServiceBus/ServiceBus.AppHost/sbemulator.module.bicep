@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sku string = 'Standard'

resource sbemulator 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: take('sbemulator-${uniqueString(resourceGroup().id)}', 50)
  location: location
  properties: {
    disableLocalAuth: true
  }
  sku: {
    name: sku
  }
  tags: {
    'aspire-resource-name': 'sbemulator'
  }
}

resource queueOne 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  name: 'queue1'
  properties: {
    deadLetteringOnMessageExpiration: false
  }
  parent: sbemulator
}

resource topicOne 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  name: 'topic1'
  parent: sbemulator
}

resource sub1 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  name: 'sub1'
  properties: {
    maxDeliveryCount: 10
  }
  parent: topicOne
}

resource app_prop_filter_1 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2024-01-01' = {
  name: 'app-prop-filter-1'
  properties: {
    correlationFilter: {
      correlationId: 'id1'
      messageId: 'msgid1'
      to: 'xyz'
      replyTo: 'someQueue'
      label: 'subject1'
      sessionId: 'session1'
      replyToSessionId: 'sessionId'
      contentType: 'application/text'
    }
    filterType: 'CorrelationFilter'
  }
  parent: sub1
}

output serviceBusEndpoint string = sbemulator.properties.serviceBusEndpoint

output serviceBusHostName string = split(replace(sbemulator.properties.serviceBusEndpoint, 'https://', ''), ':')[0]

output name string = sbemulator.name