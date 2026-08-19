extension radius

@secure()
param rabbit_password string

param rabbituser string

resource recipepack 'Radius.Core/recipePacks@2025-08-01-preview' = {
  name: 'default'
  properties: {
    recipes: {
      'Radius.Messaging/rabbitMQ': {
        kind: 'bicep'
        source: 'ghcr.io/radius-project/kube-recipes/rabbitmq:latest'
      }
      'Radius.Security/secrets': {
        kind: 'bicep'
        source: 'ghcr.io/radius-project/kube-recipes/secrets:latest'
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
    }
  }
}

resource app_legacy 'Applications.Core/applications@2023-10-01-preview' = {
  name: 'app'
  properties: {
    environment: myenv_legacy.id
  }
}

resource rabbit_password_secret 'Radius.Security/secrets@2025-08-01-preview' = {
  name: 'rabbit-password-secret'
  properties: {
    environment: myenv.id
    application: app.id
    data: {
      password: {
        value: rabbit_password
        encoding: 'string'
      }
    }
  }
}

resource api_env_secret 'Radius.Security/secrets@2025-08-01-preview' = {
  name: 'api-env-secret'
  properties: {
    environment: myenv.id
    application: app.id
    data: {
      ConnectionStrings__mongo: {
        value: 'mongodb://${uriComponent(mongo.properties.username)}:${uriComponent(mongo.listSecrets().password)}@${mongo.properties.host}:${mongo.properties.port}/?authSource=admin&authMechanism=SCRAM-SHA-256'
        encoding: 'string'
      }
      MONGO_PASSWORD: {
        value: mongo.listSecrets().password
        encoding: 'string'
      }
      MONGO_URI: {
        value: 'mongodb://${uriComponent(mongo.properties.username)}:${uriComponent(mongo.listSecrets().password)}@${mongo.properties.host}:${mongo.properties.port}/?authSource=admin&authMechanism=SCRAM-SHA-256'
        encoding: 'string'
      }
      ConnectionStrings__rabbit: {
        value: 'amqp://${uriComponent(rabbituser)}:${uriComponent(rabbit_password)}@${rabbit.properties.host}:${rabbit.properties.port}'
        encoding: 'string'
      }
      RABBIT_PASSWORD: {
        value: rabbit_password
        encoding: 'string'
      }
      RABBIT_URI: {
        value: 'amqp://${uriComponent(rabbituser)}:${uriComponent(rabbit_password)}@${rabbit.properties.host}:${rabbit.properties.port}'
        encoding: 'string'
      }
    }
  }
}

resource mongo 'Applications.Datastores/mongoDatabases@2023-10-01-preview' = {
  name: 'mongo'
  properties: {
    application: app_legacy.id
    environment: myenv_legacy.id
  }
}

resource rabbit 'Radius.Messaging/rabbitMQ@2025-08-01-preview' = {
  name: 'rabbit'
  properties: {
    application: app.id
    environment: myenv.id
    username: rabbituser
    password: rabbit_password_secret.id
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
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'ConnectionStrings__mongo'
              }
            }
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
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'MONGO_PASSWORD'
              }
            }
          }
          MONGO_AUTHENTICATIONDATABASE: {
            value: 'admin'
          }
          MONGO_AUTHENTICATIONMECHANISM: {
            value: 'SCRAM-SHA-256'
          }
          MONGO_URI: {
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'MONGO_URI'
              }
            }
          }
          ConnectionStrings__rabbit: {
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'ConnectionStrings__rabbit'
              }
            }
          }
          RABBIT_HOST: {
            value: rabbit.properties.host
          }
          RABBIT_PORT: {
            value: string(rabbit.properties.port)
          }
          RABBIT_USERNAME: {
            value: rabbituser
          }
          RABBIT_PASSWORD: {
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'RABBIT_PASSWORD'
              }
            }
          }
          RABBIT_URI: {
            valueFrom: {
              secretKeyRef: {
                secretName: 'api-env-secret'
                key: 'RABBIT_URI'
              }
            }
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