#!/bin/bash
# Build the APK: libff9.so (host as a library), Mono's native libraries, the runtime zip
# (.NET 8 class libraries + FF9Engine.dll), then Gradle (release, signed when the key exists).
# Nothing from the game goes in: the player's copy is imported on the device.
# usage: bash tools/build_apk.sh  ->  android/app/build/outputs/apk/release/app-release.apk
set -e
cd "$(dirname "$0")/.."
SDK=${ANDROID_SDK:-$LOCALAPPDATA/Android/Sdk}
TC=$SDK/ndk/28.2.13676358/toolchains/llvm/prebuilt/windows-x86_64/bin
N=dl/mono/pkg/runtimes/android-arm64/native
L=dl/mono/pkg/runtimes/android-arm64/lib/net8.0
OUT=build/apk
rm -rf $OUT && mkdir -p $OUT/jni/arm64-v8a $OUT/assets $OUT/runtime/bcl $OUT/runtime/engine

bash tools/build_engine.sh
[ -f out/soloud/libsoloud.a ] || bash tools/build_soloud.sh
[ -f out/ffmpeg/lib/libavcodec.a ] || bash tools/build_ffmpeg.sh
[ -f out/host/ue_thunks.S ] || py tools/gen_thunks.py out/host

# libff9.so: the same host, as a shared library with the JNI entry points
PREFIX="-ffile-prefix-map=$(pwd -W 2>/dev/null || pwd)=ff9recomp"
"$TC/clang++.exe" --target=aarch64-linux-android30 -O2 -fPIC -std=c++17 $PREFIX -c src/host/lowmem.cpp -o out/host/lowmem.pic.o
"$TC/clang++.exe" --target=aarch64-linux-android30 -O2 -fPIC -std=c++17 -w $PREFIX -DWITH_MINIAUDIO -Iout/soloud/src-copy/include \
  -c src/host/soloud_ext.cpp -o out/host/soloud_ext.pic.o
"$TC/clang.exe" --target=aarch64-linux-android30 -O2 -fPIC -shared -DFF9_LIBRARY $PREFIX -Wall -Wno-unused-function \
  -I$N/include/mono-2.0 -Iout/ffmpeg/include \
  src/host/*.c out/host/ue_stubs.c out/host/ue_thunks.S out/host/lowmem.pic.o out/host/soloud_ext.pic.o \
  -L$N -lmonosgen-2.0 -lm -Wl,--whole-archive out/soloud/libsoloud.a -Wl,--no-whole-archive \
  -Lout/ffmpeg/lib -lavformat -lavcodec -lswscale -lswresample -lavutil -lz -lGLESv3 -lEGL \
  -lc++_static -lc++abi -lOpenSLES -laaudio -llog -landroid -Wl,--exclude-libs,libavformat.a:libavcodec.a:libswscale.a:libswresample.a:libavutil.a -Wl,--export-dynamic -Wl,-soname,libff9.so \
  -o $OUT/jni/arm64-v8a/libff9.so
"$TC/llvm-strip.exe" --strip-debug $OUT/jni/arm64-v8a/libff9.so

# libff9launcher.so: the launcher's own native code (.7z mod unpacking, LZMA SDK, public domain)
"$TC/clang.exe" --target=aarch64-linux-android30 -O2 -fPIC -shared $PREFIX -w -Ithird_party/lzma   src/launcher/un7z.c third_party/lzma/*.c -llog -Wl,-soname,libff9launcher.so -o $OUT/jni/arm64-v8a/libff9launcher.so
"$TC/llvm-strip.exe" --strip-debug $OUT/jni/arm64-v8a/libff9launcher.so

# libff9fg.so: frame generation, loaded only when it is on
[ out/framegen/libff9fg.so -nt src/framegen/framegen.cpp ] || bash tools/build_framegen.sh
cp out/framegen/libff9fg.so $OUT/jni/arm64-v8a/

# Mono's native libraries (the runtime and its components)
cp $N/*.so $OUT/jni/arm64-v8a/
# class libraries and the engine, unpacked on first start
cp $L/*.dll $N/System.Private.CoreLib.dll $OUT/runtime/bcl/
cp out/engine/FF9Engine.dll $OUT/runtime/engine/
(cd $OUT/runtime && "$(command -v zip || echo none)" -qr ../assets/runtime.zip . 2>/dev/null) || \
  py -c "import shutil; shutil.make_archive('$OUT/assets/runtime', 'zip', '$OUT/runtime')"

# never ship this PC's user name
if grep -rl "$(whoami)" $OUT/jni $OUT/assets 2>/dev/null; then echo "user name found in the APK inputs"; exit 1; fi

cd android
JAVA_HOME="${JAVA_HOME:-$(ls -d "/c/Program Files/Microsoft"/jdk-17* "/c/Program Files/Eclipse Adoptium"/jdk-17* "/c/Program Files/Android/Android Studio/jbr" 2>/dev/null | head -1)}" \
  ./gradlew -q assembleRelease || { echo "gradle failed once (Windows file lock?), retrying"; sleep 3; JAVA_HOME="${JAVA_HOME:-$(ls -d "/c/Program Files/Microsoft"/jdk-17* "/c/Program Files/Eclipse Adoptium"/jdk-17* "/c/Program Files/Android/Android Studio/jbr" 2>/dev/null | head -1)}" ./gradlew -q assembleRelease; }
ls -la app/build/outputs/apk/release/
