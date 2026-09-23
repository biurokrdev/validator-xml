# Monitoring D2 ViewerEditor w Elasticsearch / Kibana — pakiet startowy

Wszystkie zapytania i panele opierają się WYŁĄCZNIE na polach, które aplikacje dziś emitują
(audyt kodu 2026-09-23: RequestObservabilityMiddleware, GcpJsonConsoleFormatter,
GcpJsonSerilogFormatter, DeliveryAttemptRunner, kontrolery, document-storage.service.ts, nginx.conf).
Standard formatu logów: .ai/OBSERVABILITY.md. Szersza propozycja i lista braków: OBSERVABILITY_DASHBOARDS.md (root repo).

## Pliki

| Plik | Co robi | Jak użyć |
|---|---|---|
| 01-setup.console | ingest pipeline d2-logs + index template logs-d2 (mapowanie keyword/long, normalizacja service/@timestamp, Fatal->Critical) | Kibana -> Dev Tools, wykonaj raz PRZED pierwszymi logami |
| 02-queries.console | 20 zapytań Query DSL + 5 ES\|QL — po jednym na każde pytanie z listy, samowystarczalne (własne runtime_mappings) | Dev Tools, uruchamiaj pojedynczo; zakres now-24h do zmiany |
| 03-kibana-dashboard.ndjson | data view logs-d2-* (runtime fields route / doc_id / app), data view heartbeat-*, 17 wizualizacji Lens + saved search, dashboard "D2 ViewerEditor — monitoring" | Stack Management -> Saved Objects -> Import |
| 04-alerts.console | 7 reguł alertów (.es-query): milczy WebApi / Services, sonda down, wysyp 5xx, dead-letter, odrzucone ingesty, błędy frontendu | Dev Tools (prefiks kbn:) albo curl na /api/alerting/rule; dopnij actions |
| 05-heartbeat.yml | Elastic Heartbeat — 3 sondy HTTP: GUI /health, WebApi /api/health, Services /health | wdrożyć Heartbeat w GCP, podmienić hosty |
| build_dashboard.py | generator 02 i 03 (jedno źródło prawdy dla skryptów Painless i selektorów KQL) | python build_dashboard.py po każdej zmianie |

Platforma docelowa: Elasticsearch/Kibana 9.5.3. Indeks logs-d2-* (podmień), logi obu API zbierane ze stdout
kontenerów (Filebeat/Elastic Agent) do Elasticsearch.
Uwagi do 9.x:
- NDJSON ma coreMigrationVersion 8.8.0 / typeMigrationVersion 8.9.0 — Kibana 9.5 migruje takie obiekty przy imporcie
  (starsze niż bieżąca wersja są dozwolone, nowsze nie). Po imporcie otwórz dashboard i sprawdź, czy panele Lens się renderują.
- Heartbeat 9.x: warunki JSON w check.response.json przez "expression", nie "condition" (05-heartbeat.yml już tak ma).
- Dev Tools obsługuje wywołania API Kibany z prefiksem kbn: (04-alerts.console).
- ES|QL: wszystkie użyte funkcje (GROK, REPLACE, COUNT_DISTINCT, DATE_DIFF, DATE_TRUNC, CASE) są w 9.x; w Discover
  możesz je wkleić bezpośrednio w trybie ES|QL i zapisać jako panel dashboardu.
NDJSON i reguły NIE były importowane do żywej Kibany.

## Mapa: pytanie -> sygnał w logach -> zapytanie -> panel

| # | Pytanie | Sygnał (dokładnie to, co aplikacja loguje) | Dokładność | Zapytanie | Panel |
|---|---|---|---|---|---|
| 1 | Ile plików przysłano | Services, log "Zewnętrzny ingest dokumentu OK: MasterId=.., Mime=.., Classification=.." (DocumentController.CreateDocument, 201) | dokładna | Q1a (odrzucone: Q1b, fallback po HTTP: Q1c) | kafelek 1, lejek |
| 2 | Ile plików odesłano do aplikacji zewnętrznej | WebApi worker, log "Delivery sent to recipient" w scope deliveryId / masterId / versionId / attempt (DeliveryAttemptRunner) | dokładna | Q2a (retry / porażki / dead-letter: Q2b) | kafelek 2, panel 2b |
| 3 | Ile plików jest obecnie edytowanych | access log WebApi: PUT /api/documentstorage/{id}/versions/{id} 2xx = auto-save v2 w miejscu; "teraz" = unikalne doc_id z zapisem w ostatnich 15 min; "w toku" = otwarte do edycji (GET .../versions/{id}/download 200) bez POST .../finish 2xx | dobra (okno 15 min jest umowne); źródło prawdy to kolumna status=Editing w tabeli documents w PostgreSQL | Q3a (teraz), Q3b / E2 / E2b (w toku) | kafelek 3 (własny zakres 15 min) |
| 4 | Ile razy pobrano EDYTOWANY plik na komputer | access log WebApi: POST /api/documentstorage/{id}/user-download 200 — GUI woła to tylko z przycisku "Pobierz dokument" | dokładna (403 = zablokowane flagą userDownload) | Q4 | kafelek 4, lejek |
| 5 | Ile razy pobrano ORYGINALNY plik | access log WebApi: GET /api/documentstorage/{id}/download 200 — ale GUI używa TEGO SAMEGO requestu do załadowania podglądu read-only (loadFromStorage bez versionId) i do menu "Pobierz oryginał" | PRZYBLIŻONA (górne oszacowanie) — patrz "Jedna zmiana w GUI" | Q5 | kafelek 5, lejek |
| 6 | Czy GUI / WebApi / Services żyją, każdy osobno | (a) Heartbeat: 3 monitory d2-gui / d2-webapi / d2-services -> monitor.status; (b) z logów: ostatni wpis per service.name / service.instance.id; (c) sondy LB na /api/health w access logu obu API | (a) dokładna dla wszystkich trzech; (b),(c) tylko API — nginx nie loguje (/health ma access_log off) | Q6a, Q6b, E5 (heartbeat); Q6c, E3 (cisza); Q6d (sondy) | kafelki 6a-6c, tabela 6d, wykres 6e; alerty A1-A3 |
| 7 | Błędy frontend / WebApi / Services | WebApi: level Error/Critical + exceptionType, error.type, event.reason (db/dependency/validation/authorization/code), route; Services: level Error/Fatal + exceptionType; oba: statusCode >= 500 z access logu; frontend: DZIŚ NIC (GlobalErrorHandler i httpErrorInterceptor robią tylko console.error) | API: dokładna; frontend: tylko pośrednio (4xx/5xx na requestach GUI) do czasu B-05 | Q7a, Q7b, Q7c, E4; frontend: Q7e (dziś), Q7d (po B-05), Q7f (nginx) | kafelek 7, panele 7a-7d; alerty A4, A7 |

## Środowisko docelowe (ustalone ze screenów Discover, 2026-09-23)

- Logi: GKE → GCP Cloud Logging → Elastic (integracja GCP), data view `doc2-app-logs`, JSON rozbity na pola.
- Aplikację identyfikuje `container.name`: `doc2tools-viewer-api` (WebApi), `doc2tools-viewer-service` (Services),
  `doc2tools-viewer-frontend` (GUI). W tym samym data view są inne aplikacje platformy (doc2-webapi, doc2-ogate,
  doc2-ws, doc2-qss) — każdy filtr w pakiecie ma term na `container.name`, żeby ich nie zliczać.
- Instancja (pod): `resource.labels.pod_name`. Runtime field `app` = `container.name`.
- W plikach 02/04/06 jest placeholder `DOC2_APP_LOGS_INDEX` — podstaw wzorzec indeksu z data view doc2-app-logs
  (Stack Management → Data views → doc2-app-logs → pole "Index pattern").
- Zamiast importować data view `d2-logs` z NDJSON, możesz dodać trzy runtime fields (`route`, `doc_id`, `app`)
  do istniejącego `doc2-app-logs` (skrypty Painless w build_dashboard.py) i po imporcie przepiąć panele Lens
  na ten data view. Prostsza droga: zaimportuj NDJSON i w data view `d2-logs` zmień tytuł na wzorzec doc2-app-logs.

## Runtime fields (data view logs-d2-*, generowane w build_dashboard.py)

- route — ścieżka HTTP z GUID-ami zamienionymi na {id}, lowercase; z httpPath (oba API), RequestPath (Serilog) lub url.path (ECS).
  Przykład: /api/documentstorage/{id}/versions/{id}/finish. Bez regexów, więc działa przy domyślnym script.painless.regex.enabled.
- doc_id — pierwszy GUID w ścieżce = masterId dokumentu (do unique_count "ile dokumentów").
- app — service.name (WebApi/ECS) albo płaskie service (Services przed pipeline'em); po pipeline d2-logs oba są w service.name.

Selektor wpisu access-logu: WebApi = elapsedMs : * (tylko RequestObservabilityMiddleware loguje to pole),
Services = Elapsed : * (tylko UseSerilogRequestLogging). Inne wpisy tego samego requestu też mają
http.response.status_code (ECS), więc liczenie requestów po samym statusie zawyżałoby wynik.

## Trzy małe zmiany w kodzie, które domykają luki (NIE wykonane — do decyzji)

1. Pytanie 5 dokładne bez zmian w backendzie: w document-editor.ts w downloadOriginalDocument() wołać
   pobranie z parametrem ?reason=userDownload (osobna metoda w document-storage.service.ts albo parametr).
   Formatter WebApi już emituje url.query na wpisie access-logu, więc kubełek "pobranie_na_dysk" w Q5
   i lejku zacznie działać natychmiast. Query string nie jest maskowany (reason nie jest na liście RedactedPropertyNames).
2. Pytanie 7 dla frontendu: endpoint POST /api/document/client-log + wysyłka z GlobalErrorHandler
   (szkic w OBSERVABILITY_DASHBOARDS.md, sekcja 6 / B-05) — wtedy Q7d, panel 7a (seria "browser") i alert A7 ożywają.
   Do tego interceptor wysyłający X-Correlation-ID (backend już go czyta) = korelacja przeglądarka -> API -> wysyłka.
3. Pytanie 6 dla frontendu z logów zamiast Heartbeat: w nginx.conf włączyć access_log w formacie JSON na stdout
   (Filebeat nginx module) — wtedy Q7f pokaże 5xx/404 z nginx, a "cisza" GUI będzie widoczna jak dla API.
   Heartbeat pozostaje prostszy i pewniejszy (sonda z zewnątrz, niezależna od ruchu użytkowników).

## Pułapki, na które trafisz

- Kafelek "Edytowane teraz" ma własny zakres czasu (15 min) niezależny od time pickera dashboardu — tak ma być.
- Pole message jest text: w KQL używaj frazy w cudzysłowie (message : "Delivery sent to recipient"), nie wildcardów.
- LoggingBehaviour MediatR loguje {@Request} — dla SaveDocumentCommand to cała treść dokumentu. Template wyłącza
  indeksowanie pola Request (enabled:false), ale treść nadal siedzi w _source i liczy się do storage. Docelowo B-08.
- Services nie maskuje właściwości zdarzeń (B-01) — ogranicz dostęp do indeksu rolą do czasu wyrównania formattera.
- Sondy /api/health trafiają do access logu WebApi/Services (kilkadziesiąt % wolumenu). Q6d z nich korzysta;
  jeśli je odfiltrujesz w pipeline (zakomentowany drop w 01-setup.console), Q6d przestanie działać, reszta nie.
