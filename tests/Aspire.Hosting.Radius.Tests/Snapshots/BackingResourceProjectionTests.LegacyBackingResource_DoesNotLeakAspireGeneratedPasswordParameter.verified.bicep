extension radius

resource recipepack 'Radius.Core/recipePacks@2025-08-01-preview' = {
  name: 'default'
  properties: {
    recipes: {
      'Radius.Compute/containers': {
        recipeKind: 'bicep'
        recipeLocation: 'ghcr.io/radius-project/kube-recipes/containers:latest'
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
      'Applications.Datastores/redisCaches': {
        default: {
          templateKind: 'bicep'
          templatePath: 'ghcr.io/radius-project/recipes/local-dev/rediscaches:latest'
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

resource cache 'Applications.Datastores/redisCaches@2023-10-01-preview' = {
  name: 'cache'
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
        image: 'myapp/api:latest'
        env: {
          ConnectionStrings__cache: {
            value: '${cache.properties.host}:${cache.properties.port},password='
          }
          CACHE_HOST: {
            value: cache.properties.host
          }
          CACHE_PORT: {
            value: string(cache.properties.port)
          }
          CACHE_PASSWORD: {
            value: ''
          }
          CACHE_URI: {
            value: 'redis://:@${cache.properties.host}:${cache.properties.port}'
          }
        }
      }
    }
    application: app.id
    environment: myenv.id
    connections: {
      cache: {
        source: cache.id
      }
    }
  }
}