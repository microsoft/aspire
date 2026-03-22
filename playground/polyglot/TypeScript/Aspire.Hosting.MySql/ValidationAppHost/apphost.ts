import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();
const rootPassword = await builder.addParameter('mysql-root-password', { secret: true });
const mysql = await builder.addMySql('mysql', { password: rootPassword, port: 3306 });

await mysql
    .withPassword(rootPassword)
    .withDataVolume({ name: 'mysql-data' })
    .withDataBindMount('.', { isReadOnly: true })
    .withInitFiles('.');

await mysql.withPhpMyAdmin({
    containerName: 'phpmyadmin',
    configureContainer: async (container) => {
        await container.withHostPort({ port: 8080 });
    }
});

const db = await mysql.addDatabase('appdb', { databaseName: 'appdb' });
await db.withCreationScript('CREATE DATABASE IF NOT EXISTS appdb;');

// ---- Property access on MySqlServerResource ----
const _endpoint = await mysql.primaryEndpoint.get();
const _host = await mysql.host.get();
const _port = await mysql.port.get();
const _uri = await mysql.uriExpression.get();
const _jdbc = await mysql.jdbcConnectionString.get();

const _cstr = await mysql.connectionStringExpression.get();
const _databases = mysql.databases;
await builder.build().run();
