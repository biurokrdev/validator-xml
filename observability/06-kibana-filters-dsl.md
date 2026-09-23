# Gotowe filtry do „Add filter → Edit as Query DSL" (Kibana 9.5, dashboard „Doc2 Edytor - status")

Kontekst z Twojego dashboardu: kafelki Lens Metric z „Normalize by unit: per minute" i filtrem
`container.name:"doc2tools-viewer-frontend"`. Kafelki „Base file download" i „Received file" pokazują 0 — poniżej
filtry, które trafiają w to, co aplikacje faktycznie logują.

## 0. Najpierw sprawdź, czy JSON z API jest rozbity na pola (Dev Tools)

```
GET DOC2_APP_LOGS_INDEX/_search
{
  "size": 1,
  "query": { "bool": { "filter": [ { "match_phrase": { "message": "responded" } } ] } },
  "_source": ["container.name", "message", "httpPath", "statusCode", "service", "MasterId", "level"]
}
```

Ze screenów Discover (data view `doc2-app-logs`, GKE → GCP Cloud Logging → Elastic) wynika, że JSON JEST
parsowany na pola (widać m.in. `ConnectionId`, `ActionName`, 858 pól) → obowiązuje **wariant A**. Wariant B
zostawiam jako awaryjny, gdyby dla któregoś wpisu pola nie były rozbite.

Kontenery naszych aplikacji (pole `container.name`): `doc2tools-viewer-api` (WebApi), `doc2tools-viewer-service`
(Services), `doc2tools-viewer-frontend` (GUI). Pozostałe (`doc2-webapi`, `doc2-ogate`, `doc2-ws`, `doc2-qss`) to
inne aplikacje platformy — każdy filtr musi je wykluczać, dlatego wszędzie jest term na `container.name`.
Instancję (pod) identyfikuje `resource.labels.pod_name`.

## 1. „Received file" — plik przyjęty przez Services (ingest)

Wariant A i B (działa w obu, bo szuka frazy w `message`; fraza jest w logu niezależnie od parsowania):

```json
{ "query": { "bool": { "filter": [
  { "term": { "container.name": "doc2tools-viewer-service" } },
  { "match_phrase": { "message": "Zewnętrzny ingest dokumentu OK" } }
] } } }
```

Fallback po HTTP (wariant A): `POST /api/v1/document` → 201

```json
{ "query": { "bool": { "filter": [
  { "term": { "container.name": "doc2tools-viewer-service" } },
  { "term": { "httpMethod": "POST" } },
  { "term": { "httpPath": "/api/v1/document" } },
  { "term": { "statusCode": 201 } }
] } } }
```

## 2. „Base file download" — pobranie oryginału (v1)

Uwaga: to ten sam request, którym GUI ładuje podgląd read-only. Liczba = pobrania + podglądy.

Wariant A (regexp na keyword `httpPath`; wyklucza `.../versions/{id}/download`):

```json
{ "query": { "bool": { "filter": [
  { "term": { "container.name": "doc2tools-viewer-api" } },
  { "term": { "httpMethod": "GET" } },
  { "term": { "statusCode": 200 } },
  { "regexp": { "httpPath": "/api/documentstorage/[0-9a-fA-F-]{36}/download" } }
] } } }
```

Wariant B (po treści access-logu, message tekstowe):

```json
{ "query": { "bool": {
  "filter": [
    { "term": { "container.name": "doc2tools-viewer-api" } },
    { "match_phrase": { "message": "HTTP GET /api/documentstorage" } },
    { "match_phrase": { "message": "download responded 200" } }
  ],
  "must_not": [ { "match_phrase": { "message": "versions" } } ]
} } }
```

## 3. Pobranie EDYTOWANEGO pliku („Pobierz dokument")

Wariant A:

```json
{ "query": { "bool": { "filter": [
  { "term": { "container.name": "doc2tools-viewer-api" } },
  { "term": { "httpMethod": "POST" } },
  { "term": { "statusCode": 200 } },
  { "wildcard": { "httpPath": { "value": "/api/documentstorage/*/user-download" } } }
] } } }
```

Wariant B:

```json
{ "query": { "bool": { "filter": [
  { "term": { "container.name": "doc2tools-viewer-api" } },
  { "match_phrase": { "message": "user-download responded 200" } }
] } } }
```

## 4. Odesłane do aplikacji zewnętrznej (worker dostaw w WebApi)

```json
{ "query": { "bool": { "filter": [
  { "term": { "container.name": "doc2tools-viewer-api" } },
  { "match_phrase": { "message": "Delivery sent to recipient" } }
] } } }
```

Nieudane na stałe / dead-letter (do czerwonego kafelka):

```json
{ "query": { "bool": {
  "filter": [ { "term": { "container.name": "doc2tools-viewer-api" } } ],
  "should": [
    { "match_phrase_prefix": { "message": "Delivery dead-lettered" } },
    { "match_phrase_prefix": { "message": "Delivery permanently failed" } }
  ],
  "minimum_should_match": 1
} } }
```

## 5. Edytowane teraz (auto-save w oknie time pickera; kafelek ustaw na „Last 15 minutes")

Metryka w Lens: **Unique count** pola `doc_id` (runtime field z data view z pakietu) albo, bez runtime
fielda, Unique count pola `httpPath` (jeden dokument = jedna wersja edytowalna = jedna ścieżka).

Wariant A:

```json
{ "query": { "bool": { "filter": [
  { "term": { "container.name": "doc2tools-viewer-api" } },
  { "term": { "httpMethod": "PUT" } },
  { "range": { "statusCode": { "gte": 200, "lt": 300 } } },
  { "regexp": { "httpPath": "/api/documentstorage/[0-9a-fA-F-]{36}/versions/[0-9a-fA-F-]{36}" } }
] } } }
```

Wariant B:

```json
{ "query": { "bool": {
  "filter": [
    { "term": { "container.name": "doc2tools-viewer-api" } },
    { "match_phrase": { "message": "HTTP PUT /api/documentstorage" } },
    { "match_phrase": { "message": "responded 200" } }
  ],
  "must_not": [ { "match_phrase": { "message": "download" } } ]
} } }
```

## 6. Żyje? — Twoje kafelki HealthCheck „/m" są dobre; brakuje progu

Kafelek liczy wpisy per minutę per kontener. Dodaj w Lens „Color by value" z progiem: < 1/min = czerwony
(sondy LB uderzają w `/api/health` kilkadziesiąt razy na minutę, więc 0 przez minutę = usługa nie odpowiada
albo nie loguje). Filtr dla API, żeby liczyć tylko sondy, a nie cały ruch:

```json
{ "query": { "bool": { "filter": [
  { "term": { "container.name": "doc2tools-viewer-api" } },
  { "match_phrase": { "message": "/api/health responded 200" } }
] } } }
```

Frontend: nginx w repo ma `access_log off` na `/health`, więc Twoje „Frontend 4/m" liczy inne wpisy
(np. `index.html` lub błędy nginx). Jeśli chcesz sondy w logu GUI, usuń `access_log off` z bloku
`location = /health` w `D2GuiViewerEditor/nginx.conf`.

## 7. Błędy — sekcja „Exceptions / Errors"

WebApi + Services, wariant A (poziom logu z JSON):

```json
{ "query": { "bool": { "filter": [
  { "terms": { "container.name": ["doc2tools-viewer-api", "doc2tools-viewer-service"] } },
  { "terms": { "level": ["Error", "Critical", "Fatal"] } }
] } } }
```

Wariant B (pole `severity` z formattera GCP jest w surowej linii jako tekst):

```json
{ "query": { "bool": { "filter": [
  { "terms": { "container.name": ["doc2tools-viewer-api", "doc2tools-viewer-service"] } },
  { "bool": { "should": [
      { "match_phrase": { "message": "\"severity\":\"ERROR\"" } },
      { "match_phrase": { "message": "\"severity\":\"CRITICAL\"" } }
    ], "minimum_should_match": 1 } }
] } } }
```

Frontend (nginx) — odpowiedzi 5xx/4xx, jeśli access log nginx trafia do kontenera:

```json
{ "query": { "bool": { "filter": [
  { "term": { "container.name": "doc2tools-viewer-frontend" } },
  { "regexp": { "message": ".*\" (404|5[0-9]{2}) .*" } }
] } } }
```

(regexp na `message` typu text działa per token, więc pewniejsze jest włączenie modułu nginx w Elastic
Agent — wtedy masz `http.response.status_code` jako liczbę.)

Błędy JavaScript w przeglądarce nadal nie istnieją w logach — patrz B-05 w OBSERVABILITY_DASHBOARDS.md.

## Jak wkleić

Add filter → Edit as Query DSL → wklej cały blok `{ "query": { ... } }` → Custom label → Save.
Dla kafelków Lens możesz alternatywnie wpisać to samo w „Filter by" panelu (KQL), np.
`container.name:"doc2tools-viewer-api" and httpMethod:"POST" and httpPath:/api\/documentstorage\/.*\/user-download/ and statusCode:200`.
