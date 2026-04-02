#!/bin/bash
set -e

echo "Checking prerequisites..."

# Check for Node.js
if ! command -v node &> /dev/null; then
    echo "Error: Node.js is not installed. Please install Node.js first."
    exit 1
fi

# Check for npm
if ! command -v npm &> /dev/null; then
    echo "Error: npm is not installed. Please install npm first."
    exit 1
fi

# Check for VS Code or VS Code Insiders
if ! command -v code &> /dev/null && ! command -v code-insiders &> /dev/null; then
    echo "Error: VS Code or VS Code Insiders is not installed or not in PATH."
    echo "Please install VS Code or VS Code Insiders and ensure it's added to your PATH."
    exit 1
fi

# Check for dotnet
if ! command -v dotnet &> /dev/null; then
    echo "Error: .NET SDK is not installed. Please install .NET SDK first."
    echo "Use the restore script at the repo root."
    exit 1
fi

echo "All prerequisites satisfied."
echo ""
echo "Running npm install..."
npm install

echo ""
echo "Running npm run compile..."
npm run compile

echo ""
echo "Building Aspire CLI..."
dotnet build ../src/Aspire.Cli/Aspire.Cli.csproj

echo ""
echo "Build completed successfully!"
