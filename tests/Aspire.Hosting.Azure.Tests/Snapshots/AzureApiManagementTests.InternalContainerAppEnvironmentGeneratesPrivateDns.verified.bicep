@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_static_ip string

param vnet_outputs_id string

resource env_private_dns 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: env_outputs_azure_container_apps_environment_default_domain
  location: 'global'
  tags: {
    'aspire-resource-name': 'env-private-dns'
  }
}

resource env_private_dns_record0 'Microsoft.Network/privateDnsZones/A@2024-06-01' = {
  name: '*'
  properties: {
    ttl: 3600
    aRecords: [
      {
        ipv4Address: env_outputs_azure_container_apps_environment_static_ip
      }
    ]
  }
  parent: env_private_dns
}

resource vnet_link 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  name: 'vnet-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet_outputs_id
    }
  }
  tags: {
    'aspire-resource-name': 'env-private-dns-vnet-link'
  }
  parent: env_private_dns
}

output id string = env_private_dns.id

output name string = env_outputs_azure_container_apps_environment_default_domain