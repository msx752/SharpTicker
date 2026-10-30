#!/usr/bin/env bash
# Runs every test project for one target framework.
# Usage: bash .github/scripts/test.sh <tfm> [extra dotnet arguments...]
# Example: bash .github/scripts/test.sh net8.0 -p:LatestPatchDependencies=true
set -euo pipefail

tfm="$1"
shift

status=0
for project in tests/*/*.Tests.csproj; do
  echo "::group::$project ($tfm)"
  if ! dotnet test "$project" --configuration Release --framework "$tfm" "$@"; then
    status=1
  fi
  echo "::endgroup::"
done

exit $status
