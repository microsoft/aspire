const [containerName = "db-server", command = "pg_isready -U postgres", workingDirectory = "/"] = process.argv.slice(2);

console.log("scenario=#10301 container-exec");
console.log(`container=${containerName}`);
console.log(`working-directory=${workingDirectory}`);
console.log(`command=${command}`);
console.log(`docker-exec-shape=docker exec -w "${workingDirectory}" "${containerName}" ${command}`);

if (command.includes("fail")) {
    console.error("simulated-container-exec-failure");
    process.exit(3);
}
