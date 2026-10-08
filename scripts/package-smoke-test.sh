#!/usr/bin/env bash
# Packs every package and builds/runs an application that consumes them from a local
# feed — catches packaging defects (e.g. the snapshot generator missing from the
# packages) that project-reference builds and unit tests cannot see.
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${1:-0.0.0-smoke.$(date +%s)}"
rm -rf artifacts/packages artifacts/consumer-packages tests/PackageConsumer/bin tests/PackageConsumer/obj

dotnet pack FEB.EventSourcing.sln -c Release -o artifacts/packages -p:Version="$VERSION"
# Once per target framework the packages ship for (FebTargetFrameworks in Directory.Build.props).
FRAMEWORKS="$(dotnet msbuild tests/PackageConsumer -getProperty:TargetFrameworks -p:FebVersion="$VERSION")"
for FRAMEWORK in ${FRAMEWORKS//;/ }; do
  echo "--- PackageConsumer on $FRAMEWORK"
  dotnet run --project tests/PackageConsumer -c Release -f "$FRAMEWORK" -p:FebVersion="$VERSION"
done
