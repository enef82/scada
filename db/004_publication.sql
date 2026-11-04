-- =====================================================================
--  Публикация конфигурации: черновик → проверка → применение → откат.
--  Применять после 001_schema.sql (идемпотентно).
--
--  Рабочие таблицы devices / tags / alarm_defs / mnemos становятся ЧЕРНОВИКОМ.
--  Рантайм (служба сбора, экраны операторов) работает по последнему снимку
--  в config_versions. Удаление только мягкое (deleted = true): строки остаются,
--  чтобы на них продолжали ссылаться архив, журнал команд и старые версии для отката.
--  Имя удалённого устройства/тега освобождается: name → '~<id>~<имя>'.
-- =====================================================================
SET client_min_messages = warning;
SET search_path = scada, public;

-- ------------------------------------------------------------- версии
CREATE TABLE IF NOT EXISTS config_versions (
    version       int PRIMARY KEY,
    created_at    timestamptz NOT NULL DEFAULT now(),
    created_by    text NOT NULL,
    comment       text NOT NULL,
    snapshot      jsonb NOT NULL,                     -- устройства, теги, тревоги, ссылки на версии схем
    changes       jsonb NOT NULL DEFAULT '[]'::jsonb, -- список изменений относительно предыдущей версии
    warnings      jsonb NOT NULL DEFAULT '[]'::jsonb, -- предупреждения, принятые при публикации
    confirmed_by  text,                               -- второй инженер (принцип четырёх глаз)
    rollback_of   int,                                -- откат к этой версии
    applied_at    timestamptz,                        -- служба сбора подтвердила применение
    applied_ms    int
);

-- журнал правок черновика: кто и что менял (очищается при публикации)
CREATE TABLE IF NOT EXISTS draft_changes (
    id           bigserial PRIMARY KEY,
    ts           timestamptz NOT NULL DEFAULT now(),
    user_login   text NOT NULL,
    object_type  text NOT NULL CHECK (object_type IN ('device','tag','alarm','mnemo')),
    object_id    int NOT NULL,
    object_name  text NOT NULL DEFAULT '',
    op           text NOT NULL CHECK (op IN ('create','update','delete'))
);
CREATE INDEX IF NOT EXISTS ix_draft_changes_obj ON draft_changes(object_type, object_id);

-- ------------------------------------------------------------- мягкое удаление и версия строки
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['devices','tags','alarm_defs','mnemos'] LOOP
        EXECUTE format('ALTER TABLE scada.%I ADD COLUMN IF NOT EXISTS deleted boolean NOT NULL DEFAULT false', t);
        EXECUTE format('ALTER TABLE scada.%I ADD COLUMN IF NOT EXISTS row_version int NOT NULL DEFAULT 1', t);
    END LOOP;
END $$;

-- ------------------------------------------------------------- уведомления
-- правки черновика больше не будят службу сбора: отдельный канал scada_draft (для интерфейса)
CREATE OR REPLACE FUNCTION notify_config() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_notify('scada_draft', TG_TABLE_NAME);
    RETURN NULL;
END $$;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['devices','tags','alarm_defs','mnemos'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_%1$s_notify ON scada.%1$s', t);
        EXECUTE format('CREATE TRIGGER trg_%1$s_notify AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE
                        ON scada.%1$s FOR EACH STATEMENT EXECUTE FUNCTION scada.notify_config()', t);
    END LOOP;
END $$;

-- публикация: scada_config с номером версии (служба сбора перезагружает конфигурацию)
CREATE OR REPLACE FUNCTION notify_published() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        PERFORM pg_notify('scada_config', NEW.version::text);
    ELSIF NEW.applied_at IS NOT NULL AND OLD.applied_at IS NULL THEN
        PERFORM pg_notify('scada_config_applied',
            json_build_object('version', NEW.version, 'ms', NEW.applied_ms)::text);
    END IF;
    RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS trg_config_versions_notify ON config_versions;
CREATE TRIGGER trg_config_versions_notify AFTER INSERT OR UPDATE ON config_versions
    FOR EACH ROW EXECUTE FUNCTION notify_published();

-- версии конфигурации неизменяемы (кроме отметки о применении)
CREATE OR REPLACE FUNCTION config_versions_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'config_versions: удаление запрещено'; END IF;
    IF NEW.snapshot IS DISTINCT FROM OLD.snapshot OR NEW.changes IS DISTINCT FROM OLD.changes
       OR NEW.created_by IS DISTINCT FROM OLD.created_by OR NEW.comment IS DISTINCT FROM OLD.comment THEN
        RAISE EXCEPTION 'config_versions: опубликованная версия не изменяется';
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_config_versions_guard ON config_versions;
CREATE TRIGGER trg_config_versions_guard BEFORE UPDATE OR DELETE ON config_versions
    FOR EACH ROW EXECUTE FUNCTION config_versions_guard();

INSERT INTO schema_version(version) VALUES (4) ON CONFLICT DO NOTHING;
-- первая версия (снимок текущих таблиц) создаётся службой при первом запуске
