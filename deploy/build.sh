#!/usr/bin/env bash
# Сборка дистрибутива на машине разработчика / CI.
# Результат: dist/scada-mvp-<версия>-linux-x64.tar.gz — самодостаточные бинарники
# (на целевом сервере .NET устанавливать не нужно — важно для закрытого контура).
#
#   ./deploy/build.sh                # с драйвером OPC UA
#   SCADA_OPCUA=false ./deploy/build.sh   # без OPC UA (до решения по лицензии стека)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${RID:-linux-x64}"
OPCUA="${SCADA_OPCUA:-true}"
VERSION="$(grep -oP '(?<=<Version>)[^<]+' "$ROOT/backend/Directory.Build.props")"
OUT="$ROOT/dist/scada-mvp-$VERSION-$RID"

echo "==> Фронтенд"
cd "$ROOT/frontend"
npm ci --no-audit --no-fund
npm run build          # кладёт сборку в backend/src/Scada.Web/wwwroot

echo "==> Тесты"
cd "$ROOT/backend"
dotnet test tests/Scada.Tests/Scada.Tests.csproj -c Release

echo "==> Публикация ($RID, OPC UA: $OPCUA)"
rm -rf "$OUT"
dotnet publish src/Scada.Acquisition/Scada.Acquisition.csproj -c Release -r "$RID" --self-contained \
  -p:ScadaOpcUa="$OPCUA" -p:PublishSingleFile=false -o "$OUT/acquisition"
dotnet publish src/Scada.Web/Scada.Web.csproj -c Release -r "$RID" --self-contained -o "$OUT/web"

mkdir -p "$OUT/db" "$OUT/deploy"
cp "$ROOT"/db/*.sql "$OUT/db/"
cp -r "$ROOT/deploy/systemd" "$ROOT/deploy/scada.env.example" "$ROOT/deploy/install.sh" "$OUT/deploy/"
cp "$ROOT/README.md" "$OUT/"

tar -C "$ROOT/dist" -czf "$OUT.tar.gz" "$(basename "$OUT")"
echo "==> Готово: $OUT.tar.gz"
