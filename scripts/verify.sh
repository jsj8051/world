#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════
# 一键回归脚本（2026-08-19，参数大扫除系列基建）
# 用法：bash scripts/verify.sh [--fast]
#   --fast   只跑 n16 快测试（TectonicsTest + LogicGridDiag），跳过 CivSimDiag 全量
#
# 设计动机（历史 bug 教训）：
#   · 增量 build 可能静默失败（改 C# 后必须 Rebuild + 对比 DLL 时间戳）
#   · 每次改动都要 headless 验证，手动敲命令易漏
#   · 本脚本 = Rebuild → 时间戳断言 → 4 组 headless 回归 → 汇总退出码
#
# 前置：Godot mono 控制台 exe 路径（可用 GODOT_EXE 环境变量覆盖）
# ═══════════════════════════════════════════════════════════════════
set -u
cd "$(dirname "$0")/.." || exit 1

GODOT="${GODOT_EXE:-/d/Godot_v4.7.1-stable_mono_win64/Godot_v4.7.1-stable_mono_win64_console.exe}"
DLL=".godot/mono/temp/bin/Debug/world.dll"
FAST="${1:-}"

[ -x "$GODOT" ] || { echo "❌ Godot 不可执行: $GODOT（用 GODOT_EXE 环境变量指定）"; exit 2; }

echo "═══ [1/6] dotnet build -t:Rebuild ═══"
TS_BEFORE=$(stat -c '%Y' "$DLL" 2>/dev/null || echo 0)
if ! dotnet build -t:Rebuild 2>&1 | tail -8 | grep -q "已成功生成\|Build succeeded\|0 个错误"; then
    echo "❌ 构建失败"; exit 1
fi
TS_AFTER=$(stat -c '%Y' "$DLL" 2>/dev/null || echo 0)
[ "$TS_AFTER" -gt "$TS_BEFORE" ] || { echo "❌ DLL 时间戳未更新（构建静默失败？）"; exit 1; }
echo "✓ DLL 已更新（$(stat -c '%y' "$DLL")）"

FAIL=0
run_test() {
    local name="$1" scene="$2" args="$3" timeout_s="$4"
    echo ""
    echo "═══ [run] $name ═══"
    local out
    out=$(timeout "$timeout_s" "$GODOT" --headless --path . "res://scenes/diag/$scene.tscn" $args 2>&1)
    local rc=$?
    if [ $rc -ne 0 ]; then
        echo "❌ $name：退出码 $rc（timeout=$timeout_s）"
        echo "$out" | tail -15
        FAIL=1
    elif echo "$out" | grep -qE "PASS.*FAIL|FAIL "; then
        # CivSimDiag 的 PASS/FAIL 行（T 全套）；其他场景 PASS 即过
        local bad
        bad=$(echo "$out" | grep -cE "^  FAIL|FAIL T[0-9]")
        echo "$out" | grep -E "PASS|FAIL" | tail -6
        if [ "${bad:-0}" -gt 0 ]; then echo "❌ $name：$bad 项 FAIL"; FAIL=1; else echo "✓ $name 通过"; fi
    else
        local sig
        sig=$(echo "$out" | grep -E "PASS|校验|通过|成功|Quit" | tail -4)
        echo "${sig:-（无断言输出，看上面完整日志）}"
        echo "✓ $name 退出码 0"
    fi
}

run_test "TectonicsTest(n16)"  TectonicsTest  "-- --n=16 --plates=6" 180
run_test "LogicGridDiag(n64 往返)" LogicGridDiag "-- --arch=user://maps/regress_v9_n64.mpa" 180
if [ "$FAST" != "--fast" ]; then
    # ⚠️ MonsoonDiag 必须带 --out 到临时档——默认覆盖源档会污染基准（2026-08-19 verify.sh 缺陷修复）
    run_test "MonsoonDiag(n64)"   MonsoonDiag   "-- --arch=user://maps/regress_v9_n64.mpa --out=user://maps/tmp_verify_monsoon.mpa" 240
    run_test "CivSimDiag(T 全套)"  CivSimDiag    "" 600
else
    echo ""
    echo "（--fast：跳过 MonsoonDiag/CivSimDiag）"
fi

echo ""
echo "════════════════════════════════════"
if [ $FAIL -eq 0 ]; then echo "🎉 全部回归通过"; else echo "❌ 存在失败项，见上方日志"; fi
echo "════════════════════════════════════"
exit $FAIL
