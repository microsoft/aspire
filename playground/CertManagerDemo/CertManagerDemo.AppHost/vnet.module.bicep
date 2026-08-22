@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource vnet 'Microsoft.Network/virtualNetworks@2025-05-01' = {
  name: take('vnet-${uniqueString(resourceGroup().id)}', 64)
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.100.0.0/16'
      ]
    }
  }
  location: location
  tags: {
    'aspire-resource-name': 'vnet'
  }
}

resource aks_nodes 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  name: 'aks-nodes'
  properties: {
    addressPrefix: '10.100.0.0/22'
  }
  parent: vnet
}

resource alb_public 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  name: 'alb-public'
  properties: {
    addressPrefix: '10.100.4.0/24'
    delegations: [
      {
        properties: {
          serviceName: 'Microsoft.ServiceNetworking/trafficControllers'
        }
        name: 'Microsoft.ServiceNetworking/trafficControllers'
      }
    ]
  }
  parent: vnet
  dependsOn: [
    aks_nodes
  ]
}

resource alb_admin 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  name: 'alb-admin'
  properties: {
    addressPrefix: '10.100.5.0/24'
    delegations: [
      {
        properties: {
          serviceName: 'Microsoft.ServiceNetworking/trafficControllers'
        }
        name: 'Microsoft.ServiceNetworking/trafficControllers'
      }
    ]
  }
  parent: vnet
  dependsOn: [
    alb_public
  ]
}

output aks_nodes_Id string = aks_nodes.id

output alb_public_Id string = alb_public.id

output alb_admin_Id string = alb_admin.id

output id string = vnet.id

output name string = vnet.name