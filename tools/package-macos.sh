#!/bin/sh
# Packages a self-contained macOS build of LlamaTree (dotnet publish -f net10.0 -r osx-<arch> --self-contained) as
#   LlamaTree-<version>-macos-<arch>.dmg   LlamaTree.app, and a link to Applications to drag it to
# signed with our Developer ID and notarized by Apple, so Gatekeeper opens it. Runs on a Mac (codesign, diskutil and
# notarytool are Apple's); the release pipeline runs it on a hosted macOS agent (azure-pipelines.yml).
#
#   tools/package-macos.sh <publish folder> <version> <x64|arm64> <output folder>
#
#   MACOS_SIGN_IDENTITY   the signing identity, "Developer ID Application: Llamachant Technology Ltd. (N83JJRC3Y5)", in
#                         a keychain codesign searches. Unset: an ad-hoc signature, for trying the bundle on this Mac
#                         (and in CI); such a copy isn't notarized, and another Mac won't open it.
#   MACOS_NOTARY_KEY, MACOS_NOTARY_KEY_ID, MACOS_NOTARY_ISSUER
#                         the App Store Connect API key (.p8 file), its key ID and issuer ID: notarize with them. Unset:
#                         not notarized (only with an identity: a release must be both).
#
# The bundle: Contents/MacOS holds the program and the .NET runtime, each file signed on its own (macOS treats
# everything there as code); the page (wwwroot) and the icon are in Contents/Resources, where the app looks for them
# (src/LlamaTree/Platform/AppFiles.cs) and the bundle's signature seals them. The app is signed with the hardened
# runtime and installer/macos/LlamaTree.entitlements, timestamped, notarized and stapled; then the disk image is too.

set -eu

[ $# -eq 4 ] || { echo "Usage: $0 <publish folder> <version> <x64|arm64> <output folder>" >&2; exit 2; }
publish=$1
version=$2
arch=$3
output=$4
case "$arch" in x64 | arm64) ;; *) echo "Unknown architecture $arch: x64 or arm64." >&2; exit 2 ;; esac
[ "$(uname -s)" = Darwin ] || { echo "This runs on a Mac: codesign, diskutil and notarytool are macOS's own." >&2; exit 2; }
[ -f "$publish/LlamaTree" ] || { echo "$publish has no LlamaTree: publish the net10.0 build for osx-$arch there first." >&2; exit 1; }
[ -f "$publish/Photino.Native.dylib" ] || { echo "$publish has no Photino.Native.dylib: publish it with -r osx-$arch." >&2; exit 1; }

here=$(cd "$(dirname "$0")/.." && pwd)
macos="$here/installer/macos"
identity=${MACOS_SIGN_IDENTITY:-}
notarize=""
if [ -n "${MACOS_NOTARY_KEY:-}${MACOS_NOTARY_KEY_ID:-}${MACOS_NOTARY_ISSUER:-}" ]; then
    [ -n "${MACOS_NOTARY_KEY:-}" ] && [ -n "${MACOS_NOTARY_KEY_ID:-}" ] && [ -n "${MACOS_NOTARY_ISSUER:-}" ] ||
        { echo "Notarizing needs all three of MACOS_NOTARY_KEY, MACOS_NOTARY_KEY_ID and MACOS_NOTARY_ISSUER." >&2; exit 2; }
    [ -n "$identity" ] || { echo "Notarizing needs MACOS_SIGN_IDENTITY: Apple only notarizes Developer ID signed apps." >&2; exit 2; }
    notarize=1
fi

mkdir -p "$output"
output=$(cd "$output" && pwd)
work=$(mktemp -d "${TMPDIR:-/tmp}/llamatree-macos.XXXXXX")
trap 'rm -rf "$work"' EXIT
app="$work/LlamaTree.app"
dmg="$output/LlamaTree-$version-macos-$arch.dmg"

# ---------------------------------------------------------------- the bundle

echo "Making LlamaTree.app ($version, $arch)"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
ditto "$publish" "$app/Contents/MacOS"
for item in wwwroot app.png; do
    mv "$app/Contents/MacOS/$item" "$app/Contents/Resources/$item"
done
cp "$macos/LlamaTree.icns" "$app/Contents/Resources/LlamaTree.icns"
sed "s/__VERSION__/$version/g" "$macos/Info.plist" >"$app/Contents/Info.plist"
plutil -lint "$app/Contents/Info.plist" >/dev/null
printf 'APPL????' >"$app/Contents/PkgInfo"
chmod 755 "$app/Contents/MacOS/LlamaTree"
[ -f "$app/Contents/MacOS/createdump" ] && chmod 755 "$app/Contents/MacOS/createdump"
# Anything Finder or a download left on the files (quarantine, resource forks) makes codesign refuse them.
xattr -cr "$app"

# ---------------------------------------------------------------- signing

if [ -n "$identity" ]; then
    echo "Signing as $identity"
    set -- --force --timestamp --options runtime --sign "$identity"
else
    echo "Signing ad hoc (no MACOS_SIGN_IDENTITY): for this Mac only"
    set -- --force --timestamp=none --options runtime --sign -
fi
# Inside out: every file in Contents/MacOS but the program, then the program with its entitlements, then the bundle,
# which seals Contents/Resources and the rest. A file that isn't code is signed too (its signature is kept in extended
# attributes): codesign won't seal a bundle with an unsigned file among its code.
find "$app/Contents/MacOS" -type f ! -path "$app/Contents/MacOS/LlamaTree" -print0 | xargs -0 -n 20 codesign "$@"
codesign "$@" --entitlements "$macos/LlamaTree.entitlements" "$app/Contents/MacOS/LlamaTree"
codesign "$@" --entitlements "$macos/LlamaTree.entitlements" "$app"
codesign --verify --deep --strict --verbose=2 "$app"

# ---------------------------------------------------------------- notarizing

# Sends a file to Apple's notary service and waits for its answer; on anything but Accepted, prints Apple's log of
# what it found and fails.
notarize_file() {   # file
    echo "Notarizing $(basename "$1")"
    xcrun notarytool submit "$1" --key "$MACOS_NOTARY_KEY" --key-id "$MACOS_NOTARY_KEY_ID" --issuer "$MACOS_NOTARY_ISSUER" \
        --wait --timeout 1h --output-format json >"$work/notary.json" || true
    status=$(plutil -extract status raw -o - "$work/notary.json" 2>/dev/null || echo unknown)
    id=$(plutil -extract id raw -o - "$work/notary.json" 2>/dev/null || true)
    echo "  $status${id:+ ($id)}"
    if [ "$status" != Accepted ]; then
        cat "$work/notary.json" >&2 || true
        [ -n "$id" ] && xcrun notarytool log "$id" --key "$MACOS_NOTARY_KEY" --key-id "$MACOS_NOTARY_KEY_ID" --issuer "$MACOS_NOTARY_ISSUER" >&2 || true
        echo "Apple didn't notarize $(basename "$1")." >&2
        exit 1
    fi
}

# The app first, so its own ticket can be stapled to it: a copy dragged out of the disk image opens offline too.
if [ -n "$notarize" ]; then
    ditto -c -k --keepParent "$app" "$work/LlamaTree.zip"
    notarize_file "$work/LlamaTree.zip"
    xcrun stapler staple "$app"
    xcrun stapler validate "$app"
fi

# ---------------------------------------------------------------- the disk image

echo "Making $(basename "$dmg")"
mkdir "$work/image"
ditto "$app" "$work/image/LlamaTree.app"
ln -s /Applications "$work/image/Applications"
rm -f "$dmg"
# macOS 27's diskutil image (an APFS image; every macOS the app runs on reads it), where hdiutil create is deprecated;
# hdiutil on older Macs, as the build agents are.
if diskutil image --help >/dev/null 2>&1; then
    diskutil image create from --format UDZO --volumeName LlamaTree "$work/image" "$dmg" >/dev/null
else
    hdiutil create -volname LlamaTree -srcfolder "$work/image" -fs HFS+ -format UDZO -imagekey zlib-level=9 -ov "$dmg" >/dev/null
fi
if [ -n "$identity" ]; then
    codesign --force --timestamp --sign "$identity" "$dmg"
    codesign --verify --strict --verbose=2 "$dmg"
fi
if [ -n "$notarize" ]; then
    notarize_file "$dmg"
    xcrun stapler staple "$dmg"
    xcrun stapler validate "$dmg"
    # What Gatekeeper will say to someone who downloads it.
    spctl --assess --type open --context context:primary-signature --verbose=2 "$dmg"
    spctl --assess --type execute --verbose=2 "$app"
fi
echo "Made $dmg ($(du -h "$dmg" | cut -f1 | tr -d ' '))"
