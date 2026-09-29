-- Listy panelu administratora zwracają KOMPLET wierszy (bez LIMIT) posortowany po created_at DESC.
-- Indeksy pod dokładnie te zapytania:
--   documents:            WHERE is_deleted = FALSE ORDER BY created_at DESC   (lista plików)
--   document_deliveries:  ORDER BY created_at DESC                            (zlecenia — „wszystkie")
--   document_deliveries:  WHERE status = $1 ORDER BY created_at DESC          (zlecenia — filtr statusu)
-- Aktywną wersję dla wiersza listy plików wyznacza podzapytanie po document_versions(document_id) WHERE is_active,
-- pokryte istniejącym unikalnym indeksem częściowym idx_document_versions_unique_active (002).

CREATE INDEX IF NOT EXISTS idx_documents_active_created_at
    ON documents (created_at DESC)
    WHERE is_deleted = FALSE;

CREATE INDEX IF NOT EXISTS ix_document_deliveries_created_at
    ON document_deliveries (created_at DESC);

CREATE INDEX IF NOT EXISTS ix_document_deliveries_status_created_at
    ON document_deliveries (status, created_at DESC);

COMMENT ON INDEX idx_documents_active_created_at IS 'Lista plików admina: nieusunięte, najnowsze pierwsze (bez LIMIT)';
COMMENT ON INDEX ix_document_deliveries_created_at IS 'Lista zleceń wysyłki admina (wszystkie statusy), najnowsze pierwsze';
COMMENT ON INDEX ix_document_deliveries_status_created_at IS 'Lista zleceń wysyłki admina filtrowana po statusie, najnowsze pierwsze';
