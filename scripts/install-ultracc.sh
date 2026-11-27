#!/usr/bin/env bash
set -euo pipefail

# Installateur automatique du plugin Jellyfin.Plugin.DoujinStyle pour Ultra.cc (usbx.me)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ZIP_FILE="$ROOT_DIR/releases/Jellyfin.Plugin.DoujinStyle_1.0.0.0.zip"

if [[ ! -f "$ZIP_FILE" ]]; then
    echo "Erreur : Archive introuvable ($ZIP_FILE)."
    exit 1
fi

# Detection automatique du dossier plugins Jellyfin sur Ultra.cc / Linux
CANDIDATE_DIRS=(
    "$HOME/.config/jellyfin/plugins"
    "$HOME/.config/jellyfin/data/plugins"
    "$HOME/.apps/jellyfin/plugins"
    "$HOME/.apps/jellyfin/config/plugins"
    "$HOME/.local/share/jellyfin/plugins"
)

PLUGINS_DIR=""
for dir in "${CANDIDATE_DIRS[@]}"; do
    parent="$(dirname "$dir")"
    if [[ -d "$dir" ]] || [[ -d "$parent" ]]; then
        PLUGINS_DIR="$dir"
        break
    fi
done

if [[ -z "$PLUGINS_DIR" ]]; then
    echo "Impossible de detecter automatiquement le dossier Jellyfin."
    echo "Passez le chemin en argument : bash scripts/install-ultracc.sh /chemin/vers/jellyfin/plugins"
    exit 1
fi

if [[ $# -ge 1 && -n "${1:-}" ]]; then
    PLUGINS_DIR="$1"
fi

TARGET_PLUGIN_DIR="$PLUGINS_DIR/DoujinStyle_1.0.0.0"
MUSIC_MEDIA_DIR="$HOME/media/Music"

echo "==> Creation du dossier Musique ($MUSIC_MEDIA_DIR) si absent..."
mkdir -p "$MUSIC_MEDIA_DIR"

echo "==> Installation du plugin dans : $TARGET_PLUGIN_DIR"
rm -rf "$TARGET_PLUGIN_DIR"
mkdir -p "$TARGET_PLUGIN_DIR"

python3 -m zipfile -e "$ZIP_FILE" "$TARGET_PLUGIN_DIR"
chmod -R u+rwX,go+rX "$TARGET_PLUGIN_DIR"

echo "==> Fichiers installes :"
ls -lh "$TARGET_PLUGIN_DIR"

if command -v app-jellyfin >/dev/null 2>&1; then
    echo "==> Redemarrage de Jellyfin sur Ultra.cc (app-jellyfin restart)..."
    app-jellyfin restart
    echo "==> Jellyfin redemarre avec succes !"
else
    echo "==> Commande 'app-jellyfin' non trouvee : redemarrez Jellyfin depuis votre panneau Ultra.cc (UCP) ou Tableau de bord Jellyfin."
fi

echo ""
echo "Installation terminee !"
echo "1. Ouvrez Jellyfin -> Tableau de bord -> Bibliotheques : verifiez qu'une bibliotheque 'Musique' pointe vers ~/media/Music."
echo "2. Verifiez Tableau de bord -> Plugins -> DoujinStyle Importer."
echo "3. Cherchez un artiste/album dans votre app Jellyfin / Finamp / Symfonium et cliquez sur Favori (coeur) ou Lecture pour l'importer !"
