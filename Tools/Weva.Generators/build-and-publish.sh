#!/usr/bin/env bash
# Weva Roslyn generator build & publish.
# Builds Tools/Weva.Generators in Release, copies the resulting
# Weva.Generators.dll to Packages/com.wevaui/Runtime/Generators/, and
# materializes a .dll.meta with the RoslynAnalyzer label so Unity loads it
# as a source generator. Idempotent — if the source files are unchanged
# the SHA matches and the copy is skipped.
set -euo pipefail

CONFIGURATION="${1:-Release}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
CSPROJ="$SCRIPT_DIR/Weva.Generators.csproj"
BUILD_OUT="$SCRIPT_DIR/bin/$CONFIGURATION/netstandard2.0/Weva.Generators.dll"
PUBLISH_DIR="$REPO_ROOT/Packages/com.wevaui/Runtime/Generators"
PUBLISH_DLL="$PUBLISH_DIR/Weva.Generators.dll"
PUBLISH_META="$PUBLISH_DLL.meta"

echo "[weva] generator build:"
echo "  csproj : $CSPROJ"
echo "  output : $PUBLISH_DLL"

mkdir -p "$PUBLISH_DIR"

dotnet build -c "$CONFIGURATION" "$CSPROJ"

if [ ! -f "$BUILD_OUT" ]; then
    echo "Build output not found: $BUILD_OUT" >&2
    exit 1
fi

should_copy=1
if [ -f "$PUBLISH_DLL" ]; then
    a=$(sha256sum "$BUILD_OUT" | awk '{print $1}')
    b=$(sha256sum "$PUBLISH_DLL" | awk '{print $1}')
    if [ "$a" = "$b" ]; then
        should_copy=0
        echo "[weva] generator unchanged, skipping copy."
    fi
fi

if [ "$should_copy" -eq 1 ]; then
    cp -f "$BUILD_OUT" "$PUBLISH_DLL"
    echo "[weva] copied $BUILD_OUT -> $PUBLISH_DLL"
fi

if [ ! -f "$PUBLISH_META" ]; then
    cat > "$PUBLISH_META" <<'EOF'
fileFormatVersion: 2
guid: 6f3b9c8a1d2e4f5a8b9c0d1e2f3a4b5c
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 1
  validateReferences: 1
  platformData:
  - first:
      '': Any
    second:
      enabled: 0
      settings:
        Exclude Editor: 0
        Exclude Linux64: 0
        Exclude OSXUniversal: 0
        Exclude Win: 0
        Exclude Win64: 0
  - first:
      Any:
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  userData: ''
  assetBundleName: ''
  assetBundleVariant: ''
  labels:
  - RoslynAnalyzer
EOF
    echo "[weva] wrote $PUBLISH_META"
fi

echo "[weva] generator publish complete."
