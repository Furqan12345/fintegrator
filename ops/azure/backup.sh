#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."
mkdir -p backups
chmod 700 backups
backup_path="$(pwd)/backups/ipaas-$(date -u +%Y%m%d-%H%M%S).db"

docker compose stop engine api
trap 'docker compose up -d api engine' EXIT
docker run --rm \
  -v simpleipaas-data:/data:ro \
  -v "$(pwd)/backups:/backup" \
  busybox:1.37 sh -c 'test -s /data/ipaas.db && cp /data/ipaas.db "/backup/$1"' sh "$(basename "$backup_path")"
chmod 600 "$backup_path"
echo "Backup created: $backup_path"