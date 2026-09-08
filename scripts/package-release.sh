#!/bin/bash
# Run from the repository root after publishing osx-arm64.
set -euo pipefail
test -f Deneb.slnx
published="${1:-artifacts/osx-arm64}"
release_output="${2:-artifacts/release}"
test -x "$published/deneb"
package_cache="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
staging="$(mktemp -d "${TMPDIR:-/tmp}/deneb-release.XXXXXX")"
mkdir -p "$release_output" "$staging/licenses"
cp "$published/deneb" README.md README.ru.md LICENSE CHANGELOG.md THIRD-PARTY-NOTICES.md EXPERIMENTAL.md "$staging/"
cp -R docs "$staging/docs"
cp docs/licenses/*.txt "$staging/licenses/"
for package in microsoft.netcore.app.runtime.osx-arm64/10.0.8 system.management/9.0.4 system.codedom/9.0.4; do
  label="${package//\//-}"
  cp "$package_cache/$package/LICENSE.TXT" "$staging/licenses/$label-LICENSE.txt"
  cp "$package_cache/$package/THIRD-PARTY-NOTICES.TXT" "$staging/licenses/$label-NOTICES.txt"
done
COPYFILE_DISABLE=1 tar -czf "$release_output/deneb-osx-arm64.tar.gz" -C "$staging" deneb README.md README.ru.md LICENSE CHANGELOG.md THIRD-PARTY-NOTICES.md EXPERIMENTAL.md docs licenses
(cd "$release_output" && shasum -a 256 deneb-osx-arm64.tar.gz > SHA256SUMS.txt)
printf 'Archive and checksums: %s\nStaging directory: %s\n' "$release_output" "$staging"
