#!/usr/bin/env bash
set -euo pipefail

project_root="$(cd "$(dirname "$0")/.." && pwd)"
output_dir="$project_root/dist"
publish_dir="$output_dir/publish"
version="0.3.0.0"

rm -rf "$publish_dir"
mkdir -p "$publish_dir"
dotnet publish "$project_root/Coast/Jellyfin.Plugin.Coast.csproj" --configuration Release --output "$publish_dir"
cd "$publish_dir"
zip -j "$output_dir/Jellyfin.Plugin.Coast_${version}.zip" Jellyfin.Plugin.Coast.dll
