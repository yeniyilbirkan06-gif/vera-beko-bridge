#!/usr/bin/env bash
# vera-beko-bridge Windows x86 publish scripti
# Kullanım: ./publish-win-x86.sh [version]
# Örnek:   ./publish-win-x86.sh 0.2.0
#
# Çıktı: publish/win-x86/vera-beko-bridge.exe (self-contained, ~70MB)
# IntegrationHub.dll x86 → sidecar da x86 (win-x86) publish edilir.

set -e

cd "$(dirname "$0")"

VERSION="${1:-0.2.0}"
OUTPUT="publish/win-x86"

echo "[publish] Temizlik…"
rm -rf "$OUTPUT" bin obj

echo "[publish] Restore…"
dotnet restore

echo "[publish] Publish -r win-x86 --self-contained…"
dotnet publish -c Release -r win-x86 --self-contained true \
    /p:PublishSingleFile=false \
    /p:PublishTrimmed=false \
    /p:Version="$VERSION" \
    -o "$OUTPUT"

# lib/ dizinini publish çıktısına kopyala (IntegrationHub.dll runtime lookup için)
if [ -d "lib" ]; then
    echo "[publish] lib/ dizini kopyalanıyor…"
    cp -r lib "$OUTPUT/"
fi

echo ""
echo "[publish] TAMAM"
echo "[publish] Çıktı: $OUTPUT/vera-beko-bridge.exe"
echo "[publish] Boyut: $(du -sh "$OUTPUT" | cut -f1)"
echo ""
echo "Kurulum: bu klasörü Windows PC'de %LOCALAPPDATA%\\VERA\\beko-bridge\\ altına kopyala"
echo "Başlat:  vera-beko-bridge.exe (Bridge:Secret env veya appsettings.json'da ayarla)"
