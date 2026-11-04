#!/usr/bin/env bash
# Create the key pair that signs client updates for YOUR launcher. Run once.
# Private key -> local/update-private.pem (never commit); prints the public key to build the launcher with.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p local
[ -f local/update-private.pem ] && { echo "local/update-private.pem already exists; not overwriting."; } || openssl genpkey -algorithm ed25519 -out local/update-private.pem
echo "Public key (use as GW_UPDATE_PUBKEY):"
openssl pkey -in local/update-private.pem -pubout -outform DER | tail -c 32 | base64
