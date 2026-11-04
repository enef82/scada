-- Демо: дерево областей и принадлежность объектов (после 002_demo.sql и 005_access.sql).
SET client_min_messages = warning;
SET search_path = scada, public;
INSERT INTO areas (id, parent_id, name, sort_order) VALUES
    (2, 1, 'Водоотведение', 1),
    (3, 2, 'КНС-1', 1),
    (4, 2, 'КНС-2', 2)
ON CONFLICT (id) DO NOTHING;
DO $$ BEGIN PERFORM setval(pg_get_serial_sequence('scada.areas', 'id'), greatest((SELECT max(id) FROM scada.areas), 4)); END $$;
UPDATE devices SET area_id = 3 WHERE id = 1 AND area_id = 1;
UPDATE devices SET area_id = 4 WHERE id = 2 AND area_id = 1;
UPDATE mnemos  SET area_id = 3 WHERE id = 1 AND area_id = 1;
UPDATE mnemos  SET area_id = 4 WHERE id = 2 AND area_id = 1;
