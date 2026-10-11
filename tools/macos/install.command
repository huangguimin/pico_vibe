#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd -P)"
SOURCE_APP="$SCRIPT_DIR/Pico Vibe.app"
INSTALL_ROOT="$HOME/Applications"
TARGET_APP="$INSTALL_ROOT/Pico Vibe.app"
BACKUP_APP="$INSTALL_ROOT/.Pico Vibe.previous.app"

if [[ ! -d "$SOURCE_APP" || ! -x "$SOURCE_APP/Contents/MacOS/PicoVibe" ]]; then
  echo "未找到完整的 Pico Vibe.app。请从 DMG 内运行本脚本。"
  read -r -p "按回车退出。"
  exit 1
fi

mkdir -p "$INSTALL_ROOT"
if [[ -e "$BACKUP_APP" ]]; then
  /bin/rm -rf -- "$BACKUP_APP"
fi
if [[ -e "$TARGET_APP" ]]; then
  /bin/mv -- "$TARGET_APP" "$BACKUP_APP"
fi

if ! /usr/bin/ditto "$SOURCE_APP" "$TARGET_APP"; then
  [[ -e "$BACKUP_APP" ]] && /bin/mv -- "$BACKUP_APP" "$TARGET_APP"
  echo "安装失败，旧版本已恢复。"
  read -r -p "按回车退出。"
  exit 1
fi

if ! /usr/bin/codesign --verify --deep --strict "$TARGET_APP"; then
  /bin/rm -rf -- "$TARGET_APP"
  [[ -e "$BACKUP_APP" ]] && /bin/mv -- "$BACKUP_APP" "$TARGET_APP"
  echo "签名校验失败，旧版本已恢复。"
  read -r -p "按回车退出。"
  exit 1
fi

if [[ -x "$TARGET_APP/Contents/Helpers/PicoVibeHook" ]]; then
  "$TARGET_APP/Contents/Helpers/PicoVibeHook" install-hooks \
    --skill-source "$TARGET_APP/Contents/Resources/integrations/codex/pico-vibe-status" || true
fi

[[ -e "$BACKUP_APP" ]] && /bin/rm -rf -- "$BACKUP_APP"
/usr/bin/open "$TARGET_APP"
echo "Pico Vibe 已安装到 $TARGET_APP"
echo "Codex 用户请打开 /hooks，检查并信任 PicoVibeHook。"
read -r -p "按回车关闭。"
