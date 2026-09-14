-- Wycofuje 001_create_registered_number_pool.sql. USUWA DANE.

BEGIN;

DROP TABLE  IF EXISTS mass.registered_number_pool;
DROP SCHEMA IF EXISTS mass;

COMMIT;
