package main

import (
	"log"

	"apphost/modules/aspire"
)

func main() {
	builder, err := aspire.CreateBuilder(nil)
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}

	storage := builder.AddAzureStorage("storage")
	if storage.Err() != nil {
		log.Fatalf(aspire.FormatError(storage.Err()))
	}

	vnet := builder.AddAzureVirtualNetwork("vnet")
	if vnet.Err() != nil {
		log.Fatalf(aspire.FormatError(vnet.Err()))
	}
	deploymentSubnet := vnet.AddSubnet("deployment-subnet", "10.0.1.0/24")
	if deploymentSubnet.Err() != nil {
		log.Fatalf(aspire.FormatError(deploymentSubnet.Err()))
	}
	aciSubnet := vnet.AddSubnet("aci-subnet", "10.0.2.0/29")
	if aciSubnet.Err() != nil {
		log.Fatalf(aspire.FormatError(aciSubnet.Err()))
	}

	sqlServer := builder.AddAzureSqlServer("sql")
	if sqlServer.Err() != nil {
		log.Fatalf(aspire.FormatError(sqlServer.Err()))
	}

	db := sqlServer.AddDatabase("mydb")
	if db.Err() != nil {
		log.Fatalf(aspire.FormatError(db.Err()))
	}

	db2 := sqlServer.AddDatabase("inventory", &aspire.AddDatabaseOptions{
		DatabaseName: aspire.StringPtr("inventorydb"),
	})
	if db2.Err() != nil {
		log.Fatalf(aspire.FormatError(db2.Err()))
	}
	db2.WithDefaultAzureSku()

	sqlServer.RunAsContainer(&aspire.RunAsContainerOptions{
		ConfigureContainer: func(container aspire.SqlServerServerResource) {},
	})
	if sqlServer.Err() != nil {
		log.Fatalf(aspire.FormatError(sqlServer.Err()))
	}

	sqlServer.WithAdminDeploymentScriptSubnet(deploymentSubnet)
	sqlServer.WithAdminDeploymentScriptStorage(storage)
	sqlServer.WithAdminDeploymentScriptSubnet(aciSubnet)

	_ = sqlServer.AddDatabase("analytics").WithDefaultAzureSku()

	_ = sqlServer.HostName()
	_ = sqlServer.Port()
	_ = sqlServer.UriExpression()
	_ = sqlServer.ConnectionStringExpression()
	_ = sqlServer.JdbcConnectionString()
	_ = sqlServer.FullyQualifiedDomainName()
	_ = sqlServer.NameOutputReference()
	_ = sqlServer.Id()
	_, _ = sqlServer.IsContainer()
	_, _ = sqlServer.Databases()
	_, _ = sqlServer.AzureSqlDatabases()

	if sqlServer.Err() != nil {
		log.Fatalf(aspire.FormatError(sqlServer.Err()))
	}

	_ = db.Parent()
	_ = db.ConnectionStringExpression()
	_, _ = db.DatabaseName()
	_, _ = db.IsContainer()
	_ = db.UriExpression()
	_ = db.JdbcConnectionString()

	if db.Err() != nil {
		log.Fatalf(aspire.FormatError(db.Err()))
	}

	app, err := builder.Build()
	if err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
	if err := app.Run(nil); err != nil {
		log.Fatalf(aspire.FormatError(err))
	}
}
