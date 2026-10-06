#!/usr/bin/env bash
# Coverage gate for the FEB.EventSourcing packages: fails when line coverage across
# all framework assemblies drops below 85 %. The same gate runs in CI.
# Requires Docker (Testcontainers: MongoDB, Redis, PostgreSQL, SQL Server).
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet test src/FEB.EventSourcing.Tests/FEB.EventSourcing.Tests.csproj \
  /p:CollectCoverage=true \
  /p:CoverletOutputFormat=cobertura \
  /p:CoverletOutput=../../coverage/ \
  /p:Include="[FEB.EventSourcing*]*" \
  /p:Threshold=85 \
  /p:ThresholdType=line \
  /p:ThresholdStat=total \
  "$@"
