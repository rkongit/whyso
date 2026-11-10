#!/bin/sh
# Installs the latest Whyso release for macOS or Linux.
#
#   curl -fsSL https://raw.githubusercontent.com/rkongit/whyso/main/install.sh | sh
#
# Optional environment variables:
#   WHYSO_VERSION      version to install, e.g. 0.2.0 (default: latest release)
#   WHYSO_INSTALL_DIR  where to put the binary (default: ~/.local/bin)
#   WHYSO_REPO         GitHub repo to download from (default: rkongit/whyso)
#   WHYSO_BASE_URL     download from this URL instead of the GitHub release (mirrors, testing)
set -eu

repo="${WHYSO_REPO:-rkongit/whyso}"
install_dir="${WHYSO_INSTALL_DIR:-$HOME/.local/bin}"

fail() {
  echo "install.sh: $1" >&2
  exit 1
}

command -v curl >/dev/null 2>&1 || fail "curl is required"
command -v tar >/dev/null 2>&1 || fail "tar is required"

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64 | Linux-amd64) rid="linux-x64" ;;
  Darwin-arm64) rid="osx-arm64" ;;
  Darwin-x86_64) rid="osx-x64" ;;
  *) fail "no prebuilt binary for $(uname -s) $(uname -m); build from source (see README)" ;;
esac

version="${WHYSO_VERSION:-}"
if [ -z "$version" ]; then
  # /releases/latest redirects to /releases/tag/<tag>; read the tag from the final URL.
  latest_url="$(curl -fsSL -o /dev/null -w '%{url_effective}' "https://github.com/$repo/releases/latest")" ||
    fail "could not reach https://github.com/$repo/releases/latest"
  tag="${latest_url##*/}"
  case "$tag" in
    v[0-9]*) ;;
    *) fail "no published release found for $repo" ;;
  esac
  version="${tag#v}"
fi
version="${version#v}"

archive="whyso-$version-$rid.tar.gz"
base="${WHYSO_BASE_URL:-https://github.com/$repo/releases/download/v$version}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Downloading $archive"
curl -fsSL -o "$tmp/$archive" "$base/$archive" || fail "download failed: $base/$archive"
curl -fsSL -o "$tmp/SHA256SUMS.txt" "$base/SHA256SUMS.txt" || fail "download failed: $base/SHA256SUMS.txt"

expected="$(awk -v f="$archive" '$2 == f || $2 == "*" f { print $1 }' "$tmp/SHA256SUMS.txt")"
[ -n "$expected" ] || fail "$archive is not listed in SHA256SUMS.txt"
if command -v sha256sum >/dev/null 2>&1; then
  actual="$(sha256sum "$tmp/$archive" | awk '{ print $1 }')"
elif command -v shasum >/dev/null 2>&1; then
  actual="$(shasum -a 256 "$tmp/$archive" | awk '{ print $1 }')"
else
  fail "sha256sum or shasum is required to verify the download"
fi
[ "$expected" = "$actual" ] || fail "checksum mismatch for $archive (expected $expected, got $actual)"

mkdir -p "$tmp/extract" "$install_dir"
tar -xzf "$tmp/$archive" -C "$tmp/extract"
[ -f "$tmp/extract/whyso" ] || fail "archive does not contain a whyso binary"
mv -f "$tmp/extract/whyso" "$install_dir/whyso"
chmod +x "$install_dir/whyso"

echo "Installed whyso $version to $install_dir/whyso"
case ":$PATH:" in
  *":$install_dir:"*) ;;
  *)
    echo "Add it to your PATH, for example:"
    echo "  export PATH=\"$install_dir:\$PATH\""
    ;;
esac
echo "Next: run 'whyso init' inside a Git project."
