#!/bin/sh
# Builds OpenVPN Client and installs it for the current user (no root needed):
#   ~/.local/lib/ovpn-client/            the app
#   ~/.local/bin/ovpn-client             launcher
#   ~/.local/share/applications/...      app menu entry (also handles "Open With" for .ovpn)
#
#   ./install.sh            install / update (builds from source, or uses the
#                           prebuilt binary when run from a release tarball)
#   ./install.sh --uninstall
set -eu

APP_ID=io.github.ovpnclient.OvpnClient
PREFIX="${XDG_DATA_HOME:-$HOME/.local/share}"
LIB_DIR="$HOME/.local/lib/ovpn-client"
BIN_LINK="$HOME/.local/bin/ovpn-client"
DESKTOP="$PREFIX/applications/$APP_ID.desktop"
METAINFO="$PREFIX/metainfo/$APP_ID.metainfo.xml"
HERE="$(cd "$(dirname "$0")" && pwd)"

if [ "${1:-}" = "--uninstall" ]; then
    rm -rf "$LIB_DIR" "$BIN_LINK" "$DESKTOP" "$METAINFO"
    update-desktop-database "$PREFIX/applications" 2>/dev/null || true
    echo "Uninstalled. Your imported profiles are kept in $PREFIX/ovpn-client (delete it to remove them)."
    exit 0
fi

command -v openvpn >/dev/null || [ -x /usr/sbin/openvpn ] || echo "Note: openvpn is not installed yet. Install it with: sudo apt install openvpn"

rm -rf "$LIB_DIR"
if [ -x "$HERE/ovpn-client" ] && [ ! -f "$HERE/OvpnClient.csproj" ]; then
    # Release tarball: prebuilt self-contained binary, nothing to build.
    mkdir -p "$LIB_DIR"
    cp "$HERE/ovpn-client" "$LIB_DIR/ovpn-client"
else
    command -v dotnet >/dev/null || { echo "dotnet not found. Install it with: sudo apt install dotnet-sdk-10.0"; exit 1; }
    echo "Building…"
    dotnet publish "$HERE/OvpnClient.csproj" -c Release -o "$LIB_DIR" --nologo -v q
fi

mkdir -p "$(dirname "$BIN_LINK")" "$(dirname "$DESKTOP")"
ln -sf "$LIB_DIR/ovpn-client" "$BIN_LINK"

sed "s|@EXEC@|$LIB_DIR/ovpn-client|" "$HERE/data/$APP_ID.desktop.in" > "$DESKTOP"
mkdir -p "$(dirname "$METAINFO")"
cp "$HERE/data/$APP_ID.metainfo.xml" "$METAINFO"

update-desktop-database "$PREFIX/applications" 2>/dev/null || true

echo "Installed. Launch “OpenVPN Client” from the app grid, or run: ovpn-client"
