# Monitoring D2 ViewerEditor w Elasticsearch — podsumowanie (2026-09-23)

Pakiet leży w repo w katalogu `observability/`. Szersza propozycja i lista braków w kodzie: `OBSERVABILITY_DASHBOARDS.md` (root).

## Co jest w pakiecie

| Plik | Zawartość |
|---|---|
| 01-setup.console | ingest pipeline + index template dla logs-d2-* (scala format WebApi/ECS z Serilogiem Services, mapuje pola na keyword/long) |
| 02-queries.console | 20 zapytań Query DSL + 5 ES\|QL, po jednym na każde pytanie, samowystarczalne (własne runtime fields) |
| 03-kibana-dashboard.ndjson | data view z runtime fields route / doc_id / app, data view heartbeat-*, 17 paneli Lens, saved search, dashboard „D2 ViewerEditor — monitoring" |
| 04-alerts.console | 7 reguł: milczy WebApi, milczy Services, sonda down per aplikacja, wysyp 5xx, dead-letter wysyłki, odrzucone ingesty, błędy frontendu |
| 05-heartbeat.yml | 3 sondy HTTP: GUI /health, WebApi /api/health, Services /health, każda raportuje osobno jako monitor.id |
| build_dashboard.py, README.md | generator obu plików wyjściowych i mapa pytanie → sygnał → zapytanie → panel |

## Co wynika z audytu kodu i co zmienia dokładność metryk

- **Przyjęte i odesłane** są dokładne. Services loguje „Zewnętrzny ingest dokumentu OK" z MasterId i Mime, a worker dostaw loguje „Delivery sent to recipient" ze scope deliveryId.
- **Edytowane teraz** liczone jako unikalne dokumenty z auto-save PUT /api/documentstorage/{id}/versions/{id} w ostatnich 15 min. Kafelek ma własny zakres czasu. Źródłem prawdy pozostaje kolumna status = Editing w tabeli documents (PostgreSQL).
- **Pobranie edytowanego pliku** jest dokładne. GUI woła POST {id}/user-download tylko z przycisku „Pobierz dokument". Odpowiedź 403 = blokada flagą userDownload.
- **Pobranie oryginału jest przybliżone.** GUI używa tego samego GET {id}/download do załadowania podglądu read-only i do menu „Pobierz oryginał". Najtańszy fix to jedna linia w GUI (document-editor.ts, downloadOriginalDocument): parametr `?reason=userDownload`. Formatter WebApi już emituje url.query na wpisie access-logu, więc backend nie wymaga zmian. Zapytanie Q5 i lejek mają na to gotowy kubełek.
- **„Żyje" dla frontendu** da się zmierzyć tylko sondą z zewnątrz (Heartbeat). Nginx ma access_log off na /health i nic nie loguje. Dla obu API jest też wariant z logów: cisza per instancja (service.instance.id) i sondy LB w access logu.
- **Błędy frontendu nie istnieją w Elasticsearch.** GlobalErrorHandler i httpErrorInterceptor robią wyłącznie console.error. Zapytania Q7d i alert A7 są gotowe na endpoint client-log opisany w sekcji B-05 dokumentu OBSERVABILITY_DASHBOARDS.md. Do tego czasu Q7e pokazuje błędy pośrednio przez 4xx/5xx na requestach GUI do WebApi.

## Mapa pytanie → zapytanie → panel

| # | Pytanie | Sygnał | Dokładność | Zapytanie | Panel |
|---|---|---|---|---|---|
| 1 | Ile plików przysłano | Services: log ingest OK (MasterId, Mime, Classification) | dokładna | Q1a, Q1b (odrzucone), Q1c (po HTTP) | kafelek 1, lejek |
| 2 | Ile odesłano | WebApi worker: „Delivery sent to recipient" | dokładna | Q2a, Q2b (retry / porażki / dead-letter) | kafelek 2, panel 2b |
| 3 | Ile w edycji | PUT versions/{id} 2xx w 15 min; „w toku" = otwarte bez finish | dobra | Q3a, Q3b, E2, E2b | kafelek 3 |
| 4 | Pobrania edytowanego | POST {id}/user-download 200 | dokładna | Q4 | kafelek 4, lejek |
| 5 | Pobrania oryginału | GET {id}/download 200 (zawiera podglądy) | przybliżona | Q5 | kafelek 5, lejek |
| 6 | Żyje: GUI / WebApi / Services | Heartbeat monitor.status; cisza w logach; sondy /api/health | Heartbeat dokładne dla trzech; logi tylko API | Q6a–Q6d, E3, E5 | kafelki 6a–6c, 6d, 6e; alerty A1–A3 |
| 7 | Błędy | WebApi: level Error/Critical, exceptionType, event.reason, route; Services: level Error/Fatal; frontend: dziś nic | API dokładne; front pośrednio | Q7a–Q7f, E4 | kafelek 7, panele 7a–7d; alerty A4, A7 |

## Jak zweryfikowano

Generator zbudował 25 zapytań i 20 saved objects. Każda linia NDJSON parsuje się jako JSON, a wszystkie referencje paneli wskazują na istniejące obiekty. Trasy i komunikaty logów pochodzą z atrybutów Route kontrolerów, RequestObservabilityMiddleware, formatterów i DeliveryAttemptRunner. NDJSON i reguły nie były importowane do żywej Kibany, więc po imporcie trzeba sprawdzić renderowanie paneli Lens. Skrypty Painless celowo nie używają regexów (splitOnToken + substring). Skrypt z OBSERVABILITY_DASHBOARDS.md wywołuje String.replaceAll ze stringiem, czego Painless nie dopuszcza.

## Ryzyka i założenia

- Platforma: Elasticsearch/Kibana 9.5.3. NDJSON niesie wersje migracji 8.8/8.9 — Kibana 9.5 migruje je przy imporcie. Heartbeat 9.x używa `expression` w check.response.json (uwzględnione).
- Środowisko: GKE → GCP Cloud Logging → Elastic, data view `doc2-app-logs`, JSON parsowany na pola. Aplikacje po `container.name`: `doc2tools-viewer-api`, `doc2tools-viewer-service`, `doc2tools-viewer-frontend`; pod = `resource.labels.pod_name`. Placeholder `DOC2_APP_LOGS_INDEX` w plikach 02/04/06 do podstawienia wzorcem indeksu tego data view.
- Gotowe filtry do „Add filter → Edit as Query DSL" pod istniejący dashboard „Doc2 Edytor - status": `06-kibana-filters-dsl.md`.
- Services nie maskuje właściwości zdarzeń (B-01) — ograniczyć dostęp do indeksu rolą.
- LoggingBehaviour MediatR loguje całą komendę SaveDocumentCommand z treścią dokumentu. Template wyłącza indeksowanie pola Request, ale treść nadal trafia do _source (B-08).
- Sondy /api/health trafiają do access logu obu API; jeśli zostaną odfiltrowane w pipeline, przestaje działać tylko Q6d.

## Trzy małe zmiany w kodzie domykające luki (nie wykonane)

1. GUI: `?reason=userDownload` przy „Pobierz oryginał" → pytanie 5 dokładne bez zmian w backendzie.
2. Endpoint POST /api/document/client-log + wysyłka z GlobalErrorHandler + interceptor X-Correlation-ID → pytanie 7 dla frontendu i korelacja przeglądarka → API → wysyłka.
3. nginx: access_log w JSON na stdout → błędy i „cisza" GUI widoczne z logów (Heartbeat pozostaje prostszy).
