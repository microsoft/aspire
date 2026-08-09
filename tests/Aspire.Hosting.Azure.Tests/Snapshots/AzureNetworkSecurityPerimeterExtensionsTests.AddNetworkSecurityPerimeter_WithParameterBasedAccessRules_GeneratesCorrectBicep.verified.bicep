@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param inboundaddressprefix_value string

param allowedsubscription_value string

param outboundfqdn_value string

resource my_nsp 'Microsoft.Network/networkSecurityPerimeters@2025-05-01' = {
  name: take('mynsp${uniqueString(resourceGroup().id)}', 24)
  location: location
  tags: {
    'aspire-resource-name': 'my-nsp'
  }
}

resource my_nsp_profile 'Microsoft.Network/networkSecurityPerimeters/profiles@2025-05-01' = {
  name: 'defaultProfile'
  parent: my_nsp
}

resource my_nsp_profile_allow_my_ip 'Microsoft.Network/networkSecurityPerimeters/profiles/accessRules@2025-05-01' = {
  name: 'allow-my-ip'
  properties: {
    addressPrefixes: [
      '203.0.113.0/24'
      inboundaddressprefix_value
    ]
    direction: 'Inbound'
  }
  parent: my_nsp_profile
}

resource my_nsp_profile_allow_subscription 'Microsoft.Network/networkSecurityPerimeters/profiles/accessRules@2025-05-01' = {
  name: 'allow-subscription'
  properties: {
    direction: 'Inbound'
    subscriptions: [
      {
        id: '/subscriptions/00000000-0000-0000-0000-000000000001'
      }
      {
        id: allowedsubscription_value
      }
    ]
  }
  parent: my_nsp_profile
}

resource my_nsp_profile_allow_outbound_fqdn 'Microsoft.Network/networkSecurityPerimeters/profiles/accessRules@2025-05-01' = {
  name: 'allow-outbound-fqdn'
  properties: {
    direction: 'Outbound'
    fullyQualifiedDomainNames: [
      '*.blob.core.windows.net'
      outboundfqdn_value
    ]
  }
  parent: my_nsp_profile
}

output id string = my_nsp.id

output name string = my_nsp.name