let input = "";

process.stdin.setEncoding("utf8");
process.stdin.on("data", chunk => input += chunk);
process.stdin.on("end", () => {
    console.log(`ts-process-command-arg=${process.argv[2]}`);
    console.log(`ts-process-command-env=${process.env.TS_PROCESS_COMMAND_SAMPLE}`);
    console.log(`ts-process-command-stdin=${input.trim()}`);
});
