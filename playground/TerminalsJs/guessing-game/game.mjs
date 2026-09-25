// Tiny interactive REPL for the TerminalsJs playground.
//
// Read a guess at a time from stdin via readline, compare against a randomly
// chosen target, and print colored feedback until the player wins or quits.
// Designed to be attached to via WithTerminal() so the dashboard's xterm.js
// surface drives stdin/stdout over a PTY. Uses no external dependencies so
// the playground doesn't need npm install before `aspire run`.

import { createInterface } from 'node:readline';
import { stdin, stdout, exit } from 'node:process';

const Reset = '\u001b[0m';
const Bold = '\u001b[1m';
const Cyan = '\u001b[36m';
const Green = '\u001b[32m';
const Yellow = '\u001b[33m';
const Magenta = '\u001b[35m';
const Red = '\u001b[31m';

const min = 1;
const max = 100;

function pickTarget() {
    return Math.floor(Math.random() * (max - min + 1)) + min;
}

function printBanner(resourceName) {
    stdout.write('\n');
    stdout.write(`${Cyan}┌────────────────────────────────────────────────┐${Reset}\n`);
    stdout.write(`${Cyan}│${Reset} ${Bold}Aspire WithTerminal() — JS Guessing Game${Reset}      ${Cyan}│${Reset}\n`);
    stdout.write(`${Cyan}│${Reset} resource: ${Bold}${Magenta}${resourceName.padEnd(15)}${Reset} node ${process.version.padEnd(8)} ${Cyan}│${Reset}\n`);
    stdout.write(`${Cyan}└────────────────────────────────────────────────┘${Reset}\n`);
    stdout.write(`I'm thinking of a number between ${Bold}${min}${Reset} and ${Bold}${max}${Reset}.\n`);
    stdout.write(`Type a guess and press enter. ${Bold}help${Reset} for commands, ${Bold}quit${Reset} to exit.\n\n`);
}

function printHelp() {
    stdout.write(`${Bold}Commands:${Reset}\n`);
    stdout.write(`  ${Cyan}<number>${Reset}    Submit a guess between ${min} and ${max}\n`);
    stdout.write(`  ${Cyan}new${Reset}         Start a new game with a fresh target\n`);
    stdout.write(`  ${Cyan}cheat${Reset}       Reveal the target (no fun, but useful for debugging)\n`);
    stdout.write(`  ${Cyan}help${Reset}        Show this help\n`);
    stdout.write(`  ${Cyan}quit${Reset}        Exit\n`);
}

const resourceName = process.env.ASPIRE_RESOURCE_NAME ?? 'guessing-game';
let target = pickTarget();
let attempts = 0;

printBanner(resourceName);

const rl = createInterface({ input: stdin, output: stdout, terminal: false });

stdout.write(`${Bold}${Magenta}guess${Reset}${Cyan}>${Reset} `);

rl.on('line', (line) => {
    const trimmed = line.trim().toLowerCase();

    if (trimmed.length === 0) {
        stdout.write(`${Bold}${Magenta}guess${Reset}${Cyan}>${Reset} `);
        return;
    }

    if (trimmed === 'quit' || trimmed === 'exit') {
        stdout.write(`${Yellow}Bye!${Reset}\n`);
        rl.close();
        exit(0);
    }

    if (trimmed === 'help' || trimmed === '?') {
        printHelp();
        stdout.write(`${Bold}${Magenta}guess${Reset}${Cyan}>${Reset} `);
        return;
    }

    if (trimmed === 'new') {
        target = pickTarget();
        attempts = 0;
        stdout.write(`${Green}New game started. Range is ${min}-${max}.${Reset}\n`);
        stdout.write(`${Bold}${Magenta}guess${Reset}${Cyan}>${Reset} `);
        return;
    }

    if (trimmed === 'cheat') {
        stdout.write(`${Yellow}(target is ${target})${Reset}\n`);
        stdout.write(`${Bold}${Magenta}guess${Reset}${Cyan}>${Reset} `);
        return;
    }

    const guess = Number.parseInt(trimmed, 10);
    if (!Number.isFinite(guess) || guess < min || guess > max) {
        stdout.write(`${Red}Not a number between ${min} and ${max}.${Reset} Type ${Bold}help${Reset} for commands.\n`);
        stdout.write(`${Bold}${Magenta}guess${Reset}${Cyan}>${Reset} `);
        return;
    }

    attempts++;

    if (guess === target) {
        stdout.write(`${Green}${Bold}🎉 Got it!${Reset} ${target} in ${attempts} attempt${attempts === 1 ? '' : 's'}.\n`);
        stdout.write(`${Yellow}Starting a new game.${Reset}\n`);
        target = pickTarget();
        attempts = 0;
    } else if (guess < target) {
        stdout.write(`${Cyan}↑ higher${Reset} (attempts: ${attempts})\n`);
    } else {
        stdout.write(`${Cyan}↓ lower${Reset} (attempts: ${attempts})\n`);
    }

    stdout.write(`${Bold}${Magenta}guess${Reset}${Cyan}>${Reset} `);
});

rl.on('close', () => {
    // stdin EOF — PTY closed.
    exit(0);
});
