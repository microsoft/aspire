import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var signalr = builder.addAzureSignalR("signalr");
        signalr.configureInfrastructure(infrastructure -> {
            var service = infrastructure.getSignalRService();
            service.setDisableLocalAuth(true);
            var _disableLocalAuth = service.disableLocalAuth();
        });
        signalr.runAsEmulator();
        builder.build().run();
    }
