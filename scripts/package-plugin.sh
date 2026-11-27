#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${1:-1.0.0.0}"
PLUGIN_GUID="91b47a12-5d3e-4c8f-a72e-681f0c4d9e21"
BUILD_OUT="$ROOT_DIR/Jellyfin.Plugin.DoujinStyle/bin/Release/net10.0"
DIST_DIR="$ROOT_DIR/releases"
STAGE_DIR="$(mktemp -d)"

cleanup() {
    rm -rf "$STAGE_DIR"
}
trap cleanup EXIT

echo "==> Building Jellyfin.Plugin.DoujinStyle v${VERSION} (Jellyfin 12 / .NET 10)..."
dotnet build "$ROOT_DIR/Jellyfin.Plugin.DoujinStyle/Jellyfin.Plugin.DoujinStyle.csproj" -c Release

mkdir -p "$DIST_DIR"

# Copy only plugin + non-Jellyfin runtime dependencies
for dll in "$BUILD_OUT"/*.dll; do
    base="$(basename "$dll")"
    case "$base" in
        Jellyfin.*|MediaBrowser.*|Microsoft.*|System.*)
            if [[ "$base" == "Jellyfin.Plugin.DoujinStyle.dll" ]]; then
                cp "$dll" "$STAGE_DIR/"
            fi
            ;;
        *)
            cp "$dll" "$STAGE_DIR/"
            ;;
    esac
done

TIMESTAMP="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

cat > "$STAGE_DIR/meta.json" <<EOF
{
  "category": "Music",
  "changelog": "Initial release: DoujinStyle search injection, MediaFire/GDrive/Mega downloaders, SharpCompress extraction, and automatic Jellyfin library scan.",
  "description": "Recherche automatiquement des albums sur doujinstyle.com depuis vos applications Jellyfin (Web, Android/iOS, Finamp, Symfonium) et les telecharge + extrait dans votre mediatheque Musique.",
  "guid": "${PLUGIN_GUID}",
  "name": "DoujinStyle Importer",
  "overview": "Import automatique d'albums DoujinStyle depuis la recherche Jellyfin",
  "owner": "EirinSan",
  "targetAbi": "12.0.0.0",
  "timestamp": "${TIMESTAMP}",
  "version": "${VERSION}",
  "status": "Active",
  "autoUpdate": true
}
EOF

ZIP_NAME="Jellyfin.Plugin.DoujinStyle_${VERSION}.zip"
ZIP_PATH="$DIST_DIR/$ZIP_NAME"
rm -f "$ZIP_PATH"

(
    cd "$STAGE_DIR"
    python3 -m zipfile -c "$ZIP_PATH" ./*
)

MD5_HASH="$(md5sum "$ZIP_PATH" | awk '{print $1}')"
BRANCH_NAME="${MANIFEST_BRANCH:-feat/doujinstyle-jellyfin-plugin}"
DOWNLOAD_URL="${REPO_RELEASE_BASE_URL:-https://raw.githubusercontent.com/EirinSan/Music-Import/${BRANCH_NAME}/releases}/${ZIP_NAME}"

cat > "$DIST_DIR/manifest.json" <<EOF
[
  {
    "guid": "${PLUGIN_GUID}",
    "name": "DoujinStyle Importer",
    "description": "Recherche automatiquement des albums sur doujinstyle.com depuis Jellyfin/Finamp/Symfonium et les telecharge + extrait dans votre mediatheque Musique.",
    "overview": "Import automatique d'albums DoujinStyle depuis la recherche Jellyfin",
    "owner": "EirinSan",
    "category": "Music",
    "versions": [
      {
        "version": "${VERSION}",
        "changelog": "Initial release for Jellyfin 12.x (.NET 10).",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "${DOWNLOAD_URL}",
        "checksum": "${MD5_HASH}",
        "timestamp": "${TIMESTAMP}"
      }
    ]
  }
]
EOF

cp "$STAGE_DIR/meta.json" "$DIST_DIR/meta.json"

echo "==> Built: $ZIP_PATH"
echo "==> MD5  : $MD5_HASH"
