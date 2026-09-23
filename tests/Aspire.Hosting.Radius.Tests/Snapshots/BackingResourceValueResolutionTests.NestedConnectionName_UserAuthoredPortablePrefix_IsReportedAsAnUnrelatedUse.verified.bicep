extension radius

resource recipepack 'Radius.Core/recipePacks@2025-08-01-preview' = {
  name: 'default'
  properties: {
    recipes: {
      'Radius.Compute/containers': {
        kind: 'bicep'
        source: 'ghcr.io/radius-project/kube-recipes/containers:latest'
      }
      'Radius.Security/secrets': {
        kind: 'bicep'
        source: 'ghcr.io/radius-project/kube-recipes/secrets:latest'
      }
    }
  }
}

resource myenv 'Radius.Core/environments@2025-08-01-preview' = {
  name: 'myenv'
  properties: {
    recipePacks: [
      recipepack.id
    ]
    providers: {
      kubernetes: {
        namespace: 'default'
      }
    }
  }
}

resource app 'Radius.Core/applications@2025-08-01-preview' = {
  name: 'app'
  properties: {
    environment: myenv.id
  }
}

resource myenv_legacy 'Applications.Core/environments@2023-10-01-preview' = {
  name: 'myenv'
  properties: {
    compute: {
      kind: 'kubernetes'
      namespace: 'default'
    }
    recipes: {
      'Applications.Datastores/sqlDatabases': {
        default: {
          templateKind: 'bicep'
          templatePath: 'ghcr.io/radius-project/recipes/local-dev/sqldatabases:latest'
        }
      }
    }
  }
}

resource app_legacy 'Applications.Core/applications@2023-10-01-preview' = {
  name: 'app'
  properties: {
    environment: myenv_legacy.id
  }
}

resource api_env_secret 'Radius.Security/secrets@2025-08-01-preview' = {
  name: 'api-env-secret'
  properties: {
    environment: myenv.id
    application: app.id
    data: {
      ConnectionStrings__db_primary: {
        value: 'Server=${sql.properties.server},${sql.properties.port};User ID=sa;Password=${sql.listSecrets().password};TrustServerCertificate=true'
        encoding: 'string'
      }
      DB_PRIMARY_PASSWORD: {
        value: sql.listSecrets().password
        encoding: 'string'
      }
    }
  }
}

resource sql 'Applications.Datastores/sqlDatabases@2023-10-01-preview' = {
  name: 'sql'
  properties: {
    application: app_legacy.id
    environment: myenv_legacy.id
  }
}

resource api 'Radius.Compute/containers@2025-08-01-preview' = {
  name: 'api'
  properties: {
    containers: {
      api: {
        image: 'myapp/api:1.0'
        env: {
          ConnectionStrings__db_primary: {
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'ConnectionStrings__db_primary'
              }
            }
          }
          DB_PRIMARY_PASSWORD: {
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'DB_PRIMARY_PASSWORD'
              }
            }
          }
        }
      }
    }
    application: app.id
    environment: myenv.id
    connections: {
      sql: {
        source: sql.id
      }
    }
  }
}