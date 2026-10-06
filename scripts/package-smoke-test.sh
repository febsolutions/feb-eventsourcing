#!/usr/bin/env bash
# Packs every package and builds/runs an application that consumes them from a local
# feed — catches packaging defects (e.g. the snapshot generator missing from the
# packages) that project-reference builds and unit tests cannot see.
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${1:-0.0.0-smoke.$(date +%s)}"
rm -rf artifacts/packages artifacts/consumer-packages tests/PackageConsumer/bin tests/PackageConsumer/obj

dotnet pack FEB.EventSourcing.sln -c Release -o artifacts/packages -p:Version="$VERSION"
dotnet run --project tests/PackageConsumer -c Release -p:FebVersion="$VERSION"
