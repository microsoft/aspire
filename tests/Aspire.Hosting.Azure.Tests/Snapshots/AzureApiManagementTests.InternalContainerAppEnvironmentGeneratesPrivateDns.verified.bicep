@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param vnet_outputs_name string

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_static_ip string

resource vnet 'Microsoft.Network/virtualNetworks@2025-05-01' existing = {
  name: vnet_outputs_name
}

resource env_private_dns_privateDns 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: env_outputs_azure_container_apps_environment_default_domain
  location: 'global'
}

resource env_private_dns_wildcard 'Microsoft.Network/privateDnsZones/A@2024-06-01' = {
  name: '*'
  properties: {
    ttl: 3600
    aRecords: [
      {
        ipv4Address: env_outputs_azure_container_apps_environment_static_ip
      }
    ]
  }
  parent: env_private_dns_privateDns
}

resource env_private_dns_vnetLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  name: 'env-private-dns-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
  parent: env_private_dns_privateDns
}