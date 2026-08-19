extension radius

resource recipepack 'Radius.Core/recipePacks@2025-08-01-preview' = {
  name: 'default'
  properties: {
    recipes: {
      'Radius.Data/redisCaches': {
        kind: 'bicep'
        source: 'ghcr.io/radius-project/kube-recipes/rediscaches:latest'
      }
      'Radius.Compute/containers': {
        kind: 'bicep'
        source: 'ghcr.io/radius-project/kube-recipes/containers:latest'
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

resource renamed_cache 'Radius.Data/redisCaches@2025-08-01-preview' = {
  name: 'cache'
  properties: {
    application: app.id
    environment: myenv.id
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
            value: '${renamed_cache.properties.host}:${renamed_cache.properties.port},password='
          }
          CACHE_HOST: {
            value: renamed_cache.properties.host
          }
          CACHE_PORT: {
            value: string(renamed_cache.properties.port)
          }
          CACHE_PASSWORD: {
            value: ''
          }
          CACHE_URI: {
            value: 'redis://:@${renamed_cache.properties.host}:${renamed_cache.properties.port}'
          }
        }
      }
    }
    application: app.id
    environment: myenv.id
    connections: {
      cache: {
        source: renamed_cache.id
      }
    }
  }
}