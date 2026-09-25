// Virtual network
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

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

resource app_service_subnet 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  name: 'app-service-subnet'
  properties: {
    addressPrefix: '10.0.0.0/24'
    delegations: [
      {
        properties: {
          serviceName: 'Microsoft.Web/serverFarms'
        }
        name: 'Microsoft.Web/serverFarms'
      }
    ]
  }
  parent: vnet
}

output app_service_subnet_Id string = app_service_subnet.id

output id string = vnet.id

output name string = vnet.name

// App Service environment
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string = ''

param tags object = { }

param env_acr_outputs_name string

param vnet_outputs_app_service_subnet_id string

resource env_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('env_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
  tags: tags
}

resource env_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: env_acr_outputs_name
}

resource env_acr_env_mi_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(env_acr.id, env_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: env_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: env_acr
}

resource env_asplan 'Microsoft.Web/serverfarms@2025-03-01' = {
  name: take('envasplan-${uniqueString(resourceGroup().id)}', 60)
  location: location
  properties: {
    perSiteScaling: true
    reserved: true
  }
  kind: 'Linux'
  sku: {
    name: 'P0V3'
    tier: 'Premium'
  }
}

resource env_contributor_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('env_contributor_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

resource env_ra 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, env_contributor_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7'))
  properties: {
    principalId: env_contributor_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7')
    principalType: 'ServicePrincipal'
  }
}

resource dashboard 'Microsoft.Web/sites@2025-03-01' = {
  name: take('${toLower('env')}-${toLower('aspiredashboard')}-${uniqueString(resourceGroup().id)}', 60)
  location: location
  properties: {
    serverFarmId: env_asplan.id
    siteConfig: {
      numberOfWorkers: 1
      linuxFxVersion: 'ASPIREDASHBOARD|1.0'
      acrUseManagedIdentityCreds: true
      acrUserManagedIdentityID: env_mi.properties.clientId
      appSettings: [
        {
          name: 'DASHBOARD__FRONTEND__AUTHMODE'
          value: 'Unsecured'
        }
        {
          name: 'DASHBOARD__OTLP__AUTHMODE'
          value: 'Unsecured'
        }
        {
          name: 'DASHBOARD__OTLP__SUPPRESSUNSECUREDTELEMETRYMESSAGE'
          value: 'true'
        }
        {
          name: 'DASHBOARD__RESOURCESERVICECLIENT__AUTHMODE'
          value: 'Unsecured'
        }
        {
          name: 'DASHBOARD__UI__DISABLEIMPORT'
          value: 'true'
        }
        {
          name: 'WEBSITES_PORT'
          value: '5000'
        }
        {
          name: 'HTTP20_ONLY_PORT'
          value: '4317'
        }
        {
          name: 'WEBSITE_START_SCM_WITH_PRELOAD'
          value: 'true'
        }
        {
          name: 'AZURE_CLIENT_ID'
          value: env_contributor_mi.properties.clientId
        }
        {
          name: 'ALLOWED_MANAGED_IDENTITIES'
          value: env_mi.properties.clientId
        }
        {
          name: 'ASPIRE_ENVIRONMENT_NAME'
          value: 'env'
        }
      ]
      alwaysOn: true
      http20Enabled: true
      http20ProxyFlag: 1
    }
    virtualNetworkSubnetId: vnet_outputs_app_service_subnet_id
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${env_contributor_mi.id}': { }
    }
  }
  kind: 'app,linux,aspiredashboard'
}

output name string = env_asplan.name

output planId string = env_asplan.id

output webSiteSuffix string = uniqueString(resourceGroup().id)

output AZURE_CONTAINER_REGISTRY_NAME string = env_acr.name

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = env_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = env_mi.id

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID string = env_mi.properties.clientId

output AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_ID string = env_contributor_mi.id

output AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_PRINCIPAL_ID string = env_contributor_mi.properties.principalId

output AZURE_APP_SERVICE_DASHBOARD_URI string = 'https://${take('${toLower('env')}-${toLower('aspiredashboard')}-${uniqueString(resourceGroup().id)}', 60)}.azurewebsites.net'

// App Service website
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_registry_endpoint string

param env_outputs_planid string

param env_outputs_azure_container_registry_managed_identity_id string

param env_outputs_azure_container_registry_managed_identity_client_id string

param api_containerimage string

param vnet_outputs_app_service_subnet_id string

param api_containerport string

param env_outputs_azure_app_service_dashboard_uri string

param env_outputs_azure_website_contributor_managed_identity_id string

param env_outputs_azure_website_contributor_managed_identity_principal_id string

resource mainContainer 'Microsoft.Web/sites/sitecontainers@2025-03-01' = {
  name: 'main'
  properties: {
    authType: 'UserAssigned'
    image: api_containerimage
    isMain: true
    targetPort: api_containerport
    userManagedIdentityClientId: env_outputs_azure_container_registry_managed_identity_client_id
  }
  parent: webapp
}

resource webapp 'Microsoft.Web/sites@2025-03-01' = {
  name: take('${toLower('api')}-${uniqueString(resourceGroup().id)}', 60)
  location: location
  properties: {
    serverFarmId: env_outputs_planid
    siteConfig: {
      numberOfWorkers: 30
      linuxFxVersion: 'SITECONTAINERS'
      acrUseManagedIdentityCreds: true
      acrUserManagedIdentityID: env_outputs_azure_container_registry_managed_identity_client_id
      appSettings: [
        {
          name: 'WEBSITES_PORT'
          value: api_containerport
        }
        {
          name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY'
          value: 'in_memory'
        }
        {
          name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
          value: 'true'
        }
        {
          name: 'HTTP_PORTS'
          value: api_containerport
        }
        {
          name: 'ASPIRE_ENVIRONMENT_NAME'
          value: 'env'
        }
        {
          name: 'OTEL_SERVICE_NAME'
          value: 'api'
        }
        {
          name: 'OTEL_EXPORTER_OTLP_PROTOCOL'
          value: 'grpc'
        }
        {
          name: 'OTEL_EXPORTER_OTLP_ENDPOINT'
          value: 'http://localhost:6001'
        }
        {
          name: 'WEBSITE_ENABLE_ASPIRE_OTEL_SIDECAR'
          value: 'true'
        }
        {
          name: 'OTEL_COLLECTOR_URL'
          value: env_outputs_azure_app_service_dashboard_uri
        }
        {
          name: 'OTEL_CLIENT_ID'
          value: env_outputs_azure_container_registry_managed_identity_client_id
        }
      ]
    }
    virtualNetworkSubnetId: vnet_outputs_app_service_subnet_id
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}

resource api_website_ra 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(webapp.id, env_outputs_azure_website_contributor_managed_identity_id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'de139f84-1756-47ae-9be6-808fbbe84772'))
  properties: {
    principalId: env_outputs_azure_website_contributor_managed_identity_principal_id
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'de139f84-1756-47ae-9be6-808fbbe84772')
    principalType: 'ServicePrincipal'
  }
  scope: webapp
}

resource slotConfigNames 'Microsoft.Web/sites/config@2025-03-01' = {
  name: 'slotConfigNames'
  properties: {
    appSettingNames: [
      'OTEL_SERVICE_NAME'
    ]
  }
  parent: webapp
}