#!/bin/bash
# Build out/framegen/libff9fg.so (src/framegen): frame generation on lsfg-vk's engine (MIT, the
# performance variant) and volk (MIT), fetched into dl/ at the commits below. Nothing of Lossless
# Scaling's goes in: its shaders are read from the player's own Lossless.dll on the device.
set -e
cd "$(dirname "$0")/.."
SDK=${ANDROID_SDK:-$LOCALAPPDATA/Android/Sdk}
TC=$SDK/ndk/28.2.13676358/toolchains/llvm/prebuilt/windows-x86_64/bin
pin() {   # pin <dir> <url> <commit>
  local d=dl/$1
  [ -d "$d/.git" ] && [ "$(git -C $d rev-parse HEAD)" = "$3" ] && return 0
  rm -rf "$d"; mkdir -p "$d"
  git -C "$d" init -q; git -C "$d" remote add origin "$2"
  git -C "$d" fetch -q --depth 1 origin "$3"
  git -C "$d" -c advice.detachedHead=false checkout -q FETCH_HEAD
  for p in patches/$1/*.patch; do [ -f "$p" ] && git -C "$d" apply "$(pwd -W 2>/dev/null || pwd)/$p"; done
  return 0
}
pin lsfg-vk-android https://github.com/FrankBarretta/lsfg-vk-android.git 3e89e5439a98f55d5acb003d20039426ab24e69c
pin volk https://github.com/zeux/volk.git be3dbd49bf77052665e96b6c7484af855e7e5f67

O=out/framegen; F=dl/lsfg-vk-android/framegen
rm -rf $O && mkdir -p $O
PREFIX="-ffile-prefix-map=$(pwd -W 2>/dev/null || pwd)=ff9recomp"
FLAGS="--target=aarch64-linux-android30 -O2 -fPIC -fvisibility=hidden $PREFIX"
INC="-I$F/public -I$F/include -I$F/v3.1p_include -Idl/volk"
"$TC/clang.exe" $FLAGS -w -DVK_USE_PLATFORM_ANDROID_KHR=1 -Idl/volk -c dl/volk/volk.c -o $O/volk.o
n=0
for f in $F/src/common/*.cpp $F/src/core/*.cpp $F/src/pool/*.cpp $F/v3.1p_src/*.cpp $F/v3.1p_src/shaders/*.cpp; do
  n=$((n + 1))
  "$TC/clang++.exe" $FLAGS -std=c++20 -w -DVK_USE_PLATFORM_ANDROID_KHR=1 -DVK_NO_PROTOTYPES $INC -c $f -o $O/lsfg$n.o &
  [ $((n % 8)) = 0 ] && wait
done
wait
"$TC/clang++.exe" $FLAGS -std=c++20 -Wall -I$F/public -c src/framegen/framegen.cpp -o $O/framegen.o
"$TC/clang++.exe" --target=aarch64-linux-android30 -shared -static-libstdc++ -Wl,--exclude-libs,ALL -Wl,-z,max-page-size=16384 -Wl,--no-undefined \
  $O/*.o -lEGL -lGLESv3 -landroid -lnativewindow -llog -ldl -lm -Wl,-soname,libff9fg.so -o $O/libff9fg.so
"$TC/llvm-strip.exe" --strip-debug $O/libff9fg.so
ls -l $O/libff9fg.so
