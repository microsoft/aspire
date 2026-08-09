let input = "";

process.stdin.setEncoding("utf8");
process.stdin.on("data", chunk => input += chunk);
process.stdin.on("end", () => {
    console.log(`stdin-${input.trim()}`);
});
