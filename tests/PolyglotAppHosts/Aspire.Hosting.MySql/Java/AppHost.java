import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();
        var rootPassword = builder.addParameter("mysql-root-password", true);
        var mysql = builder.addMySql("mysql", new AddMySqlOptions().password(rootPassword).port(3306.0));
        mysql
            .withPassword(rootPassword)
            .withDataVolume(new WithDataVolumeOptions().name("mysql-data"))
            .withDataBindMount(".", true)
            .withInitFiles(".");
        mysql.withPhpMyAdmin(new WithPhpMyAdminOptions().containerName("phpmyadmin").configureContainer((container) -> {
                container.withHostPort(8080.0);
            }));
        var db = mysql.addDatabase("appdb", "appdb");
        db.withCreationScript("CREATE DATABASE IF NOT EXISTS appdb;");
        // ---- Property access on MySqlServerResource ----
        var _endpoint = mysql.primaryEndpoint();
        var _host = mysql.host();
        var _port = mysql.port();
        var _uri = mysql.uriExpression();
        var _jdbc = mysql.jdbcConnectionString();
        var _cstr = mysql.connectionStringExpression();
        var _databases = mysql.databases();
        builder.build().run();
    }
