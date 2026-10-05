-- Indeksy pod "ile numerów zostało" i pod wydawanie numerów. PostgreSQL 13+. Skrypt idempotentny.
-- Wycofanie: DROP INDEX mass.ix_registered_number_pool_available, mass.ix_registered_number_pool_reserved_change_date;
-- (002_create_registered_number_pool.rollback.sql usuwa je razem z tabelą).
--
-- Przy dużej tabeli pod ruchem uruchom oba CREATE INDEX z CONCURRENTLY, poza transakcją.

BEGIN;

-- Tylko numery dostępne (state = 0). Indeks maleje wraz ze zużyciem puli, więc zliczanie
-- pozostałych numerów czyta wyłącznie to, co faktycznie zostało (Index Only Scan):
--
--   SELECT count(*) FILTER (WHERE type = 1) AS krajowe,
--          count(*) FILTER (WHERE type = 2) AS zagraniczne
--   FROM   mass.registered_number_pool
--   WHERE  state = 0;
--
-- Kolumny (type, value, full_number) to dokładnie kolejność wydawania, więc ten sam indeks
-- obsługuje "pobierz kolejny wolny numer" bez sortowania:
--
--   WHERE type = ? AND state = 0 ORDER BY value, full_number LIMIT 1 FOR UPDATE SKIP LOCKED
--
-- Warunek "state = 0" musi być w zapytaniu literałem, nie parametrem - inaczej planista
-- nie dopasuje indeksu częściowego.
CREATE INDEX IF NOT EXISTS ix_registered_number_pool_available
    ON mass.registered_number_pool (type, value, full_number)
    WHERE state = 0;

-- Tylko rezerwacje (state = 1), po dacie. Służy do wyszukiwania rezerwacji porzuconych
-- (proces padł między pobraniem numeru a nadrukiem): WHERE state = 1 AND change_date < ?
CREATE INDEX IF NOT EXISTS ix_registered_number_pool_reserved_change_date
    ON mass.registered_number_pool (change_date)
    WHERE state = 1;

COMMIT;
