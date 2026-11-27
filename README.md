# Jellyfin DoujinStyle Importer (`Music-Import`)

Plugin natif **Jellyfin 12.x (.NET 10)** permettant de **rechercher automatiquement des musiques/albums sur [doujinstyle.com](https://doujinstyle.com/)** directement depuis la barre de recherche de vos applications **Jellyfin Web, Jellyfin Mobile, Finamp ou Symfonium**, puis de les **télécharger, décompresser et importer automatiquement** dans votre serveur Jellyfin.

---

## Fonctionnalités

- **Recherche intégrée dans toutes vos apps Jellyfin** : tapez le nom d'un artiste, cercle ou album (ex: `FELT`, `ShibayanRecords`, `Camellia`, `IOSYS`...) dans Jellyfin, Finamp ou Symfonium — les albums correspondants de DoujinStyle apparaissent directement avec le préfixe `[DoujinStyle]` et leur pochette.
- **Téléchargement en 1 geste depuis l'app** :
  - Appuyez sur le **Cœur / Favori (♥)** sur un album ou une piste `[DoujinStyle]` (idéal sur Finamp, Symfonium et mobile), **ou**
  - Appuyez sur **Lecture (▶)** (joue un court carillon de confirmation et lance le téléchargement en arrière-plan), **ou**
  - Activez l'option *« Télécharger dès l'ouverture de la fiche album »* dans les réglages du plugin.
- **Hébergeurs supportés nativement (100 % intégré au plugin, sans navigateur externe)** :
  - **MediaFire** (fichiers uniques `.rar`/`.zip`/`.7z` et dossiers partagés `/folder/`)
  - **Google Drive** (avec gestion automatique de la confirmation gros fichiers)
  - **Mega.nz** (déchiffrement `AES-128-CTR` natif à la volée)
  - **Liens HTTP directs**
- **Extraction & rangement propres** :
  - Décompression `.rar` (RAR4/RAR5), `.zip`, `.7z`, `.tar` via `SharpCompress`
  - Rangement automatique dans `{DossierMusique}/{Artiste}/{Album}/` (suppression des sous-dossiers doublons inutiles, conservation des `Disc 1` / `Disc 2`)
  - Complétion automatique des tags ID3 / Vorbis / FLAC manquants (`Album`, `AlbumArtist`, `Genres`) via `TagLibSharp` + ajout de `cover.jpg`
  - Déclenchement automatique du **scan de médiathèque Jellyfin** + notification en direct sur votre écran Jellyfin.

---

## Installation sur Ultra.cc (`usbx.me` — Jellyfin 12.2)

### Méthode Recommandée — 100 % depuis la page web Jellyfin (0 FTP, 0 SSH)
*(Nécessite que le dépôt GitHub hébergeant `manifest.json` soit en visibilité **Public** pour que Jellyfin puisse le télécharger.)*

1. Ouvrez votre page Jellyfin (`https://ryo.hebe.usbx.me/jellyfin`) → **Tableau de bord → Plugins → Catalogue → ⚙️ (Dépôts / Repositories) → `+`**.
2. Ajoutez le dépôt :
   - **Nom du dépôt** : `DoujinStyle Importer`
   - **URL du dépôt** :
     `https://raw.githubusercontent.com/EirinSan/Music-Import/feat/doujinstyle-jellyfin-plugin/releases/manifest.json`
3. Retournez dans **Catalogue** → rubrique **Music** → cliquez sur **DoujinStyle Importer** → **Installer**.
4. Dans **Tableau de bord**, cliquez sur **Redémarrer** pour charger le plugin.
5. Après redémarrage, ouvrez **Tableau de bord → Plugins → DoujinStyle Importer** : si vous n'avez pas encore de bibliothèque Musique, cliquez simplement sur le bouton **« Créer la médiathèque Musique en 1 clic »** (crée automatiquement `~/media/Music` et la médiathèque Jellyfin sans FTP ni SSH !).

---

Le binaire compilé prêt à l'emploi se trouve aussi dans [`releases/Jellyfin.Plugin.DoujinStyle_1.0.0.0.zip`](releases/Jellyfin.Plugin.DoujinStyle_1.0.0.0.zip) si le dépôt reste privé :

### Méthode 1 — Sans terminal (SFTP / Filebrowser Ultra.cc)
1. Téléchargez le fichier `releases/Jellyfin.Plugin.DoujinStyle_1.0.0.0.zip` depuis ce dépôt.
2. Connectez-vous en SFTP ou via **Filebrowser** sur votre slot Ultra.cc :
   - Créez le dossier `~/media/Music` s'il n'existe pas encore.
   - Allez dans `~/.config/jellyfin/plugins/` (ou `~/.apps/jellyfin/plugins/` selon votre slot) et créez un dossier nommé **`DoujinStyle_1.0.0.0`**.
   - Décompressez le contenu du `.zip` (`Jellyfin.Plugin.DoujinStyle.dll`, `SharpCompress.dll`, `TagLibSharp.dll`, `HtmlAgilityPack.dll`, `ZstdSharp.dll`, `meta.json`) dans ce dossier `DoujinStyle_1.0.0.0`.
3. Redémarrez Jellyfin depuis votre panneau **Ultra.cc Control Panel (UCP)** (ou en SSH via `app-jellyfin restart`).

### Méthode 2 — Automatique en SSH sur Ultra.cc
Connectez-vous en SSH à votre slot Ultra.cc (`hebe.usbx.me`), copiez/clonez ce dépôt puis lancez :
```bash
bash scripts/install-ultracc.sh
```
Le script crée `~/media/Music`, installe le plugin dans le dossier Jellyfin et exécute `app-jellyfin restart`.

---

## Configuration initiale dans Jellyfin

1. Ouvrez votre interface Jellyfin (`https://ryo.hebe.usbx.me/jellyfin`).
2. Allez dans **Tableau de bord → Médiathèques (Libraries)** :
   - Assurez-vous d'avoir une médiathèque de type **Musique** pointant vers votre dossier `/home/ryo/media/Music` (le plugin détectera ce chemin automatiquement !).
3. Allez dans **Tableau de bord → Plugins → DoujinStyle Importer** :
   - Vérifiez que le bandeau affiche bien votre **Dossier Musique actif**.
   - Cliquez sur **« Tester la connexion à DoujinStyle »** pour vérifier que votre serveur joint bien `doujinstyle.com`.

---

## Bridge Résidentiel Optionnel (si l'IP datacenter est filtrée)

Certaines adresses IP d'hébergeurs / seedboxes reçoivent parfois un écran anti-bot (`One moment, please...`) de la part de `doujinstyle.com`.
- Le plugin **détecte proprement** ce filtrage (sans tenter de contourner de CAPTCHA) et l'affiche dans la page du plugin.
- Si l'IP de votre slot Ultra.cc est filtrée alors que votre connexion Internet personnelle (PC / Raspberry Pi / NAS maison) accède normalement à `doujinstyle.com`, vous pouvez lancer le petit relais léger fourni dans [`bridge/`](bridge/) :

```bash
cd bridge
pip install -r requirements.txt
export BRIDGE_API_KEY="un-mot-de-passe-secret"
uvicorn app:app --host 0.0.0.0 --port 8787
```

Puis renseignez l'URL de ce relais (ex. via Tailscale / Cloudflare Tunnel / port ouvert) dans **URL du Bridge résidentiel optionnel** dans les réglages du plugin Jellyfin : la recherche se fera via votre IP résidentielle tandis que le téléchargement lourd (MediaFire / Mega / GDrive) et l'extraction continueront de tourner directement à pleine vitesse sur votre seedbox Ultra.cc !

---

## Compiler & Tester depuis les sources

Prérequis : **.NET 10 SDK**

```bash
# Lancer les tests unitaires
dotnet test Jellyfin.Plugin.DoujinStyle.sln -c Release

# Générer l'archive release + manifest.json
bash scripts/package-plugin.sh
```
