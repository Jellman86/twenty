#!/usr/bin/env bash
set -euo pipefail
python3 scripts/check-sensitive-paths.py
# Version and checksum come from the upstream release asset. No scanner token needed.
version=8.30.1
checksum=551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
curl --fail --location --silent --show-error "https://github.com/gitleaks/gitleaks/releases/download/v${version}/gitleaks_${version}_linux_x64.tar.gz" -o "$scratch/gitleaks.tar.gz"
printf '%s  %s\n' "$checksum" "$scratch/gitleaks.tar.gz" | sha256sum --check --status
tar -xzf "$scratch/gitleaks.tar.gz" -C "$scratch" gitleaks
"$scratch/gitleaks" git --redact --no-banner --log-opts="--all" .
"$scratch/gitleaks" dir --redact --no-banner .
# A generated fake key proves the scanner fails closed; no real credential is used.
mkdir "$scratch/control"
printf 'aws_access_key_id = "%s%s"\n' 'AKIA' 'ABCDEFGHIJKLMNOP' > "$scratch/control/leak.txt"
set +e
"$scratch/gitleaks" dir --redact --no-banner "$scratch/control" >/dev/null 2>&1
control_status=$?
set -e
if [ "$control_status" -ne 1 ]; then
  echo 'Secret-scanner negative control failed' >&2
  exit 1
fi
echo 'Synthetic leak control correctly rejected'
