// A direct Node workload keeps the terminal example portable without a shell or a compilation step.
import { createInterface } from 'node:readline';

const input = createInterface({ input: process.stdin, output: process.stdout });
console.log("Type a name, or 'quit' to end the terminal.");
input.setPrompt("Name: ");
input.prompt();
input.on('line', name => {
    if (name.trim() === 'quit') {
        input.close();
        return;
    }
    console.log(`Hello, ${name}!`);
    // Remain alive after automation completes so dialog completion cannot be mistaken for process exit.
    input.prompt();
});
