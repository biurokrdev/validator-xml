-- Pula numerów nadawczych przesyłek poleconych (R). Jeden wiersz = jeden numer.
-- PostgreSQL 13+. Skrypt idempotentny.
--
-- Pełny numer (full_number) liczy aplikacja (RegisteredNumberFormatter):
--   krajowy    20 cyfr:   00 + IAC + 5900773 + S1 + 8 cyfr + cyfra kontrolna GS1
--   zagraniczny 13 znaków: RR + 8 cyfr + cyfra kontrolna S10 + PL

BEGIN;

CREATE SCHEMA IF NOT EXISTS mass;

CREATE TABLE IF NOT EXISTS mass.registered_number_pool
(
    id                uuid         NOT NULL DEFAULT gen_random_uuid(),
    type              integer      NOT NULL,              -- 1 krajowy, 2 zagraniczny
    value             integer      NOT NULL,              -- 8-cyfrowy numer przesyłki
    state             integer      NOT NULL DEFAULT 0,    -- 0 dostępny, 1 zarezerwowany, 2 użyty, 3 anulowany
    editor            varchar(256) NOT NULL,
    change_date       timestamptz  NOT NULL DEFAULT now(),
    iac_digit         smallint,                           -- krajowy: cyfra IAC 1-9
    kind_digit        smallint,                           -- krajowy: S1 (1 lub 4 = polecona)
    service_indicator char(2),                            -- zagraniczny: RA..RZ
    country_code      char(2),                            -- zagraniczny: PL
    full_number       varchar(20)  NOT NULL,              -- pełny numer z cyfrą kontrolną

    CONSTRAINT pk_registered_number_pool PRIMARY KEY (id),                         -- klucz główny
    CONSTRAINT uq_registered_number_pool_full_number UNIQUE (full_number),         -- ten sam numer R tylko raz
    CONSTRAINT ck_registered_number_pool_type  CHECK (type IN (1, 2)),             -- tylko znane typy
    CONSTRAINT ck_registered_number_pool_state CHECK (state BETWEEN 0 AND 3),      -- tylko znane stany
    CONSTRAINT ck_registered_number_pool_value CHECK (value BETWEEN 0 AND 99999999) -- numer max 8 cyfr
);

-- pobranie kolejnego wolnego numeru: WHERE type = ? AND state = 0 ORDER BY value
CREATE INDEX IF NOT EXISTS ix_registered_number_pool_type_state_value
    ON mass.registered_number_pool (type, state, value);

COMMIT;
