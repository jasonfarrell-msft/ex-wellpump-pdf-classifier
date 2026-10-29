#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root/frontend"
# Install first with npm ci when node_modules is absent. Keeping installation separate
# permits controlled/offline caches in environments with a package-feed proxy.
npm run build
cd "$root"
dotnet run --project backend/tests/WellpumpClassifier.Tests.csproj -c Release
dotnet publish backend/WellpumpClassifier.csproj -c Release --no-self-contained -o artifacts/server
printf 'Published frontend and backend to %s/artifacts/server\n' "$root"
