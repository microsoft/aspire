extension radius

resource recipepack 'Radius.Core/recipePacks@2025-08-01-preview' = {
  name: 'default'
  properties: {
    recipes: {
      'Radius.Data/redisCaches': {
        kind: 'bicep'
        source: 'ghcr.io/radius-project/kube-recipes/rediscaches:ebdeec9509036f2b2f271e41661e6fcfe45eda89'
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

resource cache 'Radius.Data/redisCaches@2025-08-01-preview' = {
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