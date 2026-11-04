#!/usr/bin/env bash
# Установка на целевой Linux-сервер из распакованного дистрибутива (запуск от root):
#   tar xzf scada-mvp-*.tar.gz && cd scada-mvp-* && sudo ./deploy/install.sh
#
# Что делает: пользователь scada, каталоги /opt/scada и /var/lib/scada, файл /etc/scada/scada.env,
# схема БД (если задан PGHOST и т.д. или локальный PostgreSQL), службы systemd.
# Повторный запуск обновляет бинарники и перезапускает службы, данные не трогает.
#
# Переменные:
#   SCADA_DB=scada  SCADA_DB_OWNER=scada_owner   — БД и её владелец (создаются, если нет)
#   SCADA_DEMO=1                                  — загрузить демонстрационные данные (только на пустую БД)
#   SKIP_DB=1                                     — не трогать БД (БД на другом сервере, схема применена вручную)
set -euo pipefail

SRC="$(cd "$(dirname "$0")/.." && pwd)"
DB="${SCADA_DB:-scada}"
OWNER="${SCADA_DB_OWNER:-scada_owner}"

[ "$(id -u)" -eq 0 ] || { echo "Запустите от root"; exit 1; }

echo "==> Пользователь и каталоги"
id scada >/dev/null 2>&1 || useradd --system --home /var/lib/scada --shell /usr/sbin/nologin scada
install -d -o scada -g scada -m 750 /var/lib/scada /var/lib/scada/spool /var/lib/scada/pki
install -d -m 755 /opt/scada
install -d -o root -g scada -m 750 /etc/scada

systemctl stop scada-web scada-acquisition 2>/dev/null || true
rm -rf /opt/scada/acquisition /opt/scada/web
cp -r "$SRC/acquisition" "$SRC/web" /opt/scada/
cp -r "$SRC/db" /opt/scada/
cp "$SRC/README.md" /opt/scada/ 2>/dev/null || true
chmod +x /opt/scada/acquisition/scada-acquisition /opt/scada/web/scada-web

if [ ! -f /etc/scada/scada.env ]; then
  cp "$SRC/deploy/scada.env.example" /etc/scada/scada.env
  APP_PWD="$(head -c 18 /dev/urandom | base64 | tr -d '/+=')"
  ADMIN_PWD="$(head -c 12 /dev/urandom | base64 | tr -d '/+=')"
  sed -i "s#Password=CHANGE_ME#Password=$APP_PWD#; s#InitialAdminPassword=CHANGE_ME_NOW#InitialAdminPassword=$ADMIN_PWD#" /etc/scada/scada.env
  chown root:scada /etc/scada/scada.env
  chmod 640 /etc/scada/scada.env
  echo "    создан /etc/scada/scada.env; начальный пароль admin: $ADMIN_PWD (смените при первом входе)"
  NEW_ENV=1
fi

if [ "${SKIP_DB:-0}" != "1" ]; then
  echo "==> База данных"
  command -v psql >/dev/null || { echo "Нет psql. Установите PostgreSQL 14+ или запустите с SKIP_DB=1"; exit 1; }
  PSQL=(sudo -u postgres psql -v ON_ERROR_STOP=1 -q)
  "${PSQL[@]}" -tc "SELECT 1 FROM pg_roles WHERE rolname='$OWNER'" | grep -q 1 || "${PSQL[@]}" -c "CREATE ROLE $OWNER NOLOGIN"
  "${PSQL[@]}" -tc "SELECT 1 FROM pg_database WHERE datname='$DB'" | grep -q 1 || "${PSQL[@]}" -c "CREATE DATABASE $DB OWNER $OWNER"
  # схема применяется от имени владельца (скрипт идемпотентен)
  "${PSQL[@]}" -d "$DB" -c "SET ROLE $OWNER" -f /opt/scada/db/001_schema.sql
  "${PSQL[@]}" -d "$DB" -c "SET ROLE $OWNER" -f /opt/scada/db/004_publication.sql
  "${PSQL[@]}" -d "$DB" -c "SET ROLE $OWNER" -f /opt/scada/db/005_access.sql
  if [ "${SCADA_DEMO:-0}" = "1" ]; then
    EMPTY="$("${PSQL[@]}" -d "$DB" -tAc "SELECT count(*) = 0 FROM scada.devices")"
    [ "$EMPTY" = "t" ] && "${PSQL[@]}" -d "$DB" -c "SET ROLE $OWNER" -f /opt/scada/db/002_demo.sql  && "${PSQL[@]}" -d "$DB" -c "SET ROLE $OWNER" -f /opt/scada/db/006_demo_access.sql && echo "    загружены демо-данные"
  fi
  "${PSQL[@]}" -d "$DB" -f /opt/scada/db/003_app_role.sql
  if [ "${NEW_ENV:-0}" = "1" ]; then
    "${PSQL[@]}" -c "ALTER ROLE scada_app PASSWORD '$APP_PWD'"
  fi
fi

echo "==> Службы systemd"
cp "$SRC"/deploy/systemd/*.service /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now scada-acquisition scada-web
sleep 3
systemctl --no-pager --lines=5 status scada-acquisition scada-web || true
echo "==> Готово. Интерфейс: http://$(hostname -I | awk '{print $1}'):8080   Журналы: journalctl -u scada-acquisition -u scada-web -f"
