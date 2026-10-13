#!/usr/bin/env bash
# Copy the RimWorld 1.7 assemblies Multiplayer compiles against into Refs/RimWorld/.
# They are Ludeon's and the game's own files: Refs/ is gitignored, never commit them.
#
# Usage:
#   RIMWORLD_DIR="/path/to/RimWorld" ./scripts/fetch-rimworld-refs.sh
#   ./scripts/fetch-rimworld-refs.sh "/path/to/RimWorld"
#
# RimWorld 1.7 keeps its managed DLLs in the install root (no RimWorldWin64_Data/Managed).

set -euo pipefail

SRC="${1:-${RIMWORLD_DIR:-}}"
if [[ -z "${SRC}" ]]; then
  echo "Set RIMWORLD_DIR or pass the RimWorld 1.7 install folder." >&2
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="${SCRIPT_DIR}/../Refs/RimWorld"
mkdir -p "${DEST}"

for dll in Assembly-CSharp.dll UnityStubs.dll com.rlabrecque.steamworks.net.dll; do
  if [[ ! -f "${SRC}/${dll}" ]]; then
    echo "Missing ${SRC}/${dll}. Is this a RimWorld 1.7 install?" >&2
    exit 1
  fi
  cp "${SRC}/${dll}" "${DEST}/${dll}"
  echo "copied ${dll}"
done

echo "RimWorld refs are in ${DEST}"
