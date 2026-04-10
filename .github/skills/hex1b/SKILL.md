---
name: hex1b
description: CLI tool for automating any terminal application — TUI apps, shells, CLI tools, REPLs, and more. Use when you need to launch a process in a virtual terminal, capture its screen output, inject keystrokes or mouse input, take screenshots, record sessions, or assert on what's visible.
---

# Hex1b CLI Skill

The `dotnet hex1b` CLI tool lets you automate **any terminal application** — TUI apps,
interactive CLIs, shells, REPLs, curses programs, or anything else that runs in a terminal.
It wraps arbitrary processes in a headless virtual terminal, giving you programmatic control
over screen capture, input injection, and content assertions.

## Installation

```bash
# Install as a global tool
dotnet tool install -g Hex1b.Tool

# Or as a local tool (requires a tool manifest)
dotnet new tool-manifest   # if no manifest exists yet
dotnet tool install --local Hex1b.Tool
dotnet tool restore        # to restore from an existing manifest
```

## Concepts

A **terminal** is a headless virtual terminal managed by Hex1b. Any process that runs in a
terminal emulator can be launched inside one. Terminals are identified by a short numeric ID
(the process ID). Use a prefix if unambiguous.

All commands support `--json` for machine-readable output.

---

## How to Launch a Process in a Virtual Terminal

Start any command in a headless terminal. This is the entry point for all automation.

```bash
# Start the default interactive shell
# (PowerShell on Windows, bash on Linux/macOS)
dotnet hex1b terminal start

# Start a specific program with custom terminal size
dotnet hex1b terminal start --width 120 --height 40 -- htop

# Start with a working directory
dotnet hex1b terminal start --cwd /path/to/project -- vim myfile.txt

# Start a .NET project
dotnet hex1b terminal start -- dotnet run --project src/MyApp

# Start and immediately attach (interactive mirror)
dotnet hex1b terminal start --attach
```

To get the terminal ID for subsequent commands:

```bash
# List all running terminals
dotnet hex1b terminal list

# Get the ID as JSON (useful for scripting)
ID=$(dotnet hex1b terminal start --json -- dotnet run --project src/MyApp | jq -r .id)
```

## How to See What's on Screen

Capture the terminal's visible content at any point.

```bash
# Plain text (default) — good for reading content and assertions
dotnet hex1b capture screenshot <id>

# ANSI — preserves colors and formatting
dotnet hex1b capture screenshot <id> --format ansi

# SVG — rendered terminal screenshot as vector image
dotnet hex1b capture screenshot <id> --format svg --output screenshot.svg

# PNG — rendered terminal screenshot as raster image (requires --output)
dotnet hex1b capture screenshot <id> --format png --output screenshot.png

# HTML — rendered terminal screenshot as HTML
dotnet hex1b capture screenshot <id> --format html --output screenshot.html

# Include scrollback history
dotnet hex1b capture screenshot <id> --scrollback 100

# Wait for specific content to appear before capturing
dotnet hex1b capture screenshot <id> --format png --output ready.png --wait "Ready" --timeout 30
```

The `--wait` option polls until the specified text is visible, then captures. Useful when
the application takes time to render its initial state.

## How to Wait for Something to Appear

Use assertions to block until content is visible (or confirm it's absent). This is essential
for reliable automation — never assume timing.

```bash
# Wait up to 30 seconds for text to appear
dotnet hex1b assert <id> --text-present "Welcome" --timeout 30

# Confirm error text is NOT showing (waits up to 10s to be sure)
dotnet hex1b assert <id> --text-absent "Error" --timeout 10
```

Exit code 0 means the assertion passed; non-zero means it failed (timed out).

## How to Send Keyboard Input

Type text or send individual keys to the terminal.

```bash
# Type text (each character sent as a keystroke)
dotnet hex1b keys <id> --text "hello world"

# Send a named key
dotnet hex1b keys <id> --key Enter
dotnet hex1b keys <id> --key Tab
dotnet hex1b keys <id> --key Escape
dotnet hex1b keys <id> --key UpArrow

# Send key with modifiers
dotnet hex1b keys <id> --key C --ctrl          # Ctrl+C
dotnet hex1b keys <id> --key S --ctrl           # Ctrl+S
dotnet hex1b keys <id> --key Tab --shift        # Shift+Tab
dotnet hex1b keys <id> --key F --ctrl --shift   # Ctrl+Shift+F
```

Available key names (from the `Hex1bKey` enum, case-insensitive):

- **Letters:** `A`–`Z`
- **Digits:** `D0`–`D9`
- **Function keys:** `F1`–`F12`
- **Navigation:** `UpArrow`, `DownArrow`, `LeftArrow`, `RightArrow`, `Home`, `End`, `PageUp`, `PageDown`
- **Editing:** `Backspace`, `Delete`, `Insert`
- **Whitespace:** `Tab`, `Enter`, `Spacebar`
- **Other:** `Escape`
- **Punctuation:** `OemComma`, `OemPeriod`, `OemMinus`, `OemPlus`, `OemQuestion`, `Oem1`, `Oem4`, `Oem5`, `Oem6`, `Oem7`, `OemTilde`
- **Numpad:** `NumPad0`–`NumPad9`, `Multiply`, `Add`, `Subtract`, `Decimal`, `Divide`

## How to Send Mouse Input

Click or drag at specific terminal coordinates (0-based column, row).

```bash
# Left click at column 10, row 5
dotnet hex1b mouse click <id> 10 5

# Right click
dotnet hex1b mouse click <id> 10 5 --button right

# Drag from (5,3) to (20,3)
dotnet hex1b mouse drag <id> 5 3 20 3
```

## How to Start a Recording After a Terminal Has Launched

If the terminal is already running, start recording its session to an asciinema `.cast` file.

```bash
# Start the terminal first
dotnet hex1b terminal start -- dotnet run --project src/MyApp
# ... get the <id> from terminal list ...

# Begin recording to a file
dotnet hex1b capture recording start <id> --output session.cast

# Optionally set a title and idle time limit
dotnet hex1b capture recording start <id> --output session.cast --title "Demo session" --idle-limit 2.0

# Do your interactions...
dotnet hex1b keys <id> --text "hello"
dotnet hex1b keys <id> --key Enter

# Stop recording when done
dotnet hex1b capture recording stop <id>
```

## How to Record a Session from the Moment It Starts

Use `--record` on `terminal start` to begin recording immediately when the process launches.

```bash
# Start terminal with recording enabled from the start
dotnet hex1b terminal start --record session.cast -- dotnet run --project src/MyApp

# The recording is already in progress — interact normally
dotnet hex1b assert <id> --text-present "Ready" --timeout 15
dotnet hex1b keys <id> --key Enter
dotnet hex1b capture screenshot <id> --format text

# Stop recording when done
dotnet hex1b capture recording stop <id>
```

## How to Stop a Recording

```bash
# Stop the active recording
dotnet hex1b capture recording stop <id>

# Check if a terminal is currently recording
dotnet hex1b capture recording status <id>
```

The `.cast` file is written incrementally, so the file will contain all events up to the
point you stop.

## How to Play Back a Recording

```bash
# Simple playback in the terminal
dotnet hex1b capture recording playback --file session.cast

# Play at 2x speed
dotnet hex1b capture recording playback --file session.cast --speed 2.0

# Interactive TUI player with pause/seek controls
dotnet hex1b capture recording playback --file session.cast --player
```

## How to Inspect a Hex1b TUI App's Widget Tree

If the terminal is running a Hex1b application with `.WithDiagnostics()` enabled, you can
inspect its internal widget/node tree.

```bash
# Show the full widget tree with geometry
dotnet hex1b app tree <id>

# Include focus state
dotnet hex1b app tree <id> --focus

# Include popup stack
dotnet hex1b app tree <id> --popups

# Limit tree depth
dotnet hex1b app tree <id> --depth 3

# Get as JSON for programmatic inspection
dotnet hex1b app tree <id> --json
```

## How to Stop and Clean Up Terminals

```bash
# Stop a specific terminal
dotnet hex1b terminal stop <id>

# Get terminal details (PID, dimensions, uptime)
dotnet hex1b terminal info <id>

# Resize a running terminal
dotnet hex1b terminal resize <id> --width 160 --height 50

# Clean up stale sockets from exited processes
dotnet hex1b terminal clean
```

## How to Attach Interactively

Attach to a terminal for interactive use — you see what the process sees and can type directly.

```bash
# Attach to a terminal (Ctrl+] to detach)
dotnet hex1b terminal attach <id>
```

## How to Set Up the Agent Skill File

Generate this skill file for a repository so AI agents know how to use the CLI.

```bash
# Write skill file to .github/skills/hex1b/SKILL.md
dotnet hex1b agent init

# Specify a different repo root
dotnet hex1b agent init --path /path/to/repo

# Overwrite an existing skill file
dotnet hex1b agent init --force

# Print to stdout instead of writing to disk
dotnet hex1b agent init --stdout
```

## Common Workflow: End-to-End Scripted Test

```bash
# Launch the app
ID=$(dotnet hex1b terminal start --json -- dotnet run --project src/MyApp | jq -r .id)

# Wait for it to be ready
dotnet hex1b assert $ID --text-present "Main Menu" --timeout 15

# Navigate and interact
dotnet hex1b keys $ID --key Enter
dotnet hex1b assert $ID --text-present "Settings"
dotnet hex1b keys $ID --text "new value"
dotnet hex1b keys $ID --key Enter

# Capture final state
dotnet hex1b capture screenshot $ID --format png --output result.png
dotnet hex1b capture screenshot $ID --format text

# Clean up
dotnet hex1b terminal stop $ID
```

## Common Workflow: Record a Demo

```bash
# Start with recording
ID=$(dotnet hex1b terminal start --json --record demo.cast -- dotnet run --project samples/MyApp | jq -r .id)

# Wait and interact
dotnet hex1b assert $ID --text-present "Ready" --timeout 15
dotnet hex1b keys $ID --key Tab
dotnet hex1b keys $ID --key Enter

# Take a screenshot at a key moment
dotnet hex1b capture screenshot $ID --format png --output highlight.png

# Stop recording and terminal
dotnet hex1b capture recording stop $ID
dotnet hex1b terminal stop $ID

# Play it back
dotnet hex1b capture recording playback --file demo.cast --player
```

## Tips

- Use `--json` with `jq` for scriptable output: `dotnet hex1b terminal list --json | jq '.[] | .id'`
- Terminal IDs are PIDs — use a unique prefix instead of the full number
- `terminal list` automatically cleans up stale sockets from exited processes
- `capture screenshot --wait` is useful for waiting for async rendering before capturing
- Always use `assert` before interacting — never assume the app has rendered
- For PNG screenshots, `--output` is required since PNG is a binary format
- Recordings use the asciinema v2 `.cast` format and can be played with any compatible player