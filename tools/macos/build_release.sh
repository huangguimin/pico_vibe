#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
VERSION="${1:-$(cat "$ROOT/app/VERSION")}"
BUILD_NUMBER="${PICO_VIBE_BUILD_NUMBER:-$(date -u +%Y%m%d%H%M)}"
DIST="$ROOT/dist/macos"
WORK="$ROOT/build-pico-vibe-macos"
SIGN_IDENTITY="${APPLE_SIGN_IDENTITY:--}"
NOTARY_PROFILE="${APPLE_NOTARY_PROFILE:-}"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "此脚本必须在 macOS 14.2 或更高版本上运行。" >&2
  exit 1
fi
if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "软件版本必须是 MAJOR.MINOR.PATCH，例如 1.0.4。" >&2
  exit 1
fi
command -v dotnet >/dev/null
command -v xcrun >/dev/null

DEFAULT_MACOS_SDK="$(xcrun --sdk macosx --show-sdk-path)"
COMPATIBLE_MACOS_SDK="$(dirname "$DEFAULT_MACOS_SDK")/MacOSX15.4.sdk"
if [[ -n "${PICO_VIBE_MACOS_SDK:-}" ]]; then
  SWIFT_SDK="$PICO_VIBE_MACOS_SDK"
elif [[ -d "$COMPATIBLE_MACOS_SDK" ]]; then
  # Newer Command Line Tools can temporarily ship a Swift compiler and default
  # SDK from different patch releases. The 15.4 SDK supports our 14.2 target
  # and remains compatible with both Intel and Apple Silicon builds.
  SWIFT_SDK="$COMPATIBLE_MACOS_SDK"
else
  SWIFT_SDK="$DEFAULT_MACOS_SDK"
fi

rm -rf -- "$WORK"
mkdir -p "$WORK" "$DIST"
export CLANG_MODULE_CACHE_PATH="$WORK/clang-module-cache"
mkdir -p "$CLANG_MODULE_CACHE_PATH"
# Intermediate .app bundles live here during release builds; exclude them from
# Spotlight so "Pico Vibe" does not appear multiple times in application search.
: > "$WORK/.metadata_never_index"

dotnet test "$ROOT/app/PicoVibe.Tests/PicoVibe.Tests.csproj" -c Release
dotnet test "$ROOT/app/PicoVibe.App.Tests/PicoVibe.App.Tests.csproj" -c Release
dotnet test "$ROOT/app/PicoVibe.Hook.Tests/PicoVibe.Hook.Tests.csproj" -c Release
dotnet test "$ROOT/app/PicoVibe.Runtime.Tests/PicoVibe.Runtime.Tests.csproj" -c Release
dotnet build "$ROOT/app/PicoVibe.Mac/PicoVibe.Mac.csproj" -c Release

create_icon() {
  local iconset="$WORK/PicoVibe.iconset"
  local source="$ROOT/app/PicoVibe.App/Assets/PicoVibe.png"
  rm -rf -- "$iconset"
  mkdir -p "$iconset"
  for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$source" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    local double=$((size * 2))
    sips -z "$double" "$double" "$source" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
  done
  iconutil -c icns "$iconset" -o "$WORK/PicoVibe.icns"
}

create_icon

for RID in osx-arm64 osx-x64; do
  ARCH="${RID#osx-}"
  case "$ARCH" in
    arm64) SWIFT_TARGET="arm64-apple-macos14.2" ;;
    x64) SWIFT_TARGET="x86_64-apple-macos14.2" ;;
    *) exit 2 ;;
  esac

  APP_PUBLISH="$WORK/publish-app-$RID"
  HOOK_PUBLISH="$WORK/publish-hook-$RID"
  BUNDLE="$WORK/$RID/Pico Vibe.app"
  CONTENTS="$BUNDLE/Contents"
  mkdir -p "$CONTENTS/MacOS" "$CONTENTS/Helpers" "$CONTENTS/Resources/integrations/codex"

  dotnet publish "$ROOT/app/PicoVibe.Mac/PicoVibe.Mac.csproj" -c Release -r "$RID" \
    --self-contained true -o "$APP_PUBLISH" \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=None -p:DebugSymbols=false -p:Version="$VERSION"
  dotnet publish "$ROOT/app/PicoVibe.Hook/PicoVibe.Hook.csproj" -c Release -r "$RID" \
    --self-contained true -o "$HOOK_PUBLISH" \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=None -p:DebugSymbols=false -p:Version="$VERSION"

  /usr/bin/ditto "$APP_PUBLISH" "$CONTENTS/MacOS"
  cp "$HOOK_PUBLISH/PicoVibeHook" "$CONTENTS/Helpers/PicoVibeHook"
  xcrun swiftc -O -parse-as-library -target "$SWIFT_TARGET" -sdk "$SWIFT_SDK" \
    "$ROOT/app/PicoVibe.MacBridge/PicoVibeMacBridge.swift" \
    -o "$CONTENTS/Helpers/PicoVibeMacBridge"
  xcrun clang -O2 -fobjc-arc -fblocks -dynamiclib -target "$SWIFT_TARGET" \
    -framework Foundation \
    "$ROOT/app/PicoVibe.MacBridge/PicoVibeMediaRemote.m" \
    -o "$CONTENTS/Helpers/PicoVibeMediaRemote.dylib"
  cp "$ROOT/app/PicoVibe.MacBridge/PicoVibeMediaRemote.pl" \
    "$CONTENTS/Resources/PicoVibeMediaRemote.pl"
  /usr/bin/ditto "$ROOT/integrations/codex/pico-vibe-status" \
    "$CONTENTS/Resources/integrations/codex/pico-vibe-status"
  cp "$WORK/PicoVibe.icns" "$CONTENTS/Resources/PicoVibe.icns"
  cp "$ROOT/THIRD_PARTY_NOTICES.md" "$CONTENTS/Resources/THIRD_PARTY_NOTICES.md"
  sed -e "s/__VERSION__/$VERSION/g" -e "s/__BUILD__/$BUILD_NUMBER/g" \
    "$ROOT/app/PicoVibe.Mac/Info.plist.in" > "$CONTENTS/Info.plist"
  chmod 755 "$CONTENTS/MacOS/PicoVibe" "$CONTENTS/Helpers/PicoVibeHook" \
    "$CONTENTS/Helpers/PicoVibeMacBridge"
  chmod 644 "$CONTENTS/Resources/PicoVibeMediaRemote.pl"

  if [[ "$SIGN_IDENTITY" == "-" ]]; then
    for native in "$CONTENTS/MacOS"/*.dylib; do
      [[ -e "$native" ]] && codesign --force --sign - "$native"
    done
    codesign --force --sign - --identifier com.picovibe.macbridge \
      "$CONTENTS/Helpers/PicoVibeMacBridge"
    codesign --force --sign - --identifier com.picovibe.mediaremote \
      "$CONTENTS/Helpers/PicoVibeMediaRemote.dylib"
    codesign --force --sign - --identifier com.picovibe.hook \
      "$CONTENTS/Helpers/PicoVibeHook"
    codesign --force --sign - --entitlements "$ROOT/app/PicoVibe.Mac/PicoVibe.entitlements" "$BUNDLE"
  else
    for native in "$CONTENTS/MacOS"/*.dylib; do
      [[ -e "$native" ]] && codesign --force --timestamp --options runtime \
        --sign "$SIGN_IDENTITY" "$native"
    done
    codesign --force --timestamp --options runtime --sign "$SIGN_IDENTITY" \
      --identifier com.picovibe.hook "$CONTENTS/Helpers/PicoVibeHook"
    codesign --force --timestamp --options runtime --sign "$SIGN_IDENTITY" \
      --identifier com.picovibe.macbridge "$CONTENTS/Helpers/PicoVibeMacBridge"
    codesign --force --timestamp --options runtime --sign "$SIGN_IDENTITY" \
      --identifier com.picovibe.mediaremote \
      "$CONTENTS/Helpers/PicoVibeMediaRemote.dylib"
    codesign --force --timestamp --options runtime --sign "$SIGN_IDENTITY" \
      --entitlements "$ROOT/app/PicoVibe.Mac/PicoVibe.entitlements" "$BUNDLE"
  fi
  codesign --verify --deep --strict --verbose=2 "$BUNDLE"

  STAGE="$WORK/dmg-$RID"
  rm -rf -- "$STAGE"
  mkdir -p "$STAGE"
  /usr/bin/ditto "$BUNDLE" "$STAGE/Pico Vibe.app"
  cp "$ROOT/tools/macos/install.command" "$STAGE/安装 Pico Vibe.command"
  cp "$ROOT/tools/macos/uninstall.command" "$STAGE/卸载 Pico Vibe.command"
  chmod 755 "$STAGE"/*.command
  ln -s /Applications "$STAGE/Applications"

  DMG="$DIST/PicoVibe-$VERSION-$RID.dmg"
  # hdiutil's automatic size can leave too little filesystem metadata space
  # for a self-contained .NET bundle, even when the host disk has ample room.
  STAGE_KIB="$(du -sk "$STAGE" | awk '{print $1}')"
  DMG_KIB=$((STAGE_KIB * 2 + 65536))
  (( DMG_KIB < 524288 )) && DMG_KIB=524288
  rm -f -- "$DMG"
  hdiutil create -volname "Pico Vibe $VERSION" -srcfolder "$STAGE" \
    -size "${DMG_KIB}k" -format UDZO -ov "$DMG" >/dev/null
  if [[ "$SIGN_IDENTITY" != "-" ]]; then
    codesign --force --timestamp --sign "$SIGN_IDENTITY" "$DMG"
  fi
  if [[ -n "$NOTARY_PROFILE" ]]; then
    if [[ "$SIGN_IDENTITY" == "-" ]]; then
      echo "APPLE_NOTARY_PROFILE 需要同时设置 APPLE_SIGN_IDENTITY。" >&2
      exit 1
    fi
    xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$DMG"
    xcrun stapler validate "$DMG"
  fi
  shasum -a 256 "$DMG" > "$DMG.sha256"
done

echo "macOS 安装包已生成：$DIST"
if [[ "$SIGN_IDENTITY" == "-" ]]; then
  echo "当前是本机临时签名，仅供开发测试；客户发布必须设置 Developer ID 并公证。"
fi
