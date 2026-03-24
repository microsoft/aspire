import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var adminPassword = builder.addParameter("seq-admin-password", true);
        var seq = builder.addSeq("seq", adminPassword, 5341.0);
        seq.withDataVolume();
        seq.withDataVolume(new WithDataVolumeOptions().name("seq-data").isReadOnly(false));
        seq.withDataBindMount("./seq-data", true);
        // ---- Property access on SeqResource ----
        var _endpoint = seq.primaryEndpoint();
        var _host = seq.host();
        var _port = seq.port();
        var _uri = seq.uriExpression();
        var _cstr = seq.connectionStringExpression();
        builder.build().run();
    }
