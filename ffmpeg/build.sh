#!/usr/bin/env bash
# Cross-compiles a minimal LGPL ffmpeg.exe for win64 on Ubuntu (mingw-w64).
# VideoCatcher only uses ffmpeg for stream copy (-c copy), so it keeps network protocols, demuxers, muxers,
# parsers and bitstream filters, plus a few decoders that stream probing needs. No encoders, no GPL parts.
#
# Usage: ffmpeg/build.sh <output-dir>   -> <output-dir>/ffmpeg.exe, <output-dir>/ffmpeg-LICENSE.txt
# The release workflow caches the output keyed on this file's hash, so bump versions here.
set -euo pipefail

# Sources are fetched by commit so a moved tag can't change what gets built.
FFMPEG_TAG=n9.0.2
FFMPEG_COMMIT=946fcce07b6dcd0331c8cc609192aeff5e1924f8
LIBXML2_TAG=v2.15.4
LIBXML2_COMMIT=96498992efa48d52b0e8b83058bd88dbdaf153c1

HOST=x86_64-w64-mingw32
OUT=$(realpath -m "${1:?usage: build.sh <output-dir>}")
WORK=$(mktemp -d)
PREFIX=$WORK/prefix
JOBS=$(nproc)

fetch() { # <repo-url> <commit> <dir>
    git init -q "$3"
    git -C "$3" fetch -q --depth 1 "$1" "$2"
    git -C "$3" checkout -q FETCH_HEAD
}

# libxml2 is only needed by the DASH (mpd) demuxer.
fetch https://github.com/GNOME/libxml2.git "$LIBXML2_COMMIT" "$WORK/libxml2"
cmake -S "$WORK/libxml2" -B "$WORK/libxml2/build" -DCMAKE_BUILD_TYPE=MinSizeRel \
    -DCMAKE_SYSTEM_NAME=Windows -DCMAKE_C_COMPILER=$HOST-gcc -DCMAKE_RC_COMPILER=$HOST-windres \
    -DCMAKE_INSTALL_PREFIX="$PREFIX" -DBUILD_SHARED_LIBS=OFF \
    -DLIBXML2_WITH_PYTHON=OFF -DLIBXML2_WITH_PROGRAMS=OFF -DLIBXML2_WITH_TESTS=OFF -DLIBXML2_WITH_DOCS=OFF \
    -DLIBXML2_WITH_HTTP=OFF -DLIBXML2_WITH_ICONV=OFF -DLIBXML2_WITH_ICU=OFF -DLIBXML2_WITH_MODULES=OFF \
    -DLIBXML2_WITH_CATALOG=OFF -DLIBXML2_WITH_READLINE=OFF -DLIBXML2_WITH_DEBUG=OFF
cmake --build "$WORK/libxml2/build" -j "$JOBS"
cmake --install "$WORK/libxml2/build"

fetch https://github.com/FFmpeg/FFmpeg.git "$FFMPEG_COMMIT" "$WORK/ffmpeg"
cd "$WORK/ffmpeg"
# Without tags in the shallow fetch, `ffmpeg -version` would report the commit hash instead of the release.
echo "${FFMPEG_TAG#n}" > VERSION
# configure probes for <libxml2/libxml/xmlversion.h>, but libxml-2.0.pc only adds include/libxml2;
# native builds get the parent from the default /usr/include, a cross build needs it spelled out.
# The config.log tail is the only way to see why a probe failed in CI.
PKG_CONFIG_LIBDIR="$PREFIX/lib/pkgconfig" ./configure \
    --target-os=mingw32 --arch=x86_64 --cross-prefix=$HOST- \
    --pkg-config=pkg-config --pkg-config-flags=--static \
    --extra-cflags="-I$PREFIX/include" --extra-ldflags=-static \
    --disable-everything --disable-autodetect --disable-debug --disable-doc --disable-x86asm \
    --disable-ffplay --disable-ffprobe --disable-avdevice \
    --enable-small --enable-w32threads --enable-schannel --enable-libxml2 \
    --enable-protocol=file,pipe,http,https,tcp,tls,httpproxy,crypto,data \
    --enable-demuxer=hls,dash,mov,matroska,flv,live_flv,mpegts,avi,asf,aac,mp3 \
    --enable-muxer=mp4,ipod,mov,matroska,webm,flv,mpegts,avi,asf \
    --enable-parser=h264,hevc,aac,av1,vp9,opus,mpegaudio,ac3 \
    --enable-bsf=aac_adtstoasc,extract_extradata,h264_mp4toannexb,hevc_mp4toannexb,vp9_superframe \
    --enable-decoder=h264,hevc,aac,mp3 \
    || { tail -n 60 ffbuild/config.log; exit 1; }
make -j "$JOBS" ffmpeg.exe
$HOST-strip ffmpeg.exe

mkdir -p "$OUT"
cp ffmpeg.exe "$OUT/"
cp COPYING.LGPLv2.1 "$OUT/ffmpeg-LICENSE.txt"
ls -l "$OUT"
