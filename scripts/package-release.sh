#!/bin/bash
# Run from the repository root after publishing osx-arm64.
set -euo pipefail
test -f Deneb.slnx
test -x artifacts/osx-arm64/deneb
package_cache="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
staging="$(mktemp -d "${TMPDIR:-/tmp}/deneb-release.XXXXXX")"
mkdir -p artifacts/release "$staging/licenses"
cp artifacts/osx-arm64/deneb README.md README.ru.md LICENSE CHANGELOG.md THIRD-PARTY-NOTICES.md "$staging/"
cp -R docs "$staging/docs"
cp docs/licenses/*.txt "$staging/licenses/"
for package in microsoft.netcore.app.runtime.osx-arm64/10.0.8 system.management/9.0.4 system.codedom/9.0.4; do
  label="${package//\//-}"
  cp "$package_cache/$package/LICENSE.TXT" "$staging/licenses/$label-LICENSE.txt"
  cp "$package_cache/$package/THIRD-PARTY-NOTICES.TXT" "$staging/licenses/$label-NOTICES.txt"
done
COPYFILE_DISABLE=1 tar -czf artifacts/release/deneb-osx-arm64.tar.gz -C "$staging" deneb README.md README.ru.md LICENSE CHANGELOG.md THIRD-PARTY-NOTICES.md docs licenses
(cd artifacts/release && shasum -a 256 deneb-osx-arm64.tar.gz > SHA256SUMS.txt)
printf 'Archive and checksums: artifacts/release/\nStaging directory: %s\n' "$staging"
