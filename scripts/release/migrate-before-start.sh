#!/usr/bin/env bash
# Use the CLI from the same release as the new application binaries.
set -euo pipefail

if (( $# < 3 )) || [[ "$2" != "--" ]]; then
  printf 'Usage: %s <published-Wasla.Cli.dll> -- <new-version-start-command> [args...]\n' "$0" >&2
  exit 2
fi

cli=$1
shift 2

if [[ ! -f "$cli" ]]; then
  printf 'Published Wasla.Cli.dll not found. Rollout stopped.\n' >&2
  exit 2
fi

# A failed central migration, a failed tenant migration, or unreadable/pending/ahead schema
# must prevent the new version from starting. Do not use --only or --dry-run here.
dotnet "$cli" migrate-central
dotnet "$cli" migrate-all-tenants
dotnet "$cli" migration-status --require-current

printf 'Migration gate passed; starting the new version.\n'
exec "$@"
