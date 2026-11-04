#!/bin/bash
# Builds the launcher, signs the manifest and publishes launcher + client downloads to the VPS.
# Usage: publish_downloads.sh <client version> <mac client zip> <windows client zip>
#   (pass "-" for a client zip to keep what is already published for that platform: not supported yet; pass both)
# Launcher version comes from `const Version` in launcher.go: bump it before publishing a new launcher.
# The signing key stays at ~/GodsWar-Backups/patch-signing/private.pem (same trust root as gwupdate).
set -euo pipefail
cd "$(dirname "$0")"
CV="${1:?client version}" MAC="${2:?mac client zip}" WIN="${3:?windows client zip}"
KEY="$HOME/GodsWar-Backups/patch-signing/private.pem"
STAGE="$(mktemp -d)"
./build.sh "$STAGE" >/dev/null
LV="$(sed -n 's/^const Version = "\(.*\)"/\1/p' launcher.go)"
cp -c "$MAC" "$STAGE/GodsWar-mac-$CV.zip"; cp -c "$WIN" "$STAGE/GodsWar-windows-$CV.zip"
ditto -c -k --sequesterRsrc --keepParent "$STAGE/GodsWar Launcher.app" "$STAGE/GodsWar-Launcher-mac.zip"
cp "$STAGE/gwlauncher-windows-$LV.exe" "$STAGE/GodsWar-Launcher-windows.exe"
go run ./tools/gwpublish -key "$KEY" -dir "$STAGE" -client-version "$CV" \
  -mac-client "GodsWar-mac-$CV.zip" -windows-client "GodsWar-windows-$CV.zip" \
  -launcher-version "$LV" -mac-launcher "gwlauncher-mac-$LV" -windows-launcher "gwlauncher-windows-$LV.exe"
cd "$STAGE"
rsync --partial --inplace --timeout=60 -e ssh GodsWar-mac-$CV.zip GodsWar-windows-$CV.zip gwlauncher-mac-$LV gwlauncher-windows-$LV.exe \
  GodsWar-Launcher-mac.zip GodsWar-Launcher-windows.exe vps:/var/www/gwdownloads/files/
scp -q manifest.json manifest.json.sig vps:
ssh vps 'sudo -n chmod 644 /var/www/gwdownloads/files/* && sudo -n install -m644 ~/manifest.json ~/manifest.json.sig /var/www/gwdownloads/ && rm ~/manifest.json ~/manifest.json.sig'   # manifest last
echo "published client $CV, launcher $LV"
