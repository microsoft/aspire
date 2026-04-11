// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

internal static class AzureKustoReadWriteDatabaseResourceExtensions
{
    /// <summary>
    /// Gets the database creation script from the resource annotation if it exists. If not, creates the default database creation script.
    /// </summary>
    /// <param name="databaseResource">
    /// The <see cref="AzureKustoReadWriteDatabaseResource"/> resource to inspect for annotations.
    /// </param>
    /// <remarks>
    /// The default script is <code>.create database DATABASE_NAME persist (PERSISTENCE_PATH/DATABASE_NAME/md, PERSISTENCE_PATH/DATABASE_NAME/data) ifnotexists</code> where
    /// DATABASE_NAME is the database name and PERSISTENCE_PATH is <inheritdoc cref="AzureKustoEmulatorContainerDefaults.DefaultPersistencePath"/>.
    /// </remarks>
    /// <returns></returns>
    [AspireExport(Description = "Gets the KQL script used to create the database.")]
    public static string GetDatabaseCreationScript(this AzureKustoReadWriteDatabaseResource databaseResource)
    {
        var scriptAnnotation = databaseResource.Annotations.OfType<AzureKustoCreateDatabaseScriptAnnotation>().LastOrDefault();
        return scriptAnnotation?.Script ?? AzureKustoEmulatorContainerDefaults.DefaultCreateDatabaseCommand(databaseResource.DatabaseName);
    }
}
