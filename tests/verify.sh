#!/usr/bin/env bash
# 一键回归：Rebuild → 主世界 headless 冒烟（零 ERROR）。用法：bash tests/verify.sh。
# 前置：Godot mono 控制台 exe（GODOT_EXE 覆盖）；单测工程由 pre-commit / CI 跑，本脚本不代跑。
set -u
cd "$(dirname "$0")/.." || exit 1

GODOT="${GODOT_EXE:-/d/Godot_v4.7.1-stable_mono_win64/Godot_v4.7.1-stable_mono_win64_console.exe}"
DLL=".godot/mono/temp/bin/Debug/world.dll"

[ -x "$GODOT" ] || { echo "❌ Godot 不可执行: $GODOT（用 GODOT_EXE 指定）"; exit 2; }

echo "═══ [1/2] dotnet build -t:Rebuild ═══"
TS_BEFORE=$(stat -c '%Y' "$DLL" 2>/dev/null || echo 0)
if ! dotnet build -t:Rebuild 2>&1 | tail -8 | grep -q "已成功生成\|Build succeeded\|0 个错误"; then
    echo "❌ 构建失败"; exit 1
fi
TS_AFTER=$(stat -c '%Y' "$DLL" 2>/dev/null || echo 0)
[ "$TS_AFTER" -gt "$TS_BEFORE" ] || { echo "❌ DLL 时间戳未更新（构建静默失败？）"; exit 1; }
echo "✓ DLL 已更新"

echo ""
echo "═══ [2/2] headless 冒烟：主世界 ═══"
OUT=$(timeout 300 "$GODOT" --headless --quit-after 600 --path . "res://scenes/core/WorldGenWorld.tscn" 2>&1)
RC=$?
if [ $RC -ne 0 ]; then
    echo "❌ 主世界：退出码 $RC"; echo "$OUT" | tail -20; exit 1
fi
BAD=$(echo "$OUT" | grep -cE "Cannot load|SCRIPT ERROR|Invalid script|^  FAIL")
if [ "$BAD" -gt 0 ]; then
    echo "❌ 主世界：$BAD 项错误"; echo "$OUT" | grep -E "Cannot load|SCRIPT ERROR|Invalid script|FAIL" | tail -8; exit 1
fi
echo "✓ 主世界冒烟通过"
exit 0
