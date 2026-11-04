-- =====================================================================
--  Разграничение доступа: области, роли как наборы прав, назначения,
--  классы критичности команд, второе подтверждение, доменные учётные записи.
--  Применять после 004_publication.sql (идемпотентно).
--
--  Доступ = роль (набор кодов прав) + область (узел дерева объектов).
--  Назначение действует на свой узел и все вложенные, права складываются,
--  запрещающих правил нет. Права users.manage и audit.view действуют
--  только при назначении на корневую область.
-- =====================================================================
SET client_min_messages = warning;
SET search_path = scada, public;

-- ------------------------------------------------------------- области
-- Дерево объектов: установка (корень, id = 1) → проект → площадка → объект.
-- Области меняются сразу (как пользователи и назначения), без черновика:
-- на них ссылаются назначения, которые должны действовать немедленно.
-- Принадлежность устройств, тегов и схем к области — часть конфигурации и идёт через публикацию.
CREATE TABLE IF NOT EXISTS areas (
    id          serial PRIMARY KEY,
    parent_id   int REFERENCES areas(id) ON DELETE RESTRICT,
    name        text NOT NULL,
    sort_order  int NOT NULL DEFAULT 0,
    created_at  timestamptz NOT NULL DEFAULT now(),
    CHECK (parent_id IS NULL OR parent_id <> id),
    UNIQUE (parent_id, name)
);
INSERT INTO areas (id, parent_id, name) VALUES (1, NULL, 'Установка') ON CONFLICT (id) DO NOTHING;
DO $$ BEGIN PERFORM setval(pg_get_serial_sequence('scada.areas', 'id'), greatest((SELECT max(id) FROM scada.areas), 1)); END $$;
-- корень один
CREATE UNIQUE INDEX IF NOT EXISTS ux_areas_single_root ON areas ((true)) WHERE parent_id IS NULL;

ALTER TABLE devices ADD COLUMN IF NOT EXISTS area_id int NOT NULL DEFAULT 1 REFERENCES areas(id);
ALTER TABLE tags    ADD COLUMN IF NOT EXISTS area_id int REFERENCES areas(id);   -- NULL — область устройства
ALTER TABLE mnemos  ADD COLUMN IF NOT EXISTS area_id int NOT NULL DEFAULT 1 REFERENCES areas(id);

-- ------------------------------------------------------------- классы критичности команд
-- normal    — диалог подтверждения;
-- important — + повторный ввод пароля и комментарий;
-- critical  — пароль, комментарий и второй пользователь с правом cmd.critical за 60 с.
-- Записываемые теги получают класс «Обычный»; тег только для чтения класс не использует.
ALTER TABLE tags ADD COLUMN IF NOT EXISTS command_class text NOT NULL DEFAULT 'normal';
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'tags_command_class_check') THEN
        ALTER TABLE tags ADD CONSTRAINT tags_command_class_check
            CHECK (command_class IN ('normal','important','critical'));
    END IF;
END $$;

-- ------------------------------------------------------------- роли и назначения
CREATE TABLE IF NOT EXISTS roles (
    id           serial PRIMARY KEY,
    code         text UNIQUE,                       -- у системных ролей: viewer, operator, ...
    name         text NOT NULL UNIQUE,
    description  text NOT NULL DEFAULT '',
    is_system    boolean NOT NULL DEFAULT false,    -- системные роли не меняются и не удаляются
    permissions  text[] NOT NULL DEFAULT '{}',
    CHECK (permissions <@ ARRAY['view','alarm.ack','alarm.shelve','cmd.control','cmd.setpoint','cmd.critical',
                                'config.edit','mnemo.edit','config.publish','users.manage','audit.view']::text[])
);

INSERT INTO roles (code, name, is_system, description, permissions) VALUES
    ('viewer',          'Наблюдатель',                 true, 'Просмотр схем, значений, трендов и тревог',
        ARRAY['view']),
    ('operator',        'Оператор',                    true, 'Управление оборудованием и квитирование тревог',
        ARRAY['view','alarm.ack','cmd.control','cmd.setpoint']),
    ('senior_operator', 'Старший оператор',            true, 'Оператор + снятие тревог с контроля и критичные команды',
        ARRAY['view','alarm.ack','alarm.shelve','cmd.control','cmd.setpoint','cmd.critical']),
    ('commissioner',    'Наладчик',                    true, 'Готовит изменения конфигурации и схем, не публикует',
        ARRAY['view','config.edit','mnemo.edit']),
    ('engineer',        'Инженер',                     true, 'Конфигурация, схемы, публикация, аудит',
        ARRAY['view','config.edit','mnemo.edit','config.publish','audit.view']),
    ('security_admin',  'Администратор безопасности',  true, 'Пользователи, роли, назначения, аудит. Не управляет оборудованием',
        ARRAY['view','users.manage','audit.view'])
ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name, is_system = true, description = EXCLUDED.description,
                                 permissions = EXCLUDED.permissions;

CREATE TABLE IF NOT EXISTS role_assignments (
    id              serial PRIMARY KEY,
    principal_type  text NOT NULL CHECK (principal_type IN ('user','group')),
    principal       text NOT NULL,                  -- логин пользователя или имя группы AD (cn)
    role_id         int NOT NULL REFERENCES roles(id) ON DELETE RESTRICT,
    area_id         int NOT NULL REFERENCES areas(id) ON DELETE RESTRICT,
    valid_until     timestamptz,                    -- для подрядчиков и наладчиков
    comment         text NOT NULL DEFAULT '',
    created_at      timestamptz NOT NULL DEFAULT now(),
    created_by      text,
    UNIQUE (principal_type, principal, role_id, area_id)
);
CREATE INDEX IF NOT EXISTS ix_role_assignments_principal ON role_assignments(principal_type, lower(principal));

-- ------------------------------------------------------------- пользователи
ALTER TABLE users ADD COLUMN IF NOT EXISTS source text NOT NULL DEFAULT 'local';
ALTER TABLE users ADD COLUMN IF NOT EXISTS emergency boolean NOT NULL DEFAULT false; -- аварийная локальная запись
ALTER TABLE users ADD COLUMN IF NOT EXISTS ad_checked_at timestamptz;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'users_source_check') THEN
        ALTER TABLE users ADD CONSTRAINT users_source_check CHECK (source IN ('local','ad'));
    END IF;
END $$;

-- переход: прежние роли → назначения на корень (выполняется один раз — пока колонка role заполнена)
INSERT INTO role_assignments (principal_type, principal, role_id, area_id, created_by, comment)
SELECT 'user', u.login, r.id, 1, 'migration', 'перенесено из прежней роли ' || u.role
FROM users u
JOIN LATERAL unnest(CASE u.role
        WHEN 'viewer'   THEN ARRAY['viewer']
        WHEN 'operator' THEN ARRAY['operator']
        WHEN 'engineer' THEN ARRAY['engineer']
        WHEN 'admin'    THEN ARRAY['security_admin','engineer']
     END) AS m(code) ON true
JOIN roles r ON r.code = m.code
WHERE u.role IS NOT NULL
ON CONFLICT DO NOTHING;
ALTER TABLE users ALTER COLUMN role DROP NOT NULL;
UPDATE users SET role = NULL WHERE role IS NOT NULL;   -- колонка больше не используется

-- ------------------------------------------------------------- команды
ALTER TABLE commands ADD COLUMN IF NOT EXISTS command_class text NOT NULL DEFAULT 'normal';
ALTER TABLE commands ADD COLUMN IF NOT EXISTS comment text;
ALTER TABLE commands ADD COLUMN IF NOT EXISTS requested_session text;   -- сеанс автора (второй — с другого)
ALTER TABLE commands ADD COLUMN IF NOT EXISTS confirm_mode text;        -- second_user | permit
ALTER TABLE commands ADD COLUMN IF NOT EXISTS permit_code text;         -- номер наряда (объект с одним дежурным)
ALTER TABLE commands ADD COLUMN IF NOT EXISTS confirmed_by text;
ALTER TABLE commands DROP CONSTRAINT IF EXISTS commands_status_check;
ALTER TABLE commands ADD CONSTRAINT commands_status_check
    CHECK (status IN ('awaiting_confirm','pending','executing','done','failed','expired','rejected'));
CREATE INDEX IF NOT EXISTS ix_commands_awaiting ON commands(id) WHERE status = 'awaiting_confirm';

-- второе подтверждение критичной команды; служба сбора проверяет его сама, не полагаясь на веб
CREATE TABLE IF NOT EXISTS command_confirmations (
    command_id    bigint PRIMARY KEY REFERENCES commands(id),
    user_login    text NOT NULL,
    session_id    text NOT NULL,
    confirmed_at  timestamptz NOT NULL DEFAULT now(),
    client_ip     text
);
CREATE OR REPLACE FUNCTION command_confirmations_guard() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE c record;
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'Подтверждения команд не изменяются и не удаляются';
    END IF;
    SELECT requested_by, requested_session, status INTO c FROM commands WHERE id = NEW.command_id;
    IF c.status <> 'awaiting_confirm' THEN
        RAISE EXCEPTION 'Команда % не ожидает подтверждения', NEW.command_id;
    END IF;
    IF lower(c.requested_by) = lower(NEW.user_login) OR c.requested_session = NEW.session_id THEN
        RAISE EXCEPTION 'Подтверждать должен другой пользователь с другого сеанса';
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_command_confirmations_guard ON command_confirmations;
CREATE TRIGGER trg_command_confirmations_guard BEFORE INSERT OR UPDATE OR DELETE ON command_confirmations
    FOR EACH ROW EXECUTE FUNCTION command_confirmations_guard();

-- уведомления: подтверждённая команда (awaiting_confirm → pending) будит службу сбора;
-- новая критичная команда — интерфейс (окно подтверждения у других пользователей)
CREATE OR REPLACE FUNCTION notify_command() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status = 'pending' AND (TG_OP = 'INSERT' OR OLD.status = 'awaiting_confirm') THEN
        PERFORM pg_notify('scada_command', NEW.id::text);
    END IF;
    IF TG_OP = 'INSERT' AND NEW.status = 'awaiting_confirm' THEN
        PERFORM pg_notify('scada_command_confirm', NEW.id::text);
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.status IS DISTINCT FROM OLD.status
       AND NEW.status IN ('done','failed','expired','rejected') THEN
        PERFORM pg_notify('scada_command_done',
            json_build_object('id', NEW.id, 'status', NEW.status, 'result', NEW.result)::text);
    END IF;
    RETURN NULL;
END $$;
