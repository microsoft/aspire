const fs = require("node:fs");

const [backupPath = "sample-backup.dump", containerName = "db-server", databaseName = "mainDb"] = process.argv.slice(2);

console.log("scenario=#12906/#7786 backup-restore");
console.log(`backup-path=${backupPath}`);
console.log(`container=${containerName}`);
console.log(`database=${databaseName}`);

if (!fs.existsSync(backupPath)) {
    console.error(`backup-missing=${backupPath}`);
    process.exit(2);
}

const backupSize = fs.statSync(backupPath).size;
console.log(`backup-size-bytes=${backupSize}`);
console.log(`docker-cp-shape=docker cp "${backupPath}" "${containerName}:/tmp/restore.dump"`);
console.log(`docker-exec-shape=docker exec "${containerName}" pg_restore -U postgres -d "${databaseName}" /tmp/restore.dump`);
