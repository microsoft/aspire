#!/usr/bin/env bash

# Install MAUI workload if -restoreMaui was passed
# Only on macOS (MAUI doesn't support Linux, Windows uses .cmd)

if [[ "${restore_maui:-false}" == true ]]; then
  # Check if we're on macOS
  if [[ "$(uname -s)" == "Darwin" ]]; then
    echo ""
    echo "Installing MAUI workload..."

    dotnet_sh="$repo_root/dotnet.sh"

    if "$dotnet_sh" workload install maui; then
      echo "MAUI workload installed successfully."
      echo ""
    else
      echo ""
      echo "WARNING: Failed to install MAUI workload. You may need to run this command manually:"
      echo "  $dotnet_sh workload install maui"
      echo ""
      echo "The MAUI playground may not work without the MAUI workload installed."
      echo ""
    fi
  else
    echo "Skipping MAUI workload installation on Linux (not supported)."
  fi

  if ! command -v python3 >/dev/null 2>&1; then
    echo "python3 is required to generate AspireWithMaui.slnx"
    exit 1
  fi

  # Generate AspireWithMaui.slnx from the base Aspire.slnx
  echo ""
  echo "Generating AspireWithMaui.slnx..."

  source_slnx="$repo_root/Aspire.slnx"
  output_path="$repo_root/playground/AspireWithMaui"
  output_slnx="$output_path/AspireWithMaui.slnx"

  if [ ! -f "$source_slnx" ]; then
    echo "WARNING: Source solution file not found: $source_slnx"
  else
    mkdir -p "$output_path"

    python3 <<'PY' "$source_slnx" "$output_slnx"
import codecs
import os
import re
import sys

source = os.path.abspath(sys.argv[1])
target = os.path.abspath(sys.argv[2])
source_dir = os.path.dirname(source)
target_dir = os.path.dirname(target)

with open(source, 'rb') as handle:
    text = handle.read().decode('utf-8-sig')

maui_folder_marker = 'playground/AspireWithMaui/AspireWithMaui.AppHost/AspireWithMaui.AppHost.csproj'
if maui_folder_marker not in text:
    folder_block = (
        '\r\n  <Folder Name="/playground/AspireWithMaui/">\r\n'
        '    <Project Path="playground/AspireWithMaui/AspireWithMaui.AppHost/AspireWithMaui.AppHost.csproj" />\r\n'
        '    <Project Path="playground/AspireWithMaui/AspireWithMaui.MauiClient/AspireWithMaui.MauiClient.csproj" />\r\n'
        '    <Project Path="playground/AspireWithMaui/AspireWithMaui.MauiServiceDefaults/AspireWithMaui.MauiServiceDefaults.csproj" />\r\n'
        '    <Project Path="playground/AspireWithMaui/AspireWithMaui.ServiceDefaults/AspireWithMaui.ServiceDefaults.csproj" />\r\n'
        '    <Project Path="playground/AspireWithMaui/AspireWithMaui.WeatherApi/AspireWithMaui.WeatherApi.csproj" />\r\n'
        '  </Folder>\r\n'
    )
    text = text.replace('\r\n</Solution>', f'{folder_block}</Solution>', 1)

tests_folder_pattern = re.compile(r'(<Folder Name="/tests/Hosting/">\r?\n)(.*?)(  </Folder>)', re.DOTALL)
match = tests_folder_pattern.search(text)
desired_line = '    <Project Path="tests/Aspire.Hosting.Maui.Tests/Aspire.Hosting.Maui.Tests.csproj" />\r\n'
desired_path = 'tests/Aspire.Hosting.Maui.Tests/Aspire.Hosting.Maui.Tests.csproj'
if match:
    body = match.group(2)
    if desired_path not in body:
        lines = body.splitlines(keepends=True)

        def extract_path(line: str) -> str:
            hit = re.search(r'Path="([^"]+)"', line)
            return hit.group(1) if hit else ''

        inserted = False
        for index, line in enumerate(lines):
            existing_path = extract_path(line)
            if existing_path and desired_path.lower() < existing_path.lower():
                lines.insert(index, desired_line)
                inserted = True
                break
        if not inserted:
            lines.append(desired_line)

        new_body = ''.join(lines)
        text = text[:match.start(2)] + new_body + text[match.end(2):]

def resolve_relative(value: str) -> str:
    normalized = value.replace('\\', '/').replace('/', os.sep)
    if os.path.isabs(normalized):
        absolute = os.path.normpath(normalized)
    else:
        absolute = os.path.normpath(os.path.join(source_dir, normalized))
    relative = os.path.relpath(absolute, target_dir)
    return relative.replace(os.sep, '/')

def substitute(match: re.Match) -> str:
    original = match.group(1)
    return f'Path="{resolve_relative(original)}"'

text = re.sub(r'Path="([^"]+)"', substitute, text)

with open(target, 'wb') as handle:
    handle.write(codecs.BOM_UTF8)
    handle.write(text.encode('utf-8'))
PY

    echo "Generated AspireWithMaui.slnx at: $output_slnx"
  fi
fi
