"""Generator artefaktów Kibana/Elasticsearch dla D2 ViewerEditor.

Wyjście (obok tego pliku):
  02-queries.console        - zapytania Query DSL + ES|QL (Kibana Dev Tools), samowystarczalne
                              (każde zapytanie po trasach niesie własne runtime_mappings)
  03-kibana-dashboard.ndjson - data view logs-d2-* (runtime fields route/doc_id/app), data view heartbeat-*,
                              wizualizacje Lens, saved search i dashboard "D2 ViewerEditor — monitoring"

Jedno źródło prawdy dla skryptów Painless (route/doc_id/app) - używane i w DSL, i w data view.
Uruchom: python build_dashboard.py
"""
from __future__ import annotations

import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
INDEX = "DOC2_APP_LOGS_INDEX"
HB_INDEX = "heartbeat-*"
DV_LOGS = "d2-logs"
DV_HB = "d2-heartbeat"
WEBAPI = "doc2tools-viewer-api"
SERVICES = "doc2tools-viewer-service"
GUI = "doc2tools-viewer-frontend"

RESOLVE_PATH = (
    "String p=null; "
    "if(doc.containsKey('httpPath.keyword')&&doc['httpPath.keyword'].size()>0){p=doc['httpPath.keyword'].value;} "
    "else if(doc.containsKey('httpPath')&&doc['httpPath'].size()>0){p=doc['httpPath'].value;} "
    "else if(doc.containsKey('RequestPath.keyword')&&doc['RequestPath.keyword'].size()>0){p=doc['RequestPath.keyword'].value;} "
    "else if(doc.containsKey('RequestPath')&&doc['RequestPath'].size()>0){p=doc['RequestPath'].value;} "
    "else if(doc.containsKey('url.path')&&doc['url.path'].size()>0){p=doc['url.path'].value;} "
    "if(p==null){return;} "
)
IS_GUID = (
    "s.length()==36&&s.substring(8,9)=='-'&&s.substring(13,14)=='-'"
    "&&s.substring(18,19)=='-'&&s.substring(23,24)=='-'"
)
ROUTE_SCRIPT = RESOLVE_PATH + (
    "StringBuilder sb=new StringBuilder(); "
    "for(String s: p.splitOnToken('/')){ if(s.length()==0){continue;} sb.append('/'); "
    "if(" + IS_GUID + "){sb.append('{id}');} else {sb.append(s.toLowerCase());} } "
    "emit(sb.length()==0?'/':sb.toString());"
)
DOCID_SCRIPT = RESOLVE_PATH + (
    "for(String s: p.splitOnToken('/')){ if(" + IS_GUID + "){emit(s.toLowerCase()); return;} }"
)
APP_SCRIPT = (
    "if(doc.containsKey('container.name')&&doc['container.name'].size()>0){emit(doc['container.name'].value);return;} "
    "if(doc.containsKey('service.keyword')&&doc['service.keyword'].size()>0){emit(doc['service.keyword'].value);return;} "
    "if(doc.containsKey('service')&&doc['service'].size()>0){emit(doc['service'].value);return;} "
    "emit('unknown');"
)
RUNTIME = {
    "route": {"type": "keyword", "script": {"source": ROUTE_SCRIPT}},
    "doc_id": {"type": "keyword", "script": {"source": DOCID_SCRIPT}},
    "app": {"type": "keyword", "script": {"source": APP_SCRIPT}},
}

R_INGEST = "/api/v1/document"
R_EDIT_OPEN = "/api/documentstorage/{id}/versions/{id}/download"
R_BASE_DL = "/api/documentstorage/{id}/download"
R_AUTOSAVE = "/api/documentstorage/{id}/versions/{id}"
R_SAVE_NEW = "/api/documentstorage/{id}/save"
R_USER_DL = "/api/documentstorage/{id}/user-download"
R_FINISH = "/api/documentstorage/{id}/versions/{id}/finish"
R_HEALTH = "/api/health"


def rng(gte: str = "now-24h") -> dict:
    return {"range": {"@timestamp": {"gte": gte}}}


def svc(name: str) -> dict:
    return {"term": {"container.name": name}}


def route(r: str) -> dict:
    return {"term": {"route": r}}


def method(m: str) -> dict:
    return {"term": {"httpMethod": m}}


def status(v: int) -> dict:
    return {"term": {"statusCode": v}}


def status_range(**kw) -> dict:
    return {"range": {"statusCode": kw}}


def phrase(text: str, field: str = "message") -> dict:
    return {"match_phrase": {field: text}}


ACCESS_WEBAPI = {"exists": {"field": "elapsedMs"}}
ACCESS_SERVICES = {"exists": {"field": "Elapsed"}}
HIST_1H = {"date_histogram": {"field": "@timestamp", "calendar_interval": "1h"}}
HIST_1D = {"date_histogram": {"field": "@timestamp", "calendar_interval": "1d"}}


def search(filters: list, aggs: dict | None = None, size: int = 0, runtime: bool = False, extra: dict | None = None) -> dict:
    body: dict = {"size": size, "query": {"bool": {"filter": filters}}}
    if runtime:
        body["runtime_mappings"] = RUNTIME
    if aggs:
        body["aggs"] = aggs
    if extra:
        body.update(extra)
    return body


QUERIES: list[tuple[str, str, str, dict | str]] = []


def q(qid: str, title: str, body: dict, index: str = INDEX) -> None:
    QUERIES.append((qid, title, f"GET {index}/_search", body))


def esql(qid: str, title: str, query: str) -> None:
    QUERIES.append((qid, title, "POST _query", query))


q("Q1a", "Przyjęte pliki (ingest OK) - DOKŁADNE: jawny log biznesowy z MasterId/Mime/Classification",
  search([rng(), svc(SERVICES), phrase("Zewnętrzny ingest dokumentu OK"), {"exists": {"field": "MasterId"}}],
         {"pliki_unikalne": {"cardinality": {"field": "MasterId"}},
          "wg_mime": {"terms": {"field": "Mime", "size": 10}},
          "wg_klasyfikacji": {"terms": {"field": "Classification", "size": 10, "missing": "brak"}},
          "w_czasie": HIST_1H}))
q("Q1b", "Odrzucone przyjęcia (400 z powodem) - każdy wiersz to nieudana integracja u klienta",
  search([rng(), svc(SERVICES), phrase("Ingest dokumentu nie powiódł się")],
         {"powody": {"terms": {"field": "Error", "size": 10}}, "w_czasie": HIST_1H}))
q("Q1c", "Przyjęcia liczone po HTTP (fallback, gdyby komunikat logu się zmienił): POST /api/v1/document wg statusu",
  search([rng(), svc(SERVICES), ACCESS_SERVICES, method("POST"), route(R_INGEST)],
         {"wg_statusu": {"terms": {"field": "statusCode", "size": 10}}}, runtime=True))

q("Q2a", "Odesłane do odbiorcy - DOKŁADNE: 'Delivery sent to recipient' (scope: deliveryId, masterId, versionId, attempt)",
  search([rng(), svc(WEBAPI), phrase("Delivery sent to recipient"), {"exists": {"field": "deliveryId"}}],
         {"dostawy": {"cardinality": {"field": "deliveryId"}},
          "dokumenty": {"cardinality": {"field": "masterId"}},
          "za_ktora_proba": {"terms": {"field": "attempt", "size": 10}},
          "w_czasie": HIST_1H}))
q("Q2b", "Wysyłka - wszystkie wyniki (sent / retry / permanently failed / dead-lettered) + skuteczność",
  search([rng(), svc(WEBAPI), {"exists": {"field": "deliveryId"}}],
         {"wynik": {"filters": {"filters": {
              "wyslano": phrase("Delivery sent to recipient"),
              "retry_zaplanowany": {"match_phrase_prefix": {"message": "Delivery retry scheduled"}},
              "porazka_trwala": {"match_phrase_prefix": {"message": "Delivery permanently failed"}},
              "dead_letter": {"match_phrase_prefix": {"message": "Delivery dead-lettered"}},
              "wyjatek_w_probie": {"match_phrase_prefix": {"message": "Delivery attempt threw"}}}}},
          "dead_letter_lista": {"filter": {"match_phrase_prefix": {"message": "Delivery dead-lettered"}},
                                "aggs": {"dokumenty": {"terms": {"field": "masterId", "size": 50}}}}}))

q("Q3a", "Edytowane TERAZ (aktywny auto-save w ostatnich 15 min) - unikalne dokumenty z PUT .../versions/{id} 2xx",
  search([rng("now-15m"), svc(WEBAPI), ACCESS_WEBAPI, status_range(gte=200, lt=300),
          {"bool": {"should": [
              {"bool": {"filter": [method("PUT"), route(R_AUTOSAVE)]}},
              {"bool": {"filter": [method("POST"), route(R_SAVE_NEW)]}}],
                    "minimum_should_match": 1}}],
         {"dokumenty_w_edycji": {"cardinality": {"field": "doc_id"}},
          "uzytkownicy": {"cardinality": {"field": "userId"}},
          "lista": {"terms": {"field": "doc_id", "size": 100, "order": {"ostatni_zapis": "desc"}},
                    "aggs": {"ostatni_zapis": {"max": {"field": "@timestamp"}},
                             "zapisow": {"value_count": {"field": "elapsedMs"}}}}},
         runtime=True))
q("Q3b", "W TOKU (otwarte do edycji w ostatnich 7 dniach i NIE zakończone 'Zakończ i wyślij') - lista dokumentów",
  search([rng("now-7d"), svc(WEBAPI), ACCESS_WEBAPI, {"exists": {"field": "doc_id"}}],
         {"dokument": {"terms": {"field": "doc_id", "size": 5000},
                       "aggs": {
                           "otwarte": {"filter": {"bool": {"filter": [method("GET"), route(R_EDIT_OPEN), status(200)]}}},
                           "zapisy": {"filter": {"bool": {"filter": [method("PUT"), route(R_AUTOSAVE), status_range(lt=300)]}}},
                           "zakonczone": {"filter": {"bool": {"filter": [method("POST"), route(R_FINISH), status_range(lt=300)]}}},
                           "ostatnia_aktywnosc": {"max": {"field": "@timestamp"}},
                           "tylko_w_toku": {"bucket_selector": {
                               "buckets_path": {"o": "otwarte._count", "z": "zakonczone._count"},
                               "script": "params.o > 0 && params.z == 0"}}}}},
         runtime=True))

q("Q4", "Pobrania edytowanego pliku ('Pobierz dokument') - DOKŁADNE: POST .../user-download 200 (+ 403 = zablokowane flagą userDownload)",
  search([rng(), svc(WEBAPI), ACCESS_WEBAPI, method("POST"), route(R_USER_DL)],
         {"wynik": {"filters": {"filters": {"pobrano": status(200), "zablokowano_403": status(403), "blad_5xx": status_range(gte=500)}}},
          "dokumenty": {"filter": status(200), "aggs": {"unikalne": {"cardinality": {"field": "doc_id"}}}},
          "uzytkownicy": {"filter": status(200), "aggs": {"unikalni": {"cardinality": {"field": "userId"}}}},
          "w_czasie": HIST_1H},
         runtime=True))

q("Q5", "Pobrania oryginału (v1): GET .../{id}/download 200. UWAGA: dziś = 'Pobierz oryginał' + załadowanie PODGLĄDU read-only "
        "(ten sam request). Po dodaniu w GUI '?reason=userDownload' kubełek 'pobranie_na_dysk' staje się dokładny.",
  search([rng(), svc(WEBAPI), ACCESS_WEBAPI, method("GET"), route(R_BASE_DL), status(200)],
         {"podzial": {"filters": {"other_bucket_key": "podglad_lub_nieoznaczone",
                                  "filters": {"pobranie_na_dysk": {"term": {"url.query": "reason=userDownload"}}}}},
          "dokumenty": {"cardinality": {"field": "doc_id"}},
          "w_czasie": HIST_1H},
         runtime=True))

q("Q6a", "ŻYJE? Heartbeat (sondy HTTP z zewnątrz, 05-heartbeat.yml): ostatni status każdego z 3 monitorów",
  search([rng("now-10m")],
         {"monitor": {"terms": {"field": "monitor.id", "size": 10},
                      "aggs": {"ostatni": {"top_hits": {"size": 1, "sort": [{"@timestamp": "desc"}],
                                                        "_source": ["@timestamp", "monitor.id", "monitor.name", "monitor.status", "url.full", "http.response.status_code", "error.message"]}}}}}),
  index=HB_INDEX)
q("Q6b", "ŻYJE? Heartbeat: dostępność 24h per monitor (up vs down) + ostatni 'down'",
  search([rng()],
         {"monitor": {"terms": {"field": "monitor.id", "size": 10},
                      "aggs": {"stan": {"terms": {"field": "monitor.status", "size": 2}},
                               "ostatni_down": {"filter": {"term": {"monitor.status": "down"}},
                                                "aggs": {"kiedy": {"max": {"field": "@timestamp"}}}}}}}),
  index=HB_INDEX)
q("Q6c", "ŻYJE? Z logów (WebApi + Services, bez frontendu): ostatni wpis per aplikacja i instancja - rosnąca cisza = padła lub straciła stdout",
  search([rng("now-24h")],
         {"aplikacja": {"terms": {"field": "app", "size": 10, "missing": "unknown"},
                        "aggs": {"ostatni_wpis": {"max": {"field": "@timestamp"}},
                                 "instancja": {"terms": {"field": "resource.labels.pod_name", "size": 20},
                                               "aggs": {"ostatni_wpis": {"max": {"field": "@timestamp"}}}}}}},
         runtime=True))
q("Q6d", "ŻYJE? Sondy /api/health i /health w access logu obu API (o ile nie odfiltrowane w pipeline) - liczba 200 na minutę per aplikacja",
  search([rng("now-30m"), {"terms": {"route": [R_HEALTH, "/health", "/api/health/detailed"]}}, status(200)],
         {"aplikacja": {"terms": {"field": "app", "size": 5},
                        "aggs": {"na_minute": {"date_histogram": {"field": "@timestamp", "fixed_interval": "1m", "min_doc_count": 0}}}}},
         runtime=True))

q("Q7a", "Błędy WebApi: wyjątki (level Error/Critical) wg typu, klasyfikacji event.reason i trasy",
  search([rng(), svc(WEBAPI), {"terms": {"level": ["Error", "Critical"]}}],
         {"wg_typu": {"terms": {"field": "exceptionType", "size": 15, "missing": "(bez wyjątku - np. 5xx z access logu)"},
                      "aggs": {"przyklad": {"top_hits": {"size": 1, "sort": [{"@timestamp": "desc"}], "_source": ["message", "correlationId", "route"]}}}},
          "wg_klasyfikacji": {"terms": {"field": "event.reason", "size": 10}},
          "wg_trasy": {"terms": {"field": "route", "size": 15}},
          "w_czasie": HIST_1H},
         runtime=True))
q("Q7b", "Błędy Services: level Error/Critical(Fatal) wg exceptionType i trasy",
  search([rng(), svc(SERVICES), {"terms": {"level": ["Error", "Critical", "Fatal"]}}],
         {"wg_typu": {"terms": {"field": "exceptionType", "size": 15, "missing": "(bez wyjątku)"},
                      "aggs": {"przyklad": {"top_hits": {"size": 1, "sort": [{"@timestamp": "desc"}], "_source": ["message", "correlationId"]}}}},
          "wg_trasy": {"terms": {"field": "route", "size": 15}},
          "w_czasie": HIST_1H},
         runtime=True))
q("Q7c", "HTTP 5xx / 4xx z access logu obu API wg aplikacji i trasy (SLI: 5xx / wszystkie)",
  search([rng(), {"exists": {"field": "statusCode"}},
          {"bool": {"should": [ACCESS_WEBAPI, ACCESS_SERVICES], "minimum_should_match": 1}}],
         {"aplikacja": {"terms": {"field": "app", "size": 5},
                        "aggs": {"wszystkie": {"value_count": {"field": "statusCode"}},
                                 "5xx": {"filter": status_range(gte=500), "aggs": {"wg_trasy": {"terms": {"field": "route", "size": 10}}}},
                                 "4xx": {"filter": status_range(gte=400, lt=500), "aggs": {"wg_statusu": {"terms": {"field": "statusCode", "size": 10}}}},
                                 "sli_5xx": {"bucket_script": {"buckets_path": {"e": "5xx._count", "a": "wszystkie"}, "script": "params.a == 0 ? 0 : params.e / params.a"}}}}},
         runtime=True))
q("Q7d", "Błędy FRONTENDU - po wdrożeniu endpointu client-log (B-05 w OBSERVABILITY_DASHBOARDS.md): wpisy source=browser wg ekranu",
  search([rng(), {"term": {"source": "browser"}}, {"terms": {"level": ["Error", "Warning"]}}],
         {"wg_ekranu": {"terms": {"field": "clientRoute", "size": 15}},
          "wg_wersji_gui": {"terms": {"field": "appVersion", "size": 5}},
          "sesje": {"cardinality": {"field": "correlationId"}},
          "w_czasie": HIST_1H}))
q("Q7e", "Błędy FRONTENDU widoczne DZIŚ, pośrednio: requesty GUI -> WebApi zakończone 4xx/5xx na ścieżkach użytkownika",
  search([rng(), svc(WEBAPI), ACCESS_WEBAPI, status_range(gte=400),
          {"terms": {"route": ["/api/document/open", "/api/document/save", R_AUTOSAVE, R_SAVE_NEW, R_FINISH, R_USER_DL, R_BASE_DL, R_EDIT_OPEN]}}],
         {"wg_trasy": {"terms": {"field": "route", "size": 10},
                       "aggs": {"wg_statusu": {"terms": {"field": "statusCode", "size": 5}}}},
          "uzytkownicy_dotknieci": {"cardinality": {"field": "userId"}}},
         runtime=True))
q("Q7f", "Błędy FRONTENDU z nginx (po włączeniu access logu JSON i Filebeat nginx module): 5xx i 404 na assetach",
  search([rng(), {"term": {"event.dataset": "nginx.access"}}, {"range": {"http.response.status_code": {"gte": 400}}}],
         {"wg_statusu": {"terms": {"field": "http.response.status_code", "size": 10}},
          "wg_sciezki": {"terms": {"field": "url.original", "size": 15}}}),
  index="filebeat-*")

GUID_RE = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"
esql("E1", "KPI 24h jednym zapytaniem (przyjęte / odesłane / edytowane 15 min / pobrania edyt. / pobrania oryg. / błędy per API)", f"""
FROM {INDEX}
| WHERE @timestamp >= NOW() - 24 hours
| EVAL path = COALESCE(httpPath, RequestPath, url.path)
| EVAL r = TO_LOWER(REPLACE(path, "{GUID_RE}", "{{id}}"))
| EVAL is_access = elapsedMs IS NOT NULL OR Elapsed IS NOT NULL
| STATS
    przyjete_pliki      = COUNT(CASE(container.name == "{SERVICES}" AND STARTS_WITH(message, "Zewnętrzny ingest dokumentu OK"), 1, NULL)),
    odeslane_pliki      = COUNT(CASE(message == "Delivery sent to recipient", 1, NULL)),
    edytowane_15min     = COUNT_DISTINCT(CASE(is_access AND httpMethod == "PUT" AND r == "{R_AUTOSAVE}" AND statusCode < 300 AND @timestamp >= NOW() - 15 minutes, path, NULL)),
    pobrania_edytowany  = COUNT(CASE(is_access AND httpMethod == "POST" AND r == "{R_USER_DL}" AND statusCode == 200, 1, NULL)),
    pobrania_oryginal   = COUNT(CASE(is_access AND httpMethod == "GET" AND r == "{R_BASE_DL}" AND statusCode == 200, 1, NULL)),
    pobrania_oryg_dysk  = COUNT(CASE(is_access AND httpMethod == "GET" AND r == "{R_BASE_DL}" AND statusCode == 200 AND url.query == "reason=userDownload", 1, NULL)),
    zakonczono          = COUNT(CASE(is_access AND httpMethod == "POST" AND r == "{R_FINISH}" AND statusCode < 300, 1, NULL)),
    bledy_webapi        = COUNT(CASE(container.name == "{WEBAPI}" AND level IN ("Error", "Critical"), 1, NULL)),
    bledy_services      = COUNT(CASE(container.name == "{SERVICES}" AND level IN ("Error", "Critical", "Fatal"), 1, NULL)),
    http5xx_webapi      = COUNT(CASE(container.name == "{WEBAPI}" AND is_access AND statusCode >= 500, 1, NULL)),
    http5xx_services    = COUNT(CASE(container.name == "{SERVICES}" AND is_access AND statusCode >= 500, 1, NULL))
""".strip())

esql("E2", "Dokumenty W TOKU edycji (otwarte w 7 dni, bez 'Zakończ'), z ostatnim auto-save - lista + na końcu liczba", f"""
FROM {INDEX}
| WHERE @timestamp >= NOW() - 7 days AND container.name == "{WEBAPI}" AND elapsedMs IS NOT NULL
| GROK httpPath "/api/documentstorage/%{{UUID:doc_id}}"
| WHERE doc_id IS NOT NULL
| EVAL r = TO_LOWER(REPLACE(httpPath, "{GUID_RE}", "{{id}}"))
| STATS
    otwarte       = MAX(CASE(httpMethod == "GET"  AND r == "{R_EDIT_OPEN}" AND statusCode == 200, 1, 0)),
    zapisy        = COUNT(CASE(httpMethod == "PUT"  AND r == "{R_AUTOSAVE}" AND statusCode < 300, 1, NULL)),
    ostatni_zapis = MAX(CASE(httpMethod == "PUT"  AND r == "{R_AUTOSAVE}" AND statusCode < 300, @timestamp, NULL)),
    zakonczone    = MAX(CASE(httpMethod == "POST" AND r == "{R_FINISH}"   AND statusCode < 300, 1, 0))
  BY doc_id
| WHERE otwarte == 1 AND zakonczone == 0
| SORT ostatni_zapis DESC
| LIMIT 500
""".strip())

esql("E2b", "To samo co E2, ale sama LICZBA dokumentów w toku", f"""
FROM {INDEX}
| WHERE @timestamp >= NOW() - 7 days AND container.name == "{WEBAPI}" AND elapsedMs IS NOT NULL
| GROK httpPath "/api/documentstorage/%{{UUID:doc_id}}"
| WHERE doc_id IS NOT NULL
| EVAL r = TO_LOWER(REPLACE(httpPath, "{GUID_RE}", "{{id}}"))
| STATS
    otwarte    = MAX(CASE(httpMethod == "GET"  AND r == "{R_EDIT_OPEN}" AND statusCode == 200, 1, 0)),
    zakonczone = MAX(CASE(httpMethod == "POST" AND r == "{R_FINISH}"   AND statusCode < 300, 1, 0))
  BY doc_id
| WHERE otwarte == 1 AND zakonczone == 0
| STATS dokumentow_w_toku = COUNT(*)
""".strip())

esql("E3", "ŻYJE? Cisza w logach per aplikacja/instancja (minuty od ostatniego wpisu)", f"""
FROM {INDEX}
| WHERE @timestamp >= NOW() - 1 day
| STATS ostatni_wpis = MAX(@timestamp), wpisow = COUNT(*) BY container.name, resource.labels.pod_name
| EVAL cisza_min = DATE_DIFF("minutes", ostatni_wpis, NOW())
| SORT cisza_min DESC
""".strip())

esql("E4", "Błędy per aplikacja i godzina (WebApi + Services + frontend po B-05)", f"""
FROM {INDEX}
| WHERE @timestamp >= NOW() - 24 hours AND level IN ("Error", "Critical", "Fatal")
| EVAL app = CASE(source == "browser", "D2GuiViewerEditor (browser)", container.name)
| EVAL godzina = DATE_TRUNC(1 hour, @timestamp)
| STATS bledy = COUNT(*), typow = COUNT_DISTINCT(exceptionType), requestow = COUNT_DISTINCT(correlationId) BY app, godzina
| SORT godzina DESC, bledy DESC
""".strip())

esql("E5", "Heartbeat: ostatni status 3 monitorów (frontend / WebApi / Services) + dostępność 24h", f"""
FROM {HB_INDEX}
| WHERE @timestamp >= NOW() - 24 hours
| STATS ostatnio = MAX(@timestamp),
        sond = COUNT(*),
        down = COUNT(CASE(monitor.status == "down", 1, NULL)),
        ostatni_down = MAX(CASE(monitor.status == "down", @timestamp, NULL))
  BY monitor.id, monitor.name
| EVAL dostepnosc_pct = ROUND(100.0 * (sond - down) / sond, 2)
| SORT monitor.id
""".strip())


def write_console() -> str:
    out = [
        "# ============================================================================",
        "# 02 — Zapytania monitorujące D2 ViewerEditor (Kibana -> Dev Tools; wklej całość, uruchamiaj pojedynczo)",
        "# Wygenerowane przez build_dashboard.py — nie edytuj ręcznie, zmieniaj generator.",
        "# Każde zapytanie po trasach niesie własne runtime_mappings (route / doc_id / app), więc działa",
        "# także bez data view z 03-kibana-dashboard.ndjson. Zakres czasu: ostatnie 24 h (zmień 'now-24h').",
        "# Docelowa platforma: Elasticsearch/Kibana 9.5.3 (ES|QL, runtime fields, bucket_selector - wszystko wspierane).",
        "# ============================================================================",
        "",
    ]
    for qid, title, verb, body in QUERIES:
        out.append(f"# ---- {qid}: {title}")
        out.append(verb)
        if isinstance(body, str):
            out.append('{\n  "query": """\n' + body + '\n  """\n}')
        else:
            out.append(json.dumps(body, ensure_ascii=False, indent=2))
        out.append("")
    path = os.path.join(HERE, "02-queries.console")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(out))
    return path


CORE = "8.8.0"


def col_count(label: str = "Liczba") -> dict:
    return {"label": label, "dataType": "number", "operationType": "count", "isBucketed": False,
            "scale": "ratio", "sourceField": "___records___", "params": {"emptyAsNull": False}}


def col_unique(field: str, label: str) -> dict:
    return {"label": label, "dataType": "number", "operationType": "unique_count", "isBucketed": False,
            "scale": "ratio", "sourceField": field, "params": {"emptyAsNull": False}}


def col_max_date(label: str = "Ostatni wpis") -> dict:
    return {"label": label, "dataType": "date", "operationType": "max", "isBucketed": False,
            "scale": "ratio", "sourceField": "@timestamp", "params": {"emptyAsNull": False}}


def col_last_value(field: str, label: str) -> dict:
    return {"label": label, "dataType": "string", "operationType": "last_value", "isBucketed": False,
            "scale": "ordinal", "sourceField": field,
            "filter": {"query": f'"{field}": *', "language": "kuery"},
            "params": {"sortField": "@timestamp", "showArrayValues": False}}


def col_date_hist(interval: str = "auto") -> dict:
    return {"label": "@timestamp", "dataType": "date", "operationType": "date_histogram", "isBucketed": True,
            "scale": "interval", "sourceField": "@timestamp",
            "params": {"interval": interval, "includeEmptyRows": True, "dropPartials": False}}


def col_terms(field: str, label: str, size: int, order_col: str = "m1") -> dict:
    return {"label": label, "dataType": "string", "operationType": "terms", "isBucketed": True,
            "scale": "ordinal", "sourceField": field,
            "params": {"size": size, "orderBy": {"type": "column", "columnId": order_col}, "orderDirection": "desc",
                       "otherBucket": False, "missingBucket": False, "parentFormat": {"id": "terms"},
                       "include": [], "exclude": [], "includeIsRegex": False, "excludeIsRegex": False}}


def col_filters(label: str, items: list[tuple[str, str]]) -> dict:
    return {"label": label, "dataType": "string", "operationType": "filters", "isBucketed": True, "scale": "ordinal",
            "params": {"filters": [{"input": {"query": kql, "language": "kuery"}, "label": lab} for lab, kql in items]}}


def lens(obj_id: str, title: str, vis_type: str, vis_state: dict, columns: dict, order: list[str], kql: str, dv: str) -> dict:
    return {
        "id": obj_id, "type": "lens", "managed": False,
        "coreMigrationVersion": CORE, "typeMigrationVersion": "8.9.0",
        "attributes": {
            "title": title, "description": "", "visualizationType": vis_type,
            "state": {
                "visualization": vis_state,
                "query": {"query": kql, "language": "kuery"},
                "filters": [],
                "datasourceStates": {"formBased": {"layers": {"l1": {"columns": columns, "columnOrder": order,
                                                                       "incompleteColumns": {}, "sampling": 1}}}},
                "internalReferences": [], "adHocDataViews": {},
            },
        },
        "references": [{"id": dv, "name": "indexpattern-datasource-layer-l1", "type": "index-pattern"}],
    }


def metric(obj_id: str, title: str, kql: str, col: dict, dv: str = DV_LOGS) -> dict:
    return lens(obj_id, title, "lnsMetric", {"layerId": "l1", "layerType": "data", "metricAccessor": "m1"},
                {"m1": col}, ["m1"], kql, dv)


def xy(obj_id: str, title: str, kql: str, series: str, split: dict, metric_col: dict | None = None,
       interval: str = "auto", dv: str = DV_LOGS) -> dict:
    vis = {"legend": {"isVisible": True, "position": "right"}, "valueLabels": "hide", "fittingFunction": "None",
           "axisTitlesVisibilitySettings": {"x": True, "yLeft": True, "yRight": True},
           "tickLabelsVisibilitySettings": {"x": True, "yLeft": True, "yRight": True},
           "labelsOrientation": {"x": 0, "yLeft": 0, "yRight": 0},
           "gridlinesVisibilitySettings": {"x": True, "yLeft": True, "yRight": True},
           "preferredSeriesType": series,
           "layers": [{"layerId": "l1", "accessors": ["m1"], "position": "top", "seriesType": series,
                       "showGridlines": False, "layerType": "data", "xAccessor": "x1", "splitAccessor": "s1"}]}
    cols = {"x1": col_date_hist(interval), "s1": split, "m1": metric_col or col_count()}
    return lens(obj_id, title, "lnsXY", vis, cols, ["s1", "x1", "m1"], kql, dv)


def table(obj_id: str, title: str, kql: str, buckets: list[dict], metrics: list[dict], dv: str = DV_LOGS) -> dict:
    cols: dict = {}
    order: list[str] = []
    for i, b in enumerate(buckets):
        cols[f"s{i}"] = b
        order.append(f"s{i}")
    for i, m in enumerate(metrics, start=1):
        cols[f"m{i}"] = m
        order.append(f"m{i}")
    vis = {"layerId": "l1", "layerType": "data", "columns": [{"columnId": c} for c in order]}
    return lens(obj_id, title, "lnsDatatable", vis, cols, order, kql, dv)


def saved_search(obj_id: str, title: str, kql: str, columns: list[str], dv: str = DV_LOGS) -> dict:
    return {
        "id": obj_id, "type": "search", "managed": False,
        "coreMigrationVersion": CORE, "typeMigrationVersion": "8.0.0",
        "attributes": {
            "title": title, "description": "", "columns": columns, "sort": [["@timestamp", "desc"]],
            "grid": {}, "hideChart": False, "isTextBasedQuery": False,
            "kibanaSavedObjectMeta": {"searchSourceJSON": json.dumps(
                {"query": {"query": kql, "language": "kuery"}, "filter": [],
                 "indexRefName": "kibanaSavedObjectMeta.searchSourceJSON.index"})},
        },
        "references": [{"id": dv, "name": "kibanaSavedObjectMeta.searchSourceJSON.index", "type": "index-pattern"}],
    }


def data_view(obj_id: str, title: str, name: str, runtime: dict | None = None) -> dict:
    return {
        "id": obj_id, "type": "index-pattern", "managed": False,
        "coreMigrationVersion": CORE, "typeMigrationVersion": "8.0.0",
        "attributes": {"title": title, "name": name, "timeFieldName": "@timestamp", "fields": "[]",
                       "fieldAttrs": "{}", "fieldFormatMap": "{}", "sourceFilters": "[]", "typeMeta": "{}",
                       "runtimeFieldMap": json.dumps(runtime or {}),
                       "allowHidden": False},
        "references": [],
    }


K_WEBAPI_ACC = f'app : "{WEBAPI}" and elapsedMs : *'
K_SERVICES = f'app : "{SERVICES}"'
K_ACCESS_ANY = 'statusCode : * and (elapsedMs : * or Elapsed : *)'
K_ERRORS = 'level : ("Error" or "Critical" or "Fatal")'
K_INGEST_OK = f'{K_SERVICES} and message : "Zewnętrzny ingest dokumentu OK" and MasterId : *'
K_DELIVERED = f'app : "{WEBAPI}" and deliveryId : * and message : "Delivery sent to recipient"'
K_EDITING = (f'{K_WEBAPI_ACC} and statusCode < 300 and ((httpMethod : "PUT" and route : "{R_AUTOSAVE}") '
             f'or (httpMethod : "POST" and route : "{R_SAVE_NEW}"))')
K_USER_DL = f'{K_WEBAPI_ACC} and httpMethod : "POST" and route : "{R_USER_DL}" and statusCode : 200'
K_BASE_DL = f'{K_WEBAPI_ACC} and httpMethod : "GET" and route : "{R_BASE_DL}" and statusCode : 200'
K_EDIT_OPEN = f'{K_WEBAPI_ACC} and httpMethod : "GET" and route : "{R_EDIT_OPEN}" and statusCode : 200'
K_FINISH = f'{K_WEBAPI_ACC} and httpMethod : "POST" and route : "{R_FINISH}" and statusCode < 300'

OBJECTS: list[dict] = [
    data_view(DV_LOGS, INDEX, "D2 logi (WebApi + Services)", RUNTIME),
    data_view(DV_HB, HB_INDEX, "D2 heartbeat (sondy HTTP)"),

    metric("d2-kpi-ingest", "1. Przyjęte pliki (ingest Services)", K_INGEST_OK, col_count("Przyjęte pliki")),
    metric("d2-kpi-delivered", "2. Odesłane do aplikacji zewnętrznej", K_DELIVERED, col_count("Odesłane pliki")),
    metric("d2-kpi-editing", "3. Edytowane teraz (auto-save, 15 min)", K_EDITING, col_unique("doc_id", "Dokumenty w edycji")),
    metric("d2-kpi-user-dl", "4. Pobrania edytowanego pliku", K_USER_DL, col_count("Pobrania (edytowany)")),
    metric("d2-kpi-base-dl", "5. Pobrania oryginału (łącznie z podglądem!)", K_BASE_DL, col_count("Pobrania v1 + podgląd")),
    metric("d2-kpi-errors", "7. Błędy (wszystkie aplikacje)", K_ERRORS, col_count("Wpisy Error/Critical")),

    metric("d2-hb-gui", "6a. Frontend (GUI) — status sondy", 'monitor.id : "d2-gui"', col_last_value("monitor.status", "GUI"), DV_HB),
    metric("d2-hb-webapi", "6b. WebApi — status sondy", 'monitor.id : "d2-webapi"', col_last_value("monitor.status", "WebApi"), DV_HB),
    metric("d2-hb-services", "6c. Services — status sondy", 'monitor.id : "d2-services"', col_last_value("monitor.status", "Services"), DV_HB),
    table("d2-silence", "6d. Ostatni log per aplikacja / instancja (cisza = padła)", "app : *",
          [col_terms("app", "Aplikacja", 5), col_terms("resource.labels.pod_name", "Instancja", 20)],
          [col_max_date("Ostatni wpis"), col_count("Wpisów")]),
    xy("d2-hb-down", "6e. Nieudane sondy (down) w czasie", 'monitor.status : "down"', "bar_stacked",
       col_terms("monitor.id", "Monitor", 5), dv=DV_HB),

    xy("d2-errors-app", "7a. Błędy w czasie per aplikacja", K_ERRORS, "line", col_terms("app", "Aplikacja", 5)),
    table("d2-errors-top", "7b. Top wyjątków (WebApi + Services)", f'{K_ERRORS} and exceptionType : *',
          [col_terms("app", "Aplikacja", 5), col_terms("exceptionType", "Typ wyjątku", 10)],
          [col_count("Wystąpień"), col_unique("correlationId", "Requestów")]),
    xy("d2-http-4xx-5xx", "7c. HTTP 4xx / 5xx (access log obu API)", K_ACCESS_ANY, "bar_stacked",
       col_filters("Status", [("4xx", "statusCode >= 400 and statusCode < 500"), ("5xx", "statusCode >= 500")])),
    xy("d2-delivery", "2b. Wysyłka do odbiorcy — wyniki prób", f'app : "{WEBAPI}" and deliveryId : *', "bar_stacked",
       col_filters("Wynik", [("wysłano", 'message : "Delivery sent to recipient"'),
                             ("retry", 'message : "Delivery retry scheduled"'),
                             ("porażka trwała", 'message : "Delivery permanently failed"'),
                             ("dead-letter", 'message : "Delivery dead-lettered"')])),

    xy("d2-funnel", "Lejek dzienny: przyjęte → otwarte → zapisy → pobrania → zakończone → odesłane", "app : *", "bar",
       col_filters("Zdarzenie", [("1 przyjęte (ingest)", K_INGEST_OK),
                                 ("otwarte do edycji", K_EDIT_OPEN),
                                 ("3 auto-save", K_EDITING),
                                 ("4 pobrania edytowanego", K_USER_DL),
                                 ("5 pobrania oryginału+podgląd", K_BASE_DL),
                                 ("zakończono (Zakończ i wyślij)", K_FINISH),
                                 ("2 odesłane", K_DELIVERED)]),
       interval="1d"),

    saved_search("d2-errors-search", "7d. Ostatnie błędy — szczegóły", K_ERRORS,
                 ["app", "level", "exceptionType", "route", "message", "correlationId", "userId"]),
]

PANELS: list[tuple[str, int, int, int, int, dict | None]] = [
    ("d2-kpi-ingest", 0, 0, 8, 6, None),
    ("d2-kpi-delivered", 8, 0, 8, 6, None),
    ("d2-kpi-editing", 16, 0, 8, 6, {"timeRange": {"from": "now-15m", "to": "now"}}),
    ("d2-kpi-user-dl", 24, 0, 8, 6, None),
    ("d2-kpi-base-dl", 32, 0, 8, 6, None),
    ("d2-kpi-errors", 40, 0, 8, 6, None),

    ("d2-hb-gui", 0, 6, 6, 6, {"timeRange": {"from": "now-15m", "to": "now"}}),
    ("d2-hb-webapi", 6, 6, 6, 6, {"timeRange": {"from": "now-15m", "to": "now"}}),
    ("d2-hb-services", 12, 6, 6, 6, {"timeRange": {"from": "now-15m", "to": "now"}}),
    ("d2-silence", 18, 6, 16, 10, None),
    ("d2-hb-down", 34, 6, 14, 10, None),

    ("d2-errors-app", 0, 16, 24, 10, None),
    ("d2-errors-top", 24, 16, 24, 10, None),
    ("d2-http-4xx-5xx", 0, 26, 24, 10, None),
    ("d2-delivery", 24, 26, 24, 10, None),

    ("d2-funnel", 0, 36, 48, 12, {"timeRange": {"from": "now-30d", "to": "now"}}),

    ("d2-errors-search", 0, 48, 48, 14, None),
]


def dashboard() -> dict:
    types = {o["id"]: o["type"] for o in OBJECTS}
    panels = []
    refs = []
    for i, (pid, x, y, w, h, cfg) in enumerate(PANELS):
        ref = f"panel_{i}"
        emb = {"enhancements": {}}
        if cfg:
            emb.update(cfg)
        panels.append({"version": "8.9.0", "type": types[pid],
                       "gridData": {"x": x, "y": y, "w": w, "h": h, "i": f"p{i}"},
                       "panelIndex": f"p{i}", "embeddableConfig": emb, "panelRefName": ref})
        refs.append({"id": pid, "name": ref, "type": types[pid]})
    return {
        "id": "d2-monitoring", "type": "dashboard", "managed": False,
        "coreMigrationVersion": CORE, "typeMigrationVersion": "8.9.0",
        "attributes": {
            "title": "D2 ViewerEditor — monitoring",
            "description": "Przyjęte / odesłane / edytowane / pobrania / żyje? / błędy — WebApi, Services, GUI. "
                           "Kafelek 5 liczy też podglądy read-only (ten sam request), dopóki GUI nie doda ?reason=userDownload.",
            "panelsJSON": json.dumps(panels, ensure_ascii=False),
            "optionsJSON": json.dumps({"useMargins": True, "syncColors": False, "syncCursor": True,
                                       "syncTooltips": False, "hidePanelTitles": False}),
            "timeRestore": True, "timeFrom": "now-24h", "timeTo": "now",
            "refreshInterval": {"pause": False, "value": 60000},
            "kibanaSavedObjectMeta": {"searchSourceJSON": json.dumps({"query": {"query": "", "language": "kuery"}, "filter": []})},
            "version": 1,
        },
        "references": refs,
    }


def write_ndjson() -> str:
    path = os.path.join(HERE, "03-kibana-dashboard.ndjson")
    objs = OBJECTS + [dashboard()]
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        for o in objs:
            f.write(json.dumps(o, ensure_ascii=False) + "\n")
        f.write(json.dumps({"excludedObjects": [], "excludedObjectsCount": 0, "exportedCount": len(objs),
                            "missingRefCount": 0, "missingReferences": []}) + "\n")
    ids = {o["id"] for o in objs}
    with open(path, encoding="utf-8") as f:
        for line in f:
            obj = json.loads(line)
            for r in obj.get("references", []):
                assert r["id"] in ids, f"brak referencji {r['id']}"
    return path


if __name__ == "__main__":
    print(write_console())
    print(write_ndjson())
    print(f"queries: {len(QUERIES)}, saved objects: {len(OBJECTS) + 1}")
