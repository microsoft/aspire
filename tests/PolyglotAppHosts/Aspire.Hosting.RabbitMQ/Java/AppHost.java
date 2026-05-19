import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var rabbitmq = builder.addRabbitMQ("messaging");
        rabbitmq.withDataVolume();
        rabbitmq.withManagementPlugin();
        var rabbitmq2 = builder.addRabbitMQ("messaging2");
        rabbitmq2.withLifetime(ContainerLifetime.PERSISTENT);
        rabbitmq2.withDataVolume();
        rabbitmq2.withManagementPlugin(15673.0);
        // ---- Property access on RabbitMQServerResource ----
        var _endpoint = rabbitmq.primaryEndpoint();
        var _mgmtEndpoint = rabbitmq.managementEndpoint();
        var _host = rabbitmq.host();
        var _port = rabbitmq.port();
        var _uri = rabbitmq.uriExpression();
        var _userName = rabbitmq.userNameReference();
        var _cstr = rabbitmq.connectionStringExpression();
        builder.build().run();
    }
