#!/usr/bin/env bash
# =============================================================================
# rebuild.sh —— 从 uber/h3 官方源码重编译 h3.dll（Windows/MinGW 版）
#
# 用途：native/h3/ 下的 h3.dll 是这份脚本的产物。日常开发不需要跑它；
#       只有升级 H3 版本 / 换编译器选项 / 丢了 DLL 时才需要重编译。
#
# 前置条件（PATH 里能直接找到）：
#   * git、cmake（≥3.20）、ninja
#   * MinGW-w64 gcc（x86_64-ucrt-posix 系，winlibs 发行版即可）
#     —— 注意不要用 MSVC 的 cl：H3 的 CMake 两者都支持，但本仓库的 DLL
#        依赖清单（objdump 检查）按 MinGW 版验证过（仅 KERNEL32 + UCRT API 集）。
#
# 用法：
#   bash native/h3/rebuild.sh [版本tag]        # 默认 v4.5.0
#
# 产物：
#   编译源目录          E:/godotGames/third_party/h3-<tag>（不上入库，仅本机缓存）
#   编译中间目录        上述目录的 build-win/
#   最终 DLL            native/h3/h3.dll（libh3.dll 改名，去掉 MinGW 的 lib 前缀）
#   公开头 【不覆盖】    native/h3/h3api.h（build 生成的 h3api.h 与源码 h3api.h.in
#                        同版同构；为防误覆盖已提交的版本，本脚本不拷头文件）
# =============================================================================
set -euo pipefail

VERSION="${1:-v4.5.0}"
REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SRC_ROOT="/e/godotGames/third_party/h3-${VERSION}"

echo "==> [1/4] 获取 uber/h3 ${VERSION} 源码（浅克隆到 ${SRC_ROOT}）"
if [ ! -d "$SRC_ROOT/.git" ]; then
  git clone --depth 1 --branch "$VERSION" https://github.com/uber/h3.git "$SRC_ROOT"
else
  git -C "$SRC_ROOT" fetch --depth 1 origin tag "$VERSION"
  git -C "$SRC_ROOT" checkout --detach "$VERSION"
fi

echo "==> [2/4] CMake 配置（Release / 共享库 / 只编核心库，关掉测试与工具）"
cmake -B "$SRC_ROOT/build-win" -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DBUILD_SHARED_LIBS=ON \
  -DBUILD_TESTING=OFF -DBUILD_BENCHMARKS=OFF -DBUILD_FUZZERS=OFF \
  -DBUILD_FILTERS=OFF -DBUILD_GENERATORS=OFF \
  -DENABLE_LINTING=OFF -DENABLE_DOCS=OFF

echo "==> [3/4] 编译"
cmake --build "$SRC_ROOT/build-win"

echo "==> [4/4] 校验依赖清单 + 拷回仓库"
DLL="$SRC_ROOT/build-win/bin/libh3.dll"
echo "    运行时依赖（期望只有 KERNEL32 和 api-ms-win-crt-*，绝不能出现 libgcc/winpthread）："
objdump -p "$DLL" | grep "DLL Name"
if objdump -p "$DLL" | grep -qiE "libgcc|winpthread|libstdc"; then
  echo "    !! 出现额外运行时依赖，请检查工具链；不满足条件的 DLL 不拷回。"
  exit 1
fi
cp "$DLL" "$REPO_ROOT/native/h3/h3.dll"   # 去掉 MinGW 的 lib 前缀，DllImport("h3") 按此名找
echo "    完成：$REPO_ROOT/native/h3/h3.dll"
echo "    提示：改了 h3api.h 相关签名后，记得同步 scripts/Utils/H3/H3Native.cs 的 DllImport。"