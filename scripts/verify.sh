#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════
# 一键回归脚本（2026-08-19 参数大扫除系列基建；2026-10-09 存档清退 + CivSim 移出后重标定）
# 用法：bash scripts/verify.sh [--fast]
#   --fast   只跑「构建 + 单测 + 主世界」，跳过诊断场景组
#
# 设计动机（历史 bug 教训）：
#   · 增量 build 可能静默失败（改 C# 后必须 Rebuild + 对比 DLL 时间戳）
#   · 每次改动都要 headless 验证，手动敲命令易漏
#   · 本脚本 = Rebuild → 时间戳断言 → 单测 → headless 回归 → 汇总退出码
#
# ★2026-10-09 两次重标定：
#   ① 存档清退：原 4 组 headless 里 TectonicsTest / MonsoonDiag（场景类早已删除）、
#      LogicGridDiag / CivSimDiag(T 全套)（依赖已删的 `.mpa` 读档）三条均移除。
#   ② CivSim 移出：CivSimDiag（构造场景）随 CivSim 迁至 `scripts/_removed/`（用户拍板
#      "等待正确时机回归，现在不需要"）⇒ 其回归组一并移除。
#   现只保留**现存且无存档依赖**的场景：主世界 + H3 冒烟。
#
# 前置：Godot mono 控制台 exe 路径（可用 GODOT_EXE 环境变量覆盖）
# ═══════════════════════════════════════════════════════════════════
set -u
cd "$(dirname "$0")/.." || exit 1

GODOT="${GODOT_EXE:-/d/Godot_v4.7.1-stable_mono_win64/Godot_v4.7.1-stable_mono_win64_console.exe}"
DLL=".godot/mono/temp/bin/Debug/world.dll"
TESTPROJ="scripts/Test/World.Tests/World.Tests.csproj"
FAST="${1:-}"

[ -x "$GODOT" ] || { echo "❌ Godot 不可执行: $GODOT（用 GODOT_EXE 环境变量指定）"; exit 2; }

echo "═══ [1/3] dotnet build -t:Rebuild ═══"
TS_BEFORE=$(stat -c '%Y' "$DLL" 2>/dev/null || echo 0)
if ! dotnet build -t:Rebuild 2>&1 | tail -8 | grep -q "已成功生成\|Build succeeded\|0 个错误"; then
    echo "❌ 构建失败"; exit 1
fi
TS_AFTER=$(stat -c '%Y' "$DLL" 2>/dev/null || echo 0)
[ "$TS_AFTER" -gt "$TS_BEFORE" ] || { echo "❌ DLL 时间戳未更新（构建静默失败？）"; exit 1; }
echo "✓ DLL 已更新（$(stat -c '%y' "$DLL")）"

echo ""
echo "═══ [2/3] dotnet test ═══"
TEST_OUT=$(dotnet test "$TESTPROJ" --no-build 2>&1)
if echo "$TEST_OUT" | grep -q "已通过!\|Passed!"; then
    echo "$TEST_OUT" | grep -E "已通过!|Passed!" | tail -1
else
    echo "❌ 单元测试失败"
    echo "$TEST_OUT" | grep -E "失败|Failed" | tail -8
    exit 1
fi

FAIL=0
run_scene() {
    local name="$1" scene="$2" args="$3" timeout_s="$4" quit="${5:-}"
    echo ""
    echo "═══ [run] $name ═══"
    local out rc qargs=""
    # quit 非空 → 追加 --quit-after（用于不会自退的常驻场景，如主世界）
    [ -n "$quit" ] && qargs="--quit-after $quit"
    out=$(timeout "$timeout_s" "$GODOT" --headless $qargs --path . "$scene" $args 2>&1)
    rc=$?
    if [ $rc -ne 0 ]; then
        echo "❌ $name：退出码 $rc（timeout=$timeout_s）"
        echo "$out" | tail -15
        FAIL=1
        return
    fi
    local bad
    bad=$(echo "$out" | grep -cE "Cannot load|SCRIPT ERROR|Invalid script|^  FAIL")
    if [ "$bad" -gt 0 ]; then
        echo "❌ $name：$bad 项错误/FAIL"
        echo "$out" | grep -E "Cannot load|SCRIPT ERROR|Invalid script|FAIL" | tail -8
        FAIL=1
    else
        echo "$out" | grep -E "PASS|WORLDGEN-TIMING|通过|Quit" | tail -4
        echo "✓ $name 通过"
    fi
}

echo ""
echo "═══ [3/3] headless 回归 ═══"
run_scene "主世界 WorldGenWorld"   "res://scenes/core/WorldGenWorld.tscn" "" 300 600

if [ "$FAST" != "--fast" ]; then
    run_scene "H3SmokeDiag"          "res://scenes/diag/H3SmokeDiag.tscn" "" 180
else
    echo ""
    echo "（--fast：跳过诊断场景组）"
fi

echo ""
echo "════════════════════════════════════"
if [ $FAIL -eq 0 ]; then echo "🎉 全部回归通过"; else echo "❌ 存在失败项，见上方日志"; fi
echo "════════════════════════════════════"
exit $FAIL
