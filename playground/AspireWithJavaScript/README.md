# Integrating Angular, React, and Vue with Aspire

This sample demonstrates an approach for integrating several Node.js apps into an Aspire application, including a pnpm workspace.

The app consists of several services:

- **AspireJavaScript.MinimalApi**: This is an HTTP API that returns randomly generated weather forecast data.
- **AspireJavaScript.Angular**: This is an Angular app that consumes the weather forecast API and displays the data in a table.
- **AspireJavaScript.React**: This is a React app that consumes the weather forecast API and displays the data in a table.
- **AspireJavaScript.Vue**: This is a Vue app that consumes the weather forecast API and displays the data in a table.
- **AspireJavaScript.PnpmWorkspace**: This is a pnpm workspace that hosts a Vite React app and a shared workspace package.

## Pre-requisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- [Node.js](https://nodejs.org) - at least version 20.7.0
- **Optional** [Visual Studio 2022](https://visualstudio.microsoft.com/vs/)

### Experiencing the app

Before starting the app host run `npm install` in each JavaScript folder and `pnpm install` in `AspireJavaScript.PnpmWorkspace`.

Once the app is running, the Aspire dashboard will launch in your browser:

![Aspire dashboard](./images/aspire-dashboard.png)

From the dashboard, you can navigate to the Angular, React, and Vue apps:

**Angular**

![Angular app](./images/angular-app.png)

**React**

![React app](./images/react-app.png)

**Vue**

![Vue app](./images/vue-app.png)
