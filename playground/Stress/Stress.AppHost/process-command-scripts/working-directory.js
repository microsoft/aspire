const fs = require("node:fs");

const projectFiles = fs.readdirSync(process.cwd())
    .filter(entry => entry.endsWith(".csproj"))
    .sort();

for (const projectFile of projectFiles) {
    console.log(projectFile);
}
