-- =====================================================================
--  SCADA MVP — схема PostgreSQL (14+)
--  Единая база: конфигурация + оперативные значения + архив + журналы.
--  Обмен между процессами: LISTEN/NOTIFY (каналы описаны в конце файла).
-- =====================================================================

SET client_min_messages = warning;
CREATE SCHEMA IF NOT EXISTS scada;
SET search_path = scada, public;

-- ---------------------------------------------------------------------
-- Служебное
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS schema_version (
    version     int PRIMARY KEY,
    applied_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS settings (
    key         text PRIMARY KEY,
    value       jsonb NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------
-- Пользователи и роли
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS users (
    id             serial PRIMARY KEY,
    login          text NOT NULL UNIQUE,
    display_name   text NOT NULL DEFAULT '',
    password_hash  text NOT NULL,
    role           text NOT NULL CHECK (role IN ('viewer','operator','engineer','admin')),
    enabled        boolean NOT NULL DEFAULT true,
    must_change_password boolean NOT NULL DEFAULT false,
    created_at     timestamptz NOT NULL DEFAULT now(),
    last_login_at  timestamptz
);

-- ---------------------------------------------------------------------
-- Конфигурация оборудования
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS devices (
    id            serial PRIMARY KEY,
    name          text NOT NULL UNIQUE,
    protocol      text NOT NULL CHECK (protocol IN ('simulator','modbus_tcp','opcua')),
    endpoint      text NOT NULL DEFAULT '',          -- host:port или opc.tcp://...
    settings      jsonb NOT NULL DEFAULT '{}'::jsonb, -- unitId, byteOrder, security и т.п.
    enabled       boolean NOT NULL DEFAULT true,
    poll_ms       int NOT NULL DEFAULT 1000 CHECK (poll_ms >= 50),
    timeout_ms    int NOT NULL DEFAULT 2000 CHECK (timeout_ms >= 100),
    description   text NOT NULL DEFAULT '',
    updated_at    timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS tags (
    id                 serial PRIMARY KEY,
    name               text NOT NULL UNIQUE,
    description        text NOT NULL DEFAULT '',
    device_id          int NOT NULL REFERENCES devices(id) ON DELETE RESTRICT,
    address            text NOT NULL,
    data_type          text NOT NULL CHECK (data_type IN
                         ('bool','int16','uint16','int32','uint32','float32','float64','string')),
    unit               text NOT NULL DEFAULT '',
    -- линейное масштабирование raw -> eng (если любое поле NULL — без масштабирования)
    raw_low            double precision,
    raw_high           double precision,
    eng_low            double precision,
    eng_high           double precision,
    poll_ms            int CHECK (poll_ms IS NULL OR poll_ms >= 50),   -- NULL = как у устройства
    stale_after_ms     int CHECK (stale_after_ms IS NULL OR stale_after_ms >= 100),
    -- три независимых зоны нечувствительности
    deadband_ui        double precision NOT NULL DEFAULT 0 CHECK (deadband_ui >= 0),
    deadband_archive   double precision NOT NULL DEFAULT 0 CHECK (deadband_archive >= 0),
    -- архивирование
    archive_mode       text NOT NULL DEFAULT 'change' CHECK (archive_mode IN ('none','change','interval')),
    archive_interval_s int NOT NULL DEFAULT 60 CHECK (archive_interval_s >= 1),
    -- запись из SCADA
    writable           boolean NOT NULL DEFAULT false,
    write_min          double precision,
    write_max          double precision,
    write_confirm      boolean NOT NULL DEFAULT true,   -- требовать подтверждения оператора
    enabled            boolean NOT NULL DEFAULT true,
    updated_at         timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_tags_device ON tags(device_id);

-- ---------------------------------------------------------------------
-- Оперативные значения (последнее значение каждого тега)
-- quality: коды по мотивам OPC UA (см. Scada.Core/Model/Quality.cs)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS tag_current (
    tag_id      int PRIMARY KEY REFERENCES tags(id) ON DELETE CASCADE,
    num         double precision,
    str         text,
    quality     smallint NOT NULL,
    ts_source   timestamptz NOT NULL,
    ts_server   timestamptz NOT NULL
);

-- ---------------------------------------------------------------------
-- Архив значений: секционирование по месяцам (чистый PostgreSQL).
-- TimescaleDB — опционально, см. db/optional_timescaledb.sql
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS tag_history (
    tag_id    int NOT NULL,
    ts        timestamptz NOT NULL,
    num       double precision,
    str       text,
    quality   smallint NOT NULL
) PARTITION BY RANGE (ts);

CREATE TABLE IF NOT EXISTS tag_history_default PARTITION OF tag_history DEFAULT;

CREATE OR REPLACE FUNCTION ensure_history_partitions(months_ahead int DEFAULT 2)
RETURNS int LANGUAGE plpgsql AS $$
DECLARE
    m      date := date_trunc('month', now() - interval '1 month')::date;
    stop   date := (date_trunc('month', now()) + make_interval(months => months_ahead))::date;
    pname  text;
    made   int := 0;
BEGIN
    WHILE m <= stop LOOP
        pname := format('tag_history_%s', to_char(m, 'YYYYMM'));
        IF to_regclass('scada.' || pname) IS NULL THEN
            EXECUTE format(
              'CREATE TABLE scada.%I PARTITION OF scada.tag_history FOR VALUES FROM (%L) TO (%L)',
              pname, m, (m + interval '1 month')::date);
            EXECUTE format('CREATE INDEX ON scada.%I (tag_id, ts)', pname);
            made := made + 1;
        END IF;
        m := (m + interval '1 month')::date;
    END LOOP;
    RETURN made;
END $$;

-- Удаляет секции, целиком лежащие раньше границы хранения
CREATE OR REPLACE FUNCTION drop_history_older_than(keep interval)
RETURNS int LANGUAGE plpgsql AS $$
DECLARE
    r        record;
    border   date := date_trunc('month', now() - keep)::date;
    dropped  int := 0;
BEGIN
    FOR r IN
        SELECT c.relname
        FROM pg_inherits i
        JOIN pg_class c ON c.oid = i.inhrelid
        JOIN pg_class p ON p.oid = i.inhparent
        JOIN pg_namespace n ON n.oid = p.relnamespace
        WHERE n.nspname = 'scada' AND p.relname = 'tag_history'
          AND c.relname ~ '^tag_history_[0-9]{6}$'
    LOOP
        IF to_date(right(r.relname, 6), 'YYYYMM') + interval '1 month' <= border THEN
            EXECUTE format('DROP TABLE scada.%I', r.relname);
            dropped := dropped + 1;
        END IF;
    END LOOP;
    RETURN dropped;
END $$;

DO $$ BEGIN PERFORM scada.ensure_history_partitions(2); END $$;

-- ---------------------------------------------------------------------
-- Тревоги (ISA-18.2: активна/квитирована — независимые признаки)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS alarm_defs (
    id           serial PRIMARY KEY,
    tag_id       int NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    kind         text NOT NULL CHECK (kind IN
                   ('hihi','hi','lo','lolo','discrete_on','discrete_off','bad_quality')),
    limit_value  double precision,           -- для hihi/hi/lo/lolo
    hysteresis   double precision NOT NULL DEFAULT 0 CHECK (hysteresis >= 0),
    delay_ms     int NOT NULL DEFAULT 0 CHECK (delay_ms >= 0),
    priority     smallint NOT NULL DEFAULT 2 CHECK (priority BETWEEN 1 AND 4), -- 1 — высший
    message      text NOT NULL,
    enabled      boolean NOT NULL DEFAULT true,
    updated_at   timestamptz NOT NULL DEFAULT now(),
    CHECK (kind NOT IN ('hihi','hi','lo','lolo') OR limit_value IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS ix_alarm_defs_tag ON alarm_defs(tag_id);

-- Персистентное состояние — нужно для перезапуска службы и будущего резервирования
CREATE TABLE IF NOT EXISTS alarm_active (
    alarm_def_id   int PRIMARY KEY REFERENCES alarm_defs(id) ON DELETE CASCADE,
    active         boolean NOT NULL,
    acked          boolean NOT NULL,
    value          double precision,
    raised_at      timestamptz,
    cleared_at     timestamptz,
    acked_at       timestamptz,
    acked_by       text,
    shelved_until  timestamptz,
    shelved_by     text,
    comment        text,
    updated_at     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS alarm_events (
    id            bigserial PRIMARY KEY,
    ts            timestamptz NOT NULL DEFAULT now(),
    alarm_def_id  int NOT NULL,
    event         text NOT NULL CHECK (event IN ('raise','clear','ack','shelve','unshelve','comment')),
    value         double precision,
    user_login    text,
    comment       text
);
CREATE INDEX IF NOT EXISTS ix_alarm_events_ts ON alarm_events(ts DESC);

-- ---------------------------------------------------------------------
-- Команды управления. Срок жизни обязателен, повторов нет.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS commands (
    id            bigserial PRIMARY KEY,
    tag_id        int NOT NULL REFERENCES tags(id),
    value_num     double precision,
    value_str     text,
    requested_by  text NOT NULL,
    requested_at  timestamptz NOT NULL DEFAULT now(),
    expires_at    timestamptz NOT NULL,
    status        text NOT NULL DEFAULT 'pending'
                  CHECK (status IN ('pending','executing','done','failed','expired','rejected')),
    result        text,
    readback_num  double precision,
    readback_str  text,
    finished_at   timestamptz,
    client_ip     text,
    CHECK (expires_at > requested_at)
);
CREATE INDEX IF NOT EXISTS ix_commands_pending ON commands(id) WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS ix_commands_time ON commands(requested_at DESC);

-- ---------------------------------------------------------------------
-- Мнемосхемы: JSON-документ + история версий
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS mnemos (
    id               serial PRIMARY KEY,
    name             text NOT NULL,
    parent_id        int REFERENCES mnemos(id) ON DELETE SET NULL,  -- дерево объектов
    sort_order       int NOT NULL DEFAULT 0,
    current_version  int NOT NULL DEFAULT 0,
    updated_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       text
);

CREATE TABLE IF NOT EXISTS mnemo_versions (
    mnemo_id    int NOT NULL REFERENCES mnemos(id) ON DELETE CASCADE,
    version     int NOT NULL,
    doc         jsonb NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    created_by  text,
    PRIMARY KEY (mnemo_id, version)
);

-- ---------------------------------------------------------------------
-- Диагностика каналов связи (пишет служба сбора)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS device_status (
    device_id     int PRIMARY KEY REFERENCES devices(id) ON DELETE CASCADE,
    state         text NOT NULL,                 -- connecting/online/error/disabled
    last_ok_at    timestamptz,
    last_error    text,
    error_count   bigint NOT NULL DEFAULT 0,
    read_count    bigint NOT NULL DEFAULT 0,
    avg_latency_ms double precision,
    updated_at    timestamptz NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------
-- Аудит: только добавление + цепочка хешей (защита от правки в обход UI)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS audit_log (
    id          bigserial PRIMARY KEY,
    ts          timestamptz NOT NULL DEFAULT now(),
    user_login  text,
    action      text NOT NULL,          -- login, login_failed, config_change, command, alarm_ack...
    object      text NOT NULL DEFAULT '',
    details     jsonb NOT NULL DEFAULT '{}'::jsonb,
    client_ip   text,
    prev_hash   bytea,
    hash        bytea
);
CREATE INDEX IF NOT EXISTS ix_audit_ts ON audit_log(ts DESC);

CREATE OR REPLACE FUNCTION audit_chain() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    last bytea;
BEGIN
    -- сериализуем вставки, чтобы цепочка была строго линейной
    PERFORM pg_advisory_xact_lock(hashtext('scada.audit_log'));
    SELECT hash INTO last FROM scada.audit_log ORDER BY id DESC LIMIT 1;
    NEW.prev_hash := last;
    NEW.hash := sha256(
        coalesce(last, '\x'::bytea) ||
        convert_to(concat_ws('|', NEW.id, NEW.ts, NEW.user_login, NEW.action,
                             NEW.object, NEW.details::text, NEW.client_ip), 'UTF8'));
    RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION audit_forbid_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'audit_log is append-only';
END $$;

DROP TRIGGER IF EXISTS trg_audit_chain ON audit_log;
CREATE TRIGGER trg_audit_chain BEFORE INSERT ON audit_log
    FOR EACH ROW EXECUTE FUNCTION audit_chain();
DROP TRIGGER IF EXISTS trg_audit_no_update ON audit_log;
CREATE TRIGGER trg_audit_no_update BEFORE UPDATE OR DELETE ON audit_log
    FOR EACH ROW EXECUTE FUNCTION audit_forbid_change();
DROP TRIGGER IF EXISTS trg_audit_no_truncate ON audit_log;
CREATE TRIGGER trg_audit_no_truncate BEFORE TRUNCATE ON audit_log
    FOR EACH STATEMENT EXECUTE FUNCTION audit_forbid_change();

-- Проверка целостности цепочки: возвращает id первой испорченной записи или NULL
CREATE OR REPLACE FUNCTION audit_verify() RETURNS bigint LANGUAGE plpgsql AS $$
DECLARE
    r     record;
    last  bytea := NULL;
    calc  bytea;
BEGIN
    FOR r IN SELECT * FROM scada.audit_log ORDER BY id LOOP
        calc := sha256(
            coalesce(last, '\x'::bytea) ||
            convert_to(concat_ws('|', r.id, r.ts, r.user_login, r.action,
                                 r.object, r.details::text, r.client_ip), 'UTF8'));
        IF r.hash IS DISTINCT FROM calc OR r.prev_hash IS DISTINCT FROM last THEN
            RETURN r.id;
        END IF;
        last := r.hash;
    END LOOP;
    RETURN NULL;
END $$;

-- =====================================================================
--  LISTEN/NOTIFY — межпроцессный обмен
--
--  Канал               Кто шлёт          Кто слушает   Полезная нагрузка
--  scada_values        acquisition       web           {"v":[[tagId,num,str,q,tsMs],...]} (≤ 7.5 КБ, пачками)
--  scada_alarm         триггер БД        web           {"id":..,"active":..,"acked":..}
--  scada_alarm_ack     web               acquisition   {"ids":[..],"user":"..","comment":".."}
--  scada_command       триггер БД        acquisition   id команды
--  scada_command_done  триггер БД        web           {"id":..,"status":".."}
--  scada_config        триггер БД        оба           имя таблицы
--  scada_device        триггер БД        web           device_id
--  scada_heartbeat     acquisition       web           {"ts":..,"tags":..,"queue":..}
--
--  NOTIFY не гарантирует доставку при разрыве соединения, поэтому:
--   * web после переподключения перечитывает tag_current и alarm_active;
--   * acquisition после переподключения забирает pending-команды из таблицы,
--     но исполняет только не истёкшие (expires_at > now()).
-- =====================================================================

CREATE OR REPLACE FUNCTION notify_config() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_notify('scada_config', TG_TABLE_NAME);
    RETURN NULL;
END $$;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['devices','tags','alarm_defs'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_%1$s_notify ON scada.%1$s', t);
        EXECUTE format('CREATE TRIGGER trg_%1$s_notify AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE
                        ON scada.%1$s FOR EACH STATEMENT EXECUTE FUNCTION scada.notify_config()', t);
    END LOOP;
END $$;

CREATE OR REPLACE FUNCTION notify_command() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' AND NEW.status = 'pending' THEN
        PERFORM pg_notify('scada_command', NEW.id::text);
    ELSIF TG_OP = 'UPDATE' AND NEW.status IS DISTINCT FROM OLD.status
          AND NEW.status IN ('done','failed','expired','rejected') THEN
        PERFORM pg_notify('scada_command_done',
            json_build_object('id', NEW.id, 'status', NEW.status, 'result', NEW.result)::text);
    END IF;
    RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS trg_commands_notify ON commands;
CREATE TRIGGER trg_commands_notify AFTER INSERT OR UPDATE ON commands
    FOR EACH ROW EXECUTE FUNCTION notify_command();

CREATE OR REPLACE FUNCTION notify_alarm() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_notify('scada_alarm', json_build_object(
        'id', NEW.alarm_def_id, 'active', NEW.active, 'acked', NEW.acked)::text);
    RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS trg_alarm_notify ON alarm_active;
CREATE TRIGGER trg_alarm_notify AFTER INSERT OR UPDATE ON alarm_active
    FOR EACH ROW EXECUTE FUNCTION notify_alarm();

CREATE OR REPLACE FUNCTION notify_device() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' OR NEW.state IS DISTINCT FROM OLD.state THEN
        PERFORM pg_notify('scada_device', NEW.device_id::text);
    END IF;
    RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS trg_device_status_notify ON device_status;
CREATE TRIGGER trg_device_status_notify AFTER INSERT OR UPDATE ON device_status
    FOR EACH ROW EXECUTE FUNCTION notify_device();

INSERT INTO schema_version(version) VALUES (1) ON CONFLICT DO NOTHING;
