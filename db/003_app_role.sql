-- =====================================================================
--  Рекомендуемое разделение ролей БД для промышленной установки.
--  владелец схемы (например scada_owner) — используется только для миграций;
--  scada_app   — учётная запись служб: только DML, без DDL и без права
--                изменять/удалять журнал аудита и отключать его триггеры.
--  Выполнять от суперпользователя после 001_schema.sql (схема должна принадлежать scada_owner).
--  Пароль задайте свой: ALTER ROLE scada_app PASSWORD '...';
-- =====================================================================
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'scada_app') THEN
        CREATE ROLE scada_app LOGIN;
    END IF;
END $$;

GRANT USAGE ON SCHEMA scada TO scada_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA scada TO scada_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA scada TO scada_app;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA scada TO scada_app;

-- журнал аудита: только чтение и добавление
REVOKE UPDATE, DELETE, TRUNCATE ON scada.audit_log FROM scada_app;

-- служба сбора создаёт/удаляет секции архива: функции выполняются с правами владельца
ALTER FUNCTION scada.ensure_history_partitions(int) SECURITY DEFINER SET search_path = scada, public;
ALTER FUNCTION scada.drop_history_older_than(interval) SECURITY DEFINER SET search_path = scada, public;

-- права на объекты, которые появятся позже (новые секции архива создаёт владелец схемы)
DO $$
DECLARE owner text := (SELECT nspowner::regrole::text FROM pg_namespace WHERE nspname = 'scada');
BEGIN
    EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %s IN SCHEMA scada GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO scada_app', owner);
    EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %s IN SCHEMA scada GRANT USAGE, SELECT ON SEQUENCES TO scada_app', owner);
END $$;
