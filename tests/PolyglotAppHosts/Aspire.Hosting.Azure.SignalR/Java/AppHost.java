import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var signalr = builder.addAzureSignalR("signalr");
        signalr.runAsEmulator();
        builder.build().run();
    }
