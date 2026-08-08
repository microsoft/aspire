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
      'Applications.Datastores/mongoDatabases': {
        default: {
          templateKind: 'bicep'
          templatePath: 'ghcr.io/radius-project/recipes/local-dev/mongodatabases:latest'
        }
      }
      'Applications.Messaging/rabbitMQQueues': {
        default: {
          templateKind: 'bicep'
          templatePath: 'ghcr.io/radius-project/recipes/local-dev/rabbitmqqueues:latest'
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

resource mongo 'Applications.Datastores/mongoDatabases@2023-10-01-preview' = {
  name: 'mongo'
  properties: {
    application: app_legacy.id
    environment: myenv_legacy.id
  }
}

resource rabbit 'Applications.Messaging/rabbitMQQueues@2023-10-01-preview' = {
  name: 'rabbit'
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
          ConnectionStrings__mongo: {
            value: 'mongodb://${uriComponent(mongo.properties.username)}:${uriComponent(mongo.listSecrets().password)}@${mongo.properties.host}:${mongo.properties.port}/?authSource=admin&authMechanism=SCRAM-SHA-256'
          }
          MONGO_HOST: {
            value: mongo.properties.host
          }
          MONGO_PORT: {
            value: string(mongo.properties.port)
          }
          MONGO_USERNAME: {
            value: mongo.properties.username
          }
          MONGO_PASSWORD: {
            value: mongo.listSecrets().password
          }
          MONGO_AUTHENTICATIONDATABASE: {
            value: 'admin'
          }
          MONGO_AUTHENTICATIONMECHANISM: {
            value: 'SCRAM-SHA-256'
          }
          MONGO_URI: {
            value: 'mongodb://${uriComponent(mongo.properties.username)}:${uriComponent(mongo.listSecrets().password)}@${mongo.properties.host}:${mongo.properties.port}/?authSource=admin&authMechanism=SCRAM-SHA-256'
          }
          ConnectionStrings__rabbit: {
            value: 'amqp://${uriComponent(rabbit.properties.username)}:${uriComponent(rabbit.listSecrets().password)}@${rabbit.properties.host}:${rabbit.properties.port}'
          }
          RABBIT_HOST: {
            value: rabbit.properties.host
          }
          RABBIT_PORT: {
            value: string(rabbit.properties.port)
          }
          RABBIT_USERNAME: {
            value: rabbit.properties.username
          }
          RABBIT_PASSWORD: {
            value: rabbit.listSecrets().password
          }
          RABBIT_URI: {
            value: 'amqp://${uriComponent(rabbit.properties.username)}:${uriComponent(rabbit.listSecrets().password)}@${rabbit.properties.host}:${rabbit.properties.port}'
          }
        }
      }
    }
    application: app.id
    environment: myenv.id
    connections: {
      mongo: {
        source: mongo.id
      }
      rabbit: {
        source: rabbit.id
      }
    }
  }
}