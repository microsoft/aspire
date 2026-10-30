# Aspire AppHost sample for Azure Web PubSub

## Usage

Start Docker or Podman, then run this AppHost. Local development uses the Web PubSub emulator and does not require an Azure subscription.

Open the `webfrontend` HTTP endpoint from the Aspire dashboard. Open two browser tabs to exchange chat messages. The page also displays periodic notifications and a system message when a client connects, sent by the app's `connected` event handler.

When publishing to Azure, set `SubscriptionId` and `Location` in [appsettings.json](./appsettings.json). The AppHost provisions Azure Web PubSub and configures an HTTPS event handler.
