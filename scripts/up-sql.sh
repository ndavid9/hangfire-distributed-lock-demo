#!/usr/bin/env bash
# Start the local SQL Server used by the lock lab. Idempotent.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

if ! command -v docker >/dev/null 2>&1; then
  echo "docker is not installed." >&2
  exit 1
fi

if ! docker info >/dev/null 2>&1; then
  if sudo -n docker info >/dev/null 2>&1; then
    DOCKER=(sudo docker)
  else
    echo "The Docker daemon is not reachable. Start dockerd, then rerun this script." >&2
    exit 1
  fi
else
  DOCKER=(docker)
fi

if "${DOCKER[@]}" ps --format '{{.Names}}' | grep -qx 'hangfire-lock-sql'; then
  echo "hangfire-lock-sql is already running."
  exit 0
fi

if "${DOCKER[@]}" compose version >/dev/null 2>&1; then
  "${DOCKER[@]}" compose up -d
else
  "${DOCKER[@]}" run -d --name hangfire-lock-sql \
    -p 127.0.0.1:14333:1433 \
    -e ACCEPT_EULA=Y \
    -e 'MSSQL_SA_PASSWORD=LocalOnly_hf_Passw0rd' \
    -e MSSQL_PID=Developer \
    -e MSSQL_MEMORY_LIMIT_MB=1536 \
    --memory=2500m \
    --shm-size=256m \
    mcr.microsoft.com/mssql/server:2022-CU16-ubuntu-22.04
fi

echo "SQL Server container started. The app waits for 127.0.0.1:14333 itself."
