#!/bin/bash

# This script generates an MSBuild props file listing test projects that contain tests
# marked with a specific attribute (e.g., QuarantinedTest).
#
# Usage: ./generate-specialized-test-projects-list.sh <attribute_name>
# Example: ./generate-specialized-test-projects-list.sh QuarantinedTest

set -euo pipefail
set -x

if [[ $# -ne 2 ]]; then
    echo "Usage: $0 <attribute_name> <output_file>"
    exit 1
fi

ATTRIBUTE_NAME="${1:?Usage: $0 <attribute_name>}"
REPO_ROOT="$(git rev-parse --show-toplevel)"
OUTPUT_FILE="$2"

mkdir -p "$(dirname "$OUTPUT_FILE")"

# Find all test files with the attribute and extract unique top-level test directories.
# Match both method/class-level attributes like [OuterloopTest("reason")] and
# assembly-level attributes like [assembly: OuterloopTest("reason")].
PROJECTS=$(grep -Erl "^[[:space:]]*\\[(assembly:[[:space:]]*)?${ATTRIBUTE_NAME}(Attribute)?(\\(\"[^\"]*\"\\))?\\]" "$REPO_ROOT/tests" 2>/dev/null \
    | while read -r file; do
        # Extract the top-level test directory (e.g., tests/Aspire.Hosting.Tests)
        rel_path="${file#$REPO_ROOT/}"
        echo "$rel_path" | cut -d'/' -f1-2
    done \
    | sort -u || true)

# Generate the MSBuild props file
cat > "$OUTPUT_FILE" << 'EOF'
<Project>
  <ItemGroup>
EOF

for project_dir in $PROJECTS; do
    # Find the .csproj file in the directory
    csproj=$(find "$REPO_ROOT/$project_dir" -maxdepth 1 -name "*.csproj" 2>/dev/null | head -1)
    if [[ -n "$csproj" ]]; then
        rel_csproj="${csproj#$REPO_ROOT/}"
        echo "    <OverrideProjectToBuild Include=\"\$(RepoRoot)$rel_csproj\" />" >> "$OUTPUT_FILE"
    fi
done

cat >> "$OUTPUT_FILE" << 'EOF'
  </ItemGroup>
</Project>
EOF

echo "Generated $OUTPUT_FILE"
cat "$OUTPUT_FILE"
