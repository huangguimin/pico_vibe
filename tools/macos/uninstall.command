#!/bin/bash
set -euo pipefail

TARGET_APP="$HOME/Applications/Pico Vibe.app"
if [[ -x "$TARGET_APP/Contents/Helpers/PicoVibeHook" ]]; then
  "$TARGET_APP/Contents/Helpers/PicoVibeHook" uninstall-hooks || true
fi

/usr/bin/pkill -x PicoVibe 2>/dev/null || true
if [[ -e "$TARGET_APP" ]]; then
  TRASH_TARGET="$HOME/.Trash/Pico Vibe-$(date +%Y%m%d-%H%M%S).app"
  /bin/mv -- "$TARGET_APP" "$TRASH_TARGET"
  echo "应用已移到废纸篓：$TRASH_TARGET"
else
  echo "未找到 $TARGET_APP"
fi
echo "用户配置仍保留在 ~/Library/Application Support/PicoVibe。"
read -r -p "按回车关闭。"
