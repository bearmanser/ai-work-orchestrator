-- P1 integration-test fixture: a dedicated schema with an orders-like table
-- plus a genuinely restricted role. The adapter connects as p1_reader, which
-- holds CONNECT (via the default PUBLIC grant) plus USAGE + SELECT only.
CREATE SCHEMA IF NOT EXISTS p1;

CREATE TABLE IF NOT EXISTS p1.orders (
    id SERIAL PRIMARY KEY,
    company TEXT NOT NULL,
    reference TEXT NOT NULL,
    status TEXT NOT NULL,
    picked_at DATE NOT NULL
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'p1_reader') THEN
        CREATE ROLE p1_reader WITH LOGIN PASSWORD 'p1ReaderPw0372';
    ELSE
        ALTER ROLE p1_reader WITH LOGIN PASSWORD 'p1ReaderPw0372';
    END IF;
END
$$;

GRANT USAGE ON SCHEMA p1 TO p1_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA p1 TO p1_reader;
ALTER DEFAULT PRIVILEGES IN SCHEMA p1 GRANT SELECT ON TABLES TO p1_reader;

DELETE FROM p1.orders;

INSERT INTO p1.orders (company, reference, status, picked_at) VALUES
    ('KAMELEON-TEST', 'ORD-001', 'PICKED', DATE '2026-09-10'),
    ('KAMELEON-TEST', 'ORD-002', 'PICKED', DATE '2026-09-12'),
    ('KAMELEON-TEST', 'ORD-OLD', 'PICKED', DATE '2026-08-01'),
    ('OTHER-COMPANY', 'ORD-999', 'PICKED', DATE '2026-09-11');
