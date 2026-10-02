#!/usr/bin/env bash
# 薄席解算器性能基准（设计-08 §0.3）：res1/res2/res3 单次 Solve 墙钟 + 非线性/CG 轮数。
#
# 用途：批次 1/2/3 的**验收标尺**——改前改后跑同一条命令，逐分辨率对表。
# 口径：影子解算（--shadow=1，只解一步，不驱动生产路径），与 08 §0.3 的读数同源。
#
#   bash scripts/bench_sheet.sh              # 默认 res 1 2 3
#   RES_LIST="3" bash scripts/bench_sheet.sh # 只跑 res3
#   PLATES=15 SEED=42 bash scripts/bench_sheet.sh
set -u

EXE="${GODOT_EXE:-D:/Godot_v4.7.1-stable_mono_win64/Godot_v4.7.1-stable_mono_win64_console.exe}"
# ⚠️ Godot 是原生 Windows 程序且本机 MSYS 不做路径转换：--path 必须给 Windows 形式（pwd -W）
PROJ="${WORLD_PROJ:-$(cd "$(dirname "$0")/.." && pwd -W 2>/dev/null || pwd)}"
RES_LIST="${RES_LIST:-1 2 3}"
PLATES="${PLATES:-15}"
SEED="${SEED:-42}"

echo "== 薄席单次解算基准（shadow 口径；res=${RES_LIST} P=${PLATES} seed=${SEED}）=="
for r in $RES_LIST; do
	"$EXE" --headless --path "$PROJ" res://scenes/diag/HexDynamicDiag.tscn \
		-- "--res=$r" "--seed=$SEED" "--plates=$PLATES" --run=4 --step=4 --shadow=1 \
		| grep -E "^=== HexDynamicDiag|薄席单次解算"
done
