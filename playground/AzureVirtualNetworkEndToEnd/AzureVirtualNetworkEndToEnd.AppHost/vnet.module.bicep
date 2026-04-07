@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param nat_outputs_id string

param container_apps_nsg_outputs_id string

param private_endpoints_nsg_outputs_id string

param sql_nsg_outputs_id string

resource vnet 'Microsoft.Network/virtualNetworks@2025-05-01' = {
  name: take('vnet-${uniqueString(resourceGroup().id)}', 64)
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.0.0.0/16'
      ]
    }
  }
  location: location
  tags: {
    'aspire-resource-name': 'vnet'
  }
}

resource container_apps 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  name: 'container-apps'
  properties: {
    addressPrefix: '10.0.0.0/23'
    delegations: [
      {
        properties: {
          serviceName: 'Microsoft.App/environments'
        }
        name: 'Microsoft.App/environments'
      }
    ]
    natGateway: {
      id: nat_outputs_id
    }
    networkSecurityGroup: {
      id: container_apps_nsg_outputs_id
    }
  }
  parent: vnet
}

resource private_endpoints 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  name: 'private-endpoints'
  properties: {
    addressPrefix: '10.0.2.0/27'
    networkSecurityGroup: {
      id: private_endpoints_nsg_outputs_id
    }
  }
  parent: vnet
  dependsOn: [
    container_apps
  ]
}

resource sql_aci_subnet 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  name: 'sql-aci-subnet'
  properties: {
    addressPrefix: '10.0.255.248/29'
    delegations: [
      {
        properties: {
          serviceName: 'Microsoft.ContainerInstance/containerGroups'
        }
        name: 'Microsoft.ContainerInstance/containerGroups'
      }
    ]
    networkSecurityGroup: {
      id: sql_nsg_outputs_id
    }
  }
  parent: vnet
  dependsOn: [
    private_endpoints
  ]
}

output container_apps_Id string = container_apps.id

output private_endpoints_Id string = private_endpoints.id

output sql_aci_subnet_Id string = sql_aci_subnet.id

output id string = vnet.id

output name string = vnet.name