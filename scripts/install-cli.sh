#!/bin/sh
# Installs the sw-serverless command from the latest GitHub release (or SW_SERVERLESS_VERSION),
# checking it against the release's SHA256SUMS. Installs to ~/.local/bin unless INSTALL_DIR is set.
set -eu

repo="simplify9/SW-Serverless"
name="sw-serverless"
dir="${INSTALL_DIR:-$HOME/.local/bin}"

os=$(uname -s); arch=$(uname -m)
case "$os" in
  Linux) os=linux; if ldd --version 2>&1 | grep -qi musl; then os=linux-musl; fi ;;
  Darwin) os=osx ;;
  *) echo "Unsupported system $os; download a release from https://github.com/$repo/releases" >&2; exit 1 ;;
esac
case "$arch" in
  x86_64|amd64) arch=x64 ;;
  arm64|aarch64) arch=arm64 ;;
  *) echo "Unsupported architecture $arch" >&2; exit 1 ;;
esac
rid="$os-$arch"

if [ -n "${SW_SERVERLESS_VERSION:-}" ]; then tag="cli-v$SW_SERVERLESS_VERSION"
else tag=$(curl -fsSL "https://api.github.com/repos/$repo/releases" | grep -o '"tag_name": *"cli-v[^"]*"' | head -1 | sed 's/.*"\(cli-v[^"]*\)"/\1/'); fi
[ -n "$tag" ] || { echo "No sw-serverless release found" >&2; exit 1; }

work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
base="https://github.com/$repo/releases/download/$tag"
curl -fsSL "$base/$name-$rid.tar.gz" -o "$work/$name-$rid.tar.gz"
curl -fsSL "$base/SHA256SUMS" -o "$work/SHA256SUMS"
(cd "$work" && grep " $name-$rid.tar.gz\$" SHA256SUMS | (sha256sum -c - 2>/dev/null || shasum -a 256 -c -)) >/dev/null \
  || { echo "Checksum mismatch for $name-$rid.tar.gz" >&2; exit 1; }

mkdir -p "$work/x" "$dir"
tar -xzf "$work/$name-$rid.tar.gz" -C "$work/x"
install -m 755 "$work/x/$name" "$dir/$name"
echo "Installed $name ${tag#cli-v} to $dir/$name"
case ":$PATH:" in *":$dir:"*) ;; *) echo "Add $dir to your PATH." ;; esac
