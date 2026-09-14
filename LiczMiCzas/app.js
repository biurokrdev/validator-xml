/* LiczMiCzas – timer dnia pracy i zadań z Azure Boards (bez zależności, localStorage). */
(() => {
  "use strict";

  const STORAGE_KEY = "liczmiczas.v2";
  const TICK_MS = 1000;
  const DEFAULT_SETTINGS = { orgUrl: "", workHours: 8, breakHours: 2 };

  /**
   * Model:
   *  state.days[YYYY-MM-DD] = Day
   *  Day     = { date, status: "working"|"paused", startedAt, tasks: Task[], segments: Segment[], events: Event[] }
   *  Event   = { id, type: "start"|"stop"|"resume", at }   – rozpoczęcie / zakończenie / wznowienie pracy
   *  Task    = { id, wi, title, type: "praca"|"org", estimateH }
   *  Segment = { id, kind: "task"|"break", taskId|null, start, end|null, comment }
   *
   *  Podczas pracy (status "working") dokładnie jeden segment jest otwarty (end === null):
   *  albo zadanie, albo przerwa (czas bez zadania). Zadania typu "org" liczą się do przerwy.
   */
  let state = load();

  // ---------- Persist ----------
  function load() {
    const empty = { settings: { ...DEFAULT_SETTINGS }, days: {} };
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return empty;
      const parsed = JSON.parse(raw);
      return {
        settings: { ...DEFAULT_SETTINGS, ...(parsed.settings || {}) },
        days: parsed.days && typeof parsed.days === "object" ? parsed.days : {},
      };
    } catch {
      return empty;
    }
  }

  function save() {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
  }

  // ---------- Pomocnicze ----------
  const pad = (n) => String(n).padStart(2, "0");
  const uid = () => Date.now().toString(36) + Math.random().toString(36).slice(2, 8);

  function dateKey(d = new Date()) {
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  }
  function fmtDatePl(key) {
    const [y, m, d] = key.split("-");
    return `${d}.${m}.${y}`;
  }
  function shiftDate(key, days) {
    const [y, m, d] = key.split("-").map(Number);
    return dateKey(new Date(y, m - 1, d + days));
  }
  function endOfDay(key) {
    const [y, m, d] = key.split("-").map(Number);
    return new Date(y, m - 1, d + 1).getTime() - 1000;
  }
  /** HH:MM -> timestamp danego dnia */
  function timeOnDay(key, hhmm) {
    const [y, m, d] = key.split("-").map(Number);
    const [hh, mm] = hhmm.split(":").map(Number);
    return new Date(y, m - 1, d, hh, mm, 0, 0).getTime();
  }
  function fmtClock(ts) {
    const d = new Date(ts);
    return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
  }
  function fmtClockInput(ts) {
    return fmtClock(ts);
  }
  /** sekundy -> "2.5 h" */
  function fmtHours(sec) {
    return `${Math.round((sec / 3600) * 100) / 100} h`;
  }
  /** sekundy -> "1:32" (godziny:minuty, zaokrąglone do minuty) */
  function fmtHM(sec) {
    const mins = Math.round(Math.max(0, sec) / 60);
    return `${Math.floor(mins / 60)}:${pad(mins % 60)}`;
  }
  /** "8:30" | "8.5" | "8,5" | "8" -> godziny (liczba) albo null */
  function parseEstimate(str) {
    const v = String(str ?? "").trim();
    if (!v) return null;
    const hm = v.match(/^(\d{1,3}):([0-5]?\d)$/);
    if (hm) return Number(hm[1]) + Number(hm[2]) / 60;
    if (v.includes(":")) return null; // np. "8:60" – nieprawidłowe minuty
    const n = parseFloat(v.replace(",", "."));
    return Number.isFinite(n) && n > 0 ? n : null;
  }
  /** godziny (liczba) -> "8:30" do pola formularza */
  function estimateToInput(h) {
    return h ? fmtHM(h * 3600) : "";
  }

  /** sekundy -> "3 h 30 min" / "45 min" / "2 h" */
  function fmtHMin(sec) {
    const mins = Math.round(Math.max(0, sec) / 60);
    const h = Math.floor(mins / 60);
    const m = mins % 60;
    if (h && m) return `${h} h ${m} min`;
    if (h) return `${h} h`;
    return `${m} min`;
  }
  /** sekundy -> "01:23:45" */
  function fmtHMS(sec) {
    sec = Math.max(0, Math.floor(sec));
    return `${pad(Math.floor(sec / 3600))}:${pad(Math.floor((sec % 3600) / 60))}:${pad(sec % 60)}`;
  }
  function escapeHtml(str) {
    return String(str ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  // ---------- Dostęp do danych ----------
  const settings = () => state.settings;
  const obligoSec = () => Math.max(0, settings().workHours - settings().breakHours) * 3600;
  const breakLimitSec = () => settings().breakHours * 3600;
  const workLimitSec = () => settings().workHours * 3600;

  function getDay(key) {
    return state.days[key] || null;
  }
  function ensureDay(key) {
    if (!state.days[key]) {
      state.days[key] = { date: key, status: "paused", startedAt: null, tasks: [], segments: [], events: [] };
    }
    if (!state.days[key].events) state.days[key].events = [];
    return state.days[key];
  }
  const today = () => dateKey();
  const todayDay = () => getDay(today());

  function findTask(day, id) {
    return day.tasks.find((t) => t.id === id) || null;
  }
  function openSegment(day) {
    return day.segments.find((s) => s.end === null) || null;
  }
  function segDuration(seg, now) {
    return Math.max(0, ((seg.end ?? now) - seg.start) / 1000);
  }
  function isWorking(day) {
    return !!day && day.status === "working";
  }

  function isWorkSegment(day, s) {
    if (s.kind !== "task") return false;
    const task = findTask(day, s.taskId);
    return !task || task.type !== "org";
  }

  /**
   * Znacznik czasu, w którym naliczony czas osiągnął limit (null, jeśli jeszcze nie).
   * onlyWork = true: liczy wyłącznie segmenty pracy (zadania typu "praca").
   */
  function limitReachedAt(day, limitSec, now = Date.now(), onlyWork = false) {
    let acc = 0;
    for (const s of day.segments.slice().sort((a, b) => a.start - b.start)) {
      if (onlyWork && !isWorkSegment(day, s)) continue;
      const dur = segDuration(s, now);
      if (acc + dur >= limitSec) return s.start + (limitSec - acc) * 1000;
      acc += dur;
    }
    return null;
  }

  /** Sekundy pracy (zadania typu "praca") wykonanej po podanym znaczniku czasu. */
  function workAfter(day, ts, now = Date.now()) {
    let sum = 0;
    for (const s of day.segments) {
      if (!isWorkSegment(day, s)) continue;
      const end = s.end ?? now;
      if (end > ts) sum += (end - Math.max(s.start, ts)) / 1000;
    }
    return sum;
  }

  /**
   * Sumy dla dnia.
   *
   * Model rozliczenia:
   *  - obligo = dzień (8:00) − przerwa (2:00) = 6:00 faktycznej pracy (zadania typu "praca");
   *  - dzień jest zaliczony, gdy obecność ≥ 8:00 ORAZ praca ≥ obligo (doneAt = późniejszy z tych momentów);
   *  - owed (czas do odpracowania): przed 8:00 obecności = przerwa ponad limit (zapowiedź wydłużenia dnia),
   *    po 8:00 obecności = brakująca praca do obliga – przerwa NIE zmniejsza tego czasu;
   *  - overtime (nadgodziny) = wyłącznie praca wykonana po zaliczeniu dnia; przerwa po zaliczeniu nie liczy się;
   *  - final = true (dzień zamknięty, z przeszłości): owed = cała brakująca praca do obliga, bez względu na obecność.
   */
  function dayTotals(day, now = Date.now(), final = false) {
    const t = { work: 0, org: 0, idle: 0, brk: 0, total: 0, overtime: 0, owed: 0, breakOver: 0, doneAt: null, shortWork: 0 };
    if (!day) return t;
    for (const s of day.segments) {
      const dur = segDuration(s, now);
      if (s.kind === "break") t.idle += dur;
      else {
        const task = findTask(day, s.taskId);
        if (task?.type === "org") t.org += dur;
        else t.work += dur;
      }
    }
    t.brk = t.idle + t.org;
    t.total = t.work + t.brk;
    t.breakOver = Math.max(0, t.brk - breakLimitSec());

    const presenceAt = t.total >= workLimitSec() ? limitReachedAt(day, workLimitSec(), now) : null;
    const workAt = t.work >= obligoSec() ? limitReachedAt(day, obligoSec(), now, true) : null;
    if (presenceAt !== null && workAt !== null) {
      t.doneAt = Math.max(presenceAt, workAt);
      t.overtime = workAfter(day, t.doneAt, now);
      t.owed = 0;
    } else {
      t.owed = presenceAt !== null || final ? Math.max(0, obligoSec() - t.work) : t.breakOver;
    }
    t.shortWork = final ? Math.max(0, obligoSec() - t.work) : 0;
    return t;
  }

  function taskSecondsInDay(day, taskId, now = Date.now()) {
    return day.segments.filter((s) => s.taskId === taskId).reduce((a, s) => a + segDuration(s, now), 0);
  }

  /** Klucz identyfikujący zadanie do podpowiedzi w formularzu (ID Azure albo tytuł). */
  function taskMatchKey(task) {
    return task.wi ? `wi:${task.wi}` : `t:${(task.title || "").trim().toLowerCase()}`;
  }

  /**
   * Czas zadania łącznie. Między dniami zadania są łączone WYŁĄCZNIE po ID Azure;
   * zadanie bez ID jest osobnym zadaniem każdego dnia, więc jego „łącznie” = czas z tego dnia.
   */
  function taskSecondsAllDays(day, task, now = Date.now()) {
    if (!task.wi) return taskSecondsInDay(day, task.id, now);
    let sum = 0;
    for (const d of Object.values(state.days)) {
      for (const t of d.tasks) {
        if (t.wi === task.wi) sum += taskSecondsInDay(d, t.id, now);
      }
    }
    return sum;
  }

  /** Znane zadania (z wszystkich dni) do podpowiedzi. */
  function knownTasks() {
    const map = new Map();
    for (const day of Object.values(state.days)) {
      for (const t of day.tasks) map.set(taskMatchKey(t), t);
    }
    return [...map.values()];
  }

  /** Tytuł do wyświetlenia – gdy brak tytułu, pokazujemy ID. */
  function taskTitle(task) {
    return (task.title || "").trim() || (task.wi ? `Zadanie #${task.wi}` : "(bez tytułu)");
  }
  /** "#123" albo "Brak ID" – do podsumowania. */
  function taskIdLabel(task) {
    return task.wi ? `#${task.wi}` : "Brak ID";
  }

  function workItemUrl(wi) {
    const base = (settings().orgUrl || "").trim().replace(/\/+$/, "");
    if (!base || !wi) return null;
    return `${base}/_workitems/edit/${encodeURIComponent(wi)}`;
  }

  // ---------- Operacje na dniu pracy ----------
  function closeOpen(day, now) {
    const open = openSegment(day);
    if (open) open.end = Math.max(open.start, now);
    return open;
  }
  function pushSegment(day, seg) {
    day.segments.push({ id: uid(), kind: "break", taskId: null, start: Date.now(), end: null, comment: "", ...seg });
  }
  function addEvent(day, type, at) {
    if (!day.events) day.events = [];
    day.events.push({ id: uid(), type, at });
  }
  function beginWorking(day, now) {
    if (!day.startedAt) {
      day.startedAt = now;
      addEvent(day, "start", now);
    } else if (day.status !== "working") {
      addEvent(day, "resume", now);
    }
    day.status = "working";
  }

  function startWork() {
    const day = ensureDay(today());
    if (isWorking(day)) return;
    const now = Date.now();
    beginWorking(day, now);
    pushSegment(day, { kind: "break", start: now });
    commit();
  }

  function stopWork() {
    const day = todayDay();
    if (!isWorking(day)) return;
    const now = Date.now();
    closeOpen(day, now);
    day.status = "paused";
    addEvent(day, "stop", now);
    commit();
  }

  function startTask(taskId) {
    const day = ensureDay(today());
    const now = Date.now();
    if (isWorking(day)) closeOpen(day, now);
    else beginWorking(day, now);
    pushSegment(day, { kind: "task", taskId, start: now });
    commit();
  }

  /** Zatrzymuje bieżące zadanie – czas dalej leci, ale jako przerwa. */
  function stopTask() {
    const day = todayDay();
    if (!isWorking(day)) return;
    const open = openSegment(day);
    if (!open || open.kind !== "task") return;
    const now = Date.now();
    closeOpen(day, now);
    pushSegment(day, { kind: "break", start: now });
    commit();
  }

  function addTaskAndStart({ wi, title, type, estimateH }) {
    const day = ensureDay(today());
    const task = { id: uid(), wi, title, type, estimateH };
    day.tasks.push(task);
    startTask(task.id);
  }

  /** Dni inne niż dzisiejszy z otwartym segmentem – zamknij na koniec tamtego dnia. */
  function closeStaleDays() {
    let changed = false;
    for (const day of Object.values(state.days)) {
      if (day.date === today()) continue;
      const open = openSegment(day);
      if (open) {
        open.end = Math.max(open.start, endOfDay(day.date));
        changed = true;
      }
      if (day.status === "working") {
        day.status = "paused";
        addEvent(day, "stop", endOfDay(day.date));
        changed = true;
      }
    }
    return changed;
  }

  function commit() {
    save();
    render();
  }

  // ---------- DOM ----------
  const $ = (sel) => document.querySelector(sel);
  const el = {
    clockDate: $("#clock-date"),
    clockTime: $("#clock-time"),
    wdStatus: $("#wd-status"),
    wdTimer: $("#wd-timer"),
    wdStart: $("#wd-start"),
    wdResume: $("#wd-resume"),
    wdStop: $("#wd-stop"),
    stWork: $("#st-work"),
    stWorkLimit: $("#st-work-limit"),
    stWorkBar: $("#st-work-bar"),
    stBreak: $("#st-break"),
    stBreakLimit: $("#st-break-limit"),
    stBreakBar: $("#st-break-bar"),
    stOrg: $("#st-org"),
    wdTotal: $("#wd-total"),
    stOvertime: $("#st-overtime"),
    stOvertimeSub: $("#st-overtime-sub"),
    stOwed: $("#st-owed"),
    stOwedSub: $("#st-owed-sub"),
    stOwedBox: $(".stat.owed"),
    stOvertimeBox: $(".stat.overtime"),
    form: $("#task-form"),
    fWi: $("#f-wi"),
    fTitle: $("#f-title"),
    fType: $("#f-type"),
    fEst: $("#f-est"),
    wiSuggestions: $("#wi-suggestions"),
    titleSuggestions: $("#title-suggestions"),
    listDate: $("#list-date"),
    search: $("#task-search"),
    searchClear: $("#task-search-clear"),
    searchInfo: $("#search-info"),
    timeline: $("#timeline"),
    emptyInfo: $("#empty-info"),
    summaryDate: $("#summary-date"),
    summaryMode: $("#summary-mode"),
    summaryShowId: $("#summary-show-id"),
    summaryShowRanges: $("#summary-show-ranges"),
    summaryText: $("#summary-text"),
    summaryView: $("#summary-view"),
    refreshBtn: $("#refresh-summary"),
    optionsToggle: $("#summary-options-toggle"),
    options: $("#summary-options"),
    dayPrev: $("#day-prev"),
    dayNext: $("#day-next"),
    dayToday: $("#day-today"),
    sWork: $("#s-work"),
    sBreak: $("#s-break"),
    sOrg: $("#s-org"),
    exportBtn: $("#export-json"),
    exportCsvEntries: $("#export-csv-entries"),
    exportCsvTasks: $("#export-csv-tasks"),
    importInput: $("#import-json"),
    clearDay: $("#clear-day"),
    dialog: $("#edit-dialog"),
    editForm: $("#edit-form"),
    eHeading: $("#e-heading"),
    eTaskFields: $("#e-task-fields"),
    eWi: $("#e-wi"),
    eTitle: $("#e-title"),
    eType: $("#e-type"),
    eEst: $("#e-est"),
    eStart: $("#e-start"),
    eEnd: $("#e-end"),
    eEndHint: $("#e-end-hint"),
    eComment: $("#e-comment"),
    eCancel: $("#e-cancel"),
  };

  let selectedDate = today();
  let editing = null; // { dayKey, segId }
  let searchQuery = "";
  let eventsOpen = false; // stan rozwinięcia „Zdarzenia dnia” w podsumowaniu (przeżywa odświeżanie co sekundę)

  /** Czy zadanie pasuje do frazy wyszukiwania (ID Azure lub tytuł). */
  function taskMatches(task, query) {
    if (!query) return true;
    const q = query.trim().toLowerCase().replace(/^#/, "");
    if (!q) return true;
    return (task.wi || "").toLowerCase().includes(q) || (task.title || "").toLowerCase().includes(q);
  }

  // ---------- Render ----------
  function renderClock() {
    const now = new Date();
    el.clockDate.textContent = fmtDatePl(dateKey(now));
    el.clockTime.textContent = `${pad(now.getHours())}:${pad(now.getMinutes())}:${pad(now.getSeconds())}`;
  }

  function setBar(bar, value, limit) {
    const pct = limit > 0 ? Math.min(100, (value / limit) * 100) : 0;
    bar.style.width = `${pct}%`;
    bar.className = value > limit ? "over" : pct >= 90 ? "warn" : "";
  }

  function renderWorkday() {
    const day = todayDay();
    const now = Date.now();
    const t = dayTotals(day, now);
    const working = isWorking(day);
    const started = !!day?.startedAt;

    el.wdTimer.textContent = fmtHMS(t.total);
    el.wdStatus.className = "workday-status " + (working ? "working" : started ? "paused" : "");
    el.wdStatus.textContent = working ? "W toku" : started ? "Zatrzymano" : "Nie rozpoczęto";

    el.wdStart.hidden = started;
    el.wdResume.hidden = !started || working;
    el.wdStop.hidden = !working;

    el.stWork.textContent = fmtHM(t.work);
    el.stWorkLimit.textContent = fmtHM(obligoSec());
    setBar(el.stWorkBar, t.work, obligoSec());

    el.stBreak.textContent = fmtHM(t.brk);
    el.stBreak.classList.toggle("over", t.brk > breakLimitSec());
    el.stBreakLimit.textContent = fmtHM(breakLimitSec());
    el.stOrg.textContent = fmtHM(t.org);
    setBar(el.stBreakBar, t.brk, breakLimitSec());

    // nad licznikiem:
    //  - przed 8:00 obecności:                      "z 8:00 · do końca 3:30"
    //  - po 8:00 obecności, gdy brakuje pracy:      "Odpracowujesz 19 h 45 min przerwy · pozostało 5 h 57 min pracy"
    //  - po zaliczeniu dnia (obecność i obligo):    "od 20:20 nadgodziny (3 h 30 min)"
    const presenceDone = t.total >= workLimitSec();
    el.wdTotal.classList.remove("over", "owed");
    if (day && t.doneAt !== null) {
      el.wdTotal.innerHTML = `od <span>${fmtClock(t.doneAt)}</span> nadgodziny <span>(${fmtHMin(t.overtime)})</span>`;
      el.wdTotal.classList.add("over");
    } else if (day && presenceDone) {
      el.wdTotal.innerHTML = `Odpracowujesz · pozostało <span>${fmtHMin(t.owed)}</span>`;
      el.wdTotal.classList.add("owed");
    } else {
      el.wdTotal.innerHTML = `z <span>${fmtHM(workLimitSec())}</span> · do końca <span>${fmtHM(workLimitSec() - t.total)}</span>`;
    }

    el.stOvertime.textContent = fmtHM(t.overtime);
    el.stOvertimeBox.classList.toggle("active", t.overtime > 0);
    el.stOvertimeSub.textContent = t.doneAt !== null ? `od ${fmtClock(t.doneAt)}` : "brak";
    el.stOwed.textContent = fmtHM(t.owed);
    el.stOwedBox.classList.toggle("active", t.owed > 0);
    el.stOwedSub.textContent = t.owed > 0 ? (presenceDone ? `do obliga ${fmtHM(obligoSec())}` : `przerwa ponad ${fmtHM(breakLimitSec())}`) : "brak";

    const open = day ? openSegment(day) : null;
    const openTask = open?.kind === "task" ? findTask(day, open.taskId) : null;
    document.title = openTask
      ? `▶ ${fmtHMS(segDuration(open, now))} ${taskTitle(openTask)} – LiczMiCzas`
      : working
        ? `☕ ${fmtHMS(t.total)} – LiczMiCzas`
        : "LiczMiCzas – Timer zadań Azure Boards";
  }

  function renderSuggestions() {
    const tasks = knownTasks();
    el.wiSuggestions.innerHTML = tasks
      .filter((t) => t.wi)
      .map((t) => `<option value="${escapeHtml(t.wi)}">${escapeHtml(taskTitle(t))}</option>`)
      .join("");
    el.titleSuggestions.innerHTML = tasks
      .filter((t) => t.title)
      .map((t) => `<option value="${escapeHtml(t.title)}">${t.wi ? "#" + escapeHtml(t.wi) : ""}</option>`)
      .join("");
  }

  function renderTimeline() {
    const day = todayDay();
    const now = Date.now();
    el.listDate.textContent = fmtDatePl(today());
    const allSegments = day ? day.segments : [];
    const searching = searchQuery.trim().replace(/^#/, "").length > 0;
    // przy wyszukiwaniu pokazujemy tylko wpisy zadań pasujące do frazy (bez przerw i zdarzeń)
    const segments = searching
      ? allSegments.filter((s) => s.kind === "task" && taskMatches(findTask(day, s.taskId) || {}, searchQuery))
      : allSegments;
    const events = searching ? [] : day?.events || [];
    el.emptyInfo.hidden = searching || allSegments.length > 0 || events.length > 0;
    el.searchInfo.hidden = !searching;
    if (searching) {
      const taskCount = new Set(segments.map((s) => s.taskId)).size;
      el.searchInfo.textContent = segments.length
        ? `${segments.length} ${segments.length === 1 ? "wpis" : segments.length < 5 ? "wpisy" : "wpisów"} · ${taskCount} ${taskCount === 1 ? "zadanie" : taskCount < 5 ? "zadania" : "zadań"}`
        : "brak wyników";
    }

    // ostatni segment każdego zadania – tylko tam pokazujemy „Wznów”
    const lastSegOfTask = new Map();
    for (const s of segments) if (s.kind === "task") lastSegOfTask.set(s.taskId, s.id);

    // wpisy i zdarzenia (start / stop / wznowienie) w jednej osi czasu, najnowsze na górze
    const items = [
      ...segments.map((s) => ({ at: s.start, order: 1, html: entryHtml(day, s, now, lastSegOfTask.get(s.taskId) === s.id) })),
      ...events.map((ev) => ({ at: ev.at, order: ev.type === "stop" ? 2 : 0, html: eventHtml(ev) })),
    ].sort((a, b) => b.at - a.at || b.order - a.order);

    el.timeline.innerHTML = items.map((i) => i.html).join("");
  }

  const EVENT_LABEL = { start: "▶ Rozpoczęto pracę", stop: "■ Zakończono pracę", resume: "▶ Wznowiono pracę" };
  function eventHtml(ev) {
    return `<li class="event ${ev.type}"><span>${EVENT_LABEL[ev.type] || ev.type}</span><b>${fmtClock(ev.at)}</b></li>`;
  }

  function entryHtml(day, s, now, isLastOfTask) {
    const running = s.end === null;
    const task = s.kind === "task" ? findTask(day, s.taskId) : null;
    const type = task ? task.type : "break";
    const dur = segDuration(s, now);
    const range = `${fmtClock(s.start)} – ${running ? "…" : fmtClock(s.end)}`;

    let head, sub;
    let overEst = false;
    if (task) {
      const url = workItemUrl(task.wi);
      const wiHtml = task.wi
        ? url
          ? `<a class="entry-wi" href="${escapeHtml(url)}" target="_blank" rel="noopener">#${escapeHtml(task.wi)}</a>`
          : `<span class="entry-wi">#${escapeHtml(task.wi)}</span>`
        : `<span class="badge noid" title="Zadanie bez ID w Azure Boards">Brak ID Azure</span>`;
      const title = taskTitle(task);
      head = `${wiHtml}<span class="entry-title" title="${escapeHtml(title)}">${escapeHtml(title)}</span>
              <span class="badge ${type}">${type === "org" ? "organizacyjne" : "praca"}</span>`;
      const taskTotal = taskSecondsInDay(day, task.id, now);
      const allDays = taskSecondsAllDays(day, task, now);
      overEst = !!task.estimateH && allDays > task.estimateH * 3600;
      sub = `zadanie dziś: ${fmtHM(taskTotal)}` +
        (allDays > taskTotal + 1 ? ` · łącznie: ${fmtHM(allDays)}` : "") +
        (task.estimateH ? ` · estymata: ${fmtHM(task.estimateH * 3600)}` : "");
    } else {
      head = `<span class="entry-title">Przerwa</span><span class="badge break">przerwa</span>`;
      sub = running ? "czas bez zadania – liczony jako przerwa" : "";
    }

    // kolejność: ikony funkcyjne po lewej, akcja główna (Zatrzymaj / Wznów) skrajnie po prawej
    const actions = [`<button class="btn small" data-act="edit" title="Edytuj">✎</button>`];
    if (!running) actions.push(`<button class="btn small danger" data-act="delete" title="Usuń wpis">🗑</button>`);
    if (task) {
      if (running) actions.push(`<button class="btn small danger" data-act="stop-task">■ Zatrzymaj</button>`);
      else if (isLastOfTask)
        actions.push(`<button class="btn small success" data-act="resume-task" title="Dodaje nowy wpis tego zadania i liczy dalej">▶ Wznów</button>`);
    }

    return `
      <li class="entry ${type} ${running ? "running" : ""} ${overEst ? "over-est" : ""}" data-id="${s.id}" ${overEst ? 'title="Przekroczona estymata"' : ""}>
        <div class="entry-time">
          <b data-range>${range}</b>
          <span data-dur>${fmtHMS(dur)}</span>
        </div>
        <div class="entry-main">
          <div class="entry-head">${head}</div>
          ${sub ? `<div class="entry-sub" data-sub>${sub}</div>` : ""}
          <input class="entry-comment" data-comment placeholder="Komentarz…" value="${escapeHtml(s.comment)}" />
        </div>
        <div class="entry-actions">${actions.join("")}</div>
      </li>`;
  }

  /** Aktualizacja liczników bez przebudowy listy (nie psuje wpisywanego komentarza). */
  function updateRunningEntry() {
    const day = todayDay();
    const open = day ? openSegment(day) : null;
    if (!open) return;
    const li = el.timeline.querySelector(`li[data-id="${open.id}"]`);
    if (!li) return;
    const now = Date.now();
    li.querySelector("[data-dur]").textContent = fmtHMS(segDuration(open, now));
    if (open.kind === "task") {
      const task = findTask(day, open.taskId);
      const sub = li.querySelector("[data-sub]");
      if (sub && task) {
        const taskTotal = taskSecondsInDay(day, task.id, now);
        const allDays = taskSecondsAllDays(day, task, now);
        li.classList.toggle("over-est", !!task.estimateH && allDays > task.estimateH * 3600);
        sub.textContent =
          `zadanie dziś: ${fmtHM(taskTotal)}` +
          (allDays > taskTotal + 1 ? ` · łącznie: ${fmtHM(allDays)}` : "") +
          (task.estimateH ? ` · estymata: ${fmtHM(task.estimateH * 3600)}` : "");
      }
    }
  }

  // ---------- Podsumowanie ----------
  const EV_LABEL = { start: "rozpoczęto", stop: "zakończono", resume: "wznowiono" };

  /** Model podsumowania dnia – wspólny dla widoku HTML i wersji tekstowej. */
  function summaryModel(key) {
    const day = getDay(key);
    const now = Date.now();
    const mode = el.summaryMode.value;
    const showId = el.summaryShowId.checked;
    const showRanges = el.summaryShowRanges.checked;
    const m = { key, date: fmtDatePl(key), empty: true, showId, showRanges, mode };
    if (!day || day.segments.length === 0) return m;

    const final = key < today(); // dzień z przeszłości – rozliczony na koniec
    const t = dayTotals(day, now, final);
    const running = day.segments.some((s) => s.end === null);
    m.empty = false;
    m.totals = t;
    m.limits = { work: workLimitSec(), obligo: obligoSec(), brk: breakLimitSec() };
    m.start = fmtClock(day.startedAt ?? day.segments[0].start);
    m.end = running ? "…" : fmtClock(Math.max(...day.segments.map((s) => s.end)));
    m.running = running;
    m.doneAt = t.doneAt !== null ? fmtClock(t.doneAt) : null;
    m.events = (day.events || [])
      .slice()
      .sort((a, b) => a.at - b.at)
      .map((ev) => ({ type: ev.type, label: EV_LABEL[ev.type] || ev.type, at: fmtClock(ev.at) }));

    const rangeStr = (s) => `${fmtClock(s.start)}–${s.end === null ? "…" : fmtClock(s.end)}`;
    const toRow = (task, i) => {
      const daySec = taskSecondsInDay(day, task.id, now);
      const totalSec = taskSecondsAllDays(day, task, now);
      const segs = day.segments.filter((s) => s.taskId === task.id);
      const estSec = task.estimateH ? task.estimateH * 3600 : 0;
      let timeStr;
      if (mode === "total") timeStr = fmtHours(totalSec);
      else if (mode === "both") timeStr = `${fmtHours(daySec)} (łącznie ${fmtHours(totalSec)})`;
      else timeStr = fmtHours(daySec);
      return {
        no: i + 1,
        id: task.wi || "",
        idLabel: taskIdLabel(task),
        url: workItemUrl(task.wi),
        title: (task.title || "").trim(),
        displayTitle: taskTitle(task),
        type: task.type,
        daySec,
        totalSec,
        estSec,
        timeStr,
        estStr: estSec ? fmtHours(estSec) : "",
        // pasek i podświetlenie: zawsze czas łączny (wszystkie dni) względem estymaty
        overEst: estSec ? totalSec > estSec : false,
        running: segs.some((s) => s.end === null),
        ranges: segs.map(rangeStr),
        comments: [...new Set(segs.map((s) => (s.comment || "").trim()).filter(Boolean))],
      };
    };

    const withTime = day.tasks.filter((task) => taskSecondsInDay(day, task.id, now) > 0);
    m.work = withTime.filter((task) => task.type !== "org").map(toRow);
    m.org = withTime.filter((task) => task.type === "org").map(toRow);

    const breaks = day.segments.filter((s) => s.kind === "break" && segDuration(s, now) >= 60);
    m.breaks = {
      sec: t.idle,
      ranges: breaks.map(rangeStr),
      comments: [...new Set(breaks.map((s) => (s.comment || "").trim()).filter(Boolean))],
    };
    return m;
  }

  /** Wersja tekstowa (do skopiowania). */
  function summaryText(m) {
    const lines = [`Dzień: ${m.date}`];
    if (m.empty) {
      lines.push("(brak zarejestrowanego czasu)");
      return lines.join("\n");
    }
    const t = m.totals;
    lines.push(
      `Praca: ${m.start} – ${m.end} · łącznie ${fmtHours(t.total)} / ${fmtHours(m.limits.work)}` +
        (t.overtime > 0 ? ` · nadgodziny ${fmtHours(t.overtime)}` : ""),
    );
    lines.push(
      `Zadania: ${fmtHours(t.work)} / ${fmtHours(m.limits.obligo)} · przerwa: ${fmtHours(t.brk)} / ${fmtHours(m.limits.brk)}` +
        (t.org > 0 ? ` (w tym organizacyjne ${fmtHours(t.org)})` : ""),
    );
    if (t.overtime > 0) lines.push(`Nadgodziny: ${fmtHours(t.overtime)} (praca od ${m.doneAt})`);
    if (t.owed > 0) {
      lines.push(
        `Do odpracowania: ${fmtHours(t.owed)}` +
          (t.breakOver > 0 ? ` (przerwa ponad limit ${fmtHours(t.breakOver)})` : ""),
      );
    }
    if (m.events.length) lines.push(`Zdarzenia: ${m.events.map((ev) => `${ev.label} ${ev.at}`).join(", ")}`);

    const rowLine = (r) => {
      const idStr = m.showId ? `${r.idLabel} ` : "";
      const titleStr = r.title || (m.showId ? "" : r.displayTitle);
      const estStr = r.estStr ? ` / ${r.estStr}` : "";
      const rangesStr = m.showRanges && r.ranges.length ? ` (${r.ranges.join(", ")})` : "";
      const commentStr = r.comments.length ? ` – ${r.comments.join("; ")}` : "";
      return `${r.no} - ${(idStr + titleStr).trim()} [${r.timeStr}${estStr}]${rangesStr}${commentStr}`;
    };

    lines.push("", "Lista zadań:");
    if (m.work.length === 0) lines.push("(brak)");
    m.work.forEach((r) => lines.push(rowLine(r)));
    if (m.org.length) {
      lines.push("", "Organizacyjne:");
      m.org.forEach((r) => lines.push(rowLine(r)));
    }
    if (m.breaks.sec > 0) {
      const rangesStr = m.showRanges && m.breaks.ranges.length ? ` (${m.breaks.ranges.join(", ")})` : "";
      const commentStr = m.breaks.comments.length ? ` – ${m.breaks.comments.join("; ")}` : "";
      lines.push("", `Przerwy: ${fmtHours(m.breaks.sec)}${rangesStr}${commentStr}`);
    }
    return lines.join("\n");
  }

  /** Widok HTML podsumowania. */
  function summaryHtml(m) {
    if (m.empty) {
      return `<div class="sum-head"><span class="sum-date">${escapeHtml(m.date)}</span></div>
              <p class="muted">Brak zarejestrowanego czasu w tym dniu.</p>`;
    }
    const t = m.totals;
    // uproszczona lista: etykieta po lewej, wartość po prawej
    const chip = (label, value, cls = "") =>
      `<li class="sum-stat ${cls}"><span>${label}</span><b>${value}</b></li>`;

    const chips = [
      chip("Łącznie", `${fmtHM(t.total)} <small>/ ${fmtHM(m.limits.work)}</small>`, t.total > m.limits.work ? "over" : ""),
      chip("Zadania", `${fmtHM(t.work)} <small>/ ${fmtHM(m.limits.obligo)}</small>`, t.shortWork > 0 ? "over" : ""),
      chip("Przerwa", `${fmtHM(t.brk)} <small>/ ${fmtHM(m.limits.brk)}</small>`, t.brk > m.limits.brk ? "over" : ""),
    ];
    if (t.overtime > 0) chips.push(chip("Nadgodziny", `${fmtHM(t.overtime)} <small>od ${m.doneAt}</small>`, "over"));
    if (t.owed > 0) chips.push(chip("Do odpracowania", fmtHM(t.owed), "warn"));

    const rowHtml = (r) => {
      const idHtml = !m.showId
        ? ""
        : r.id
          ? r.url
            ? `<a class="sum-id" href="${escapeHtml(r.url)}" target="_blank" rel="noopener">#${escapeHtml(r.id)}</a>`
            : `<span class="sum-id">#${escapeHtml(r.id)}</span>`
          : `<span class="sum-id noid">Brak ID</span>`;
      const title = r.title || (m.showId && r.id ? "(bez tytułu)" : r.displayTitle);
      // zamiast paska: ta sama linia co na karcie osi czasu
      const info =
        `<div class="sum-info">zadanie dziś: ${fmtHM(r.daySec)}` +
        (r.totalSec > r.daySec + 1 ? ` · łącznie: ${fmtHM(r.totalSec)}` : "") +
        (r.estSec ? ` · estymata: ${fmtHM(r.estSec)}` : "") +
        `</div>`;
      const meta = [];
      if (m.showRanges && r.ranges.length) meta.push(`<span class="sum-ranges">${escapeHtml(r.ranges.join(", "))}</span>`);
      if (r.comments.length) meta.push(`<span class="sum-comment">${escapeHtml(r.comments.join("; "))}</span>`);
      return `
        <li class="sum-row ${r.type} ${r.running ? "running" : ""} ${r.overEst ? "over-est" : ""}" ${r.overEst ? 'title="Przekroczona estymata"' : ""}>
          <span class="sum-no">${r.no}</span>
          <div class="sum-main">
            <div class="sum-title-line">${idHtml}<span class="sum-title">${escapeHtml(title)}</span></div>
            ${info}
            ${meta.length ? `<div class="sum-meta">${meta.join("")}</div>` : ""}
          </div>
          <div class="sum-time"><b>${fmtHM(m.mode === "total" ? r.totalSec : r.daySec)}</b>${m.mode === "both" ? `<small>łącznie ${fmtHM(r.totalSec)}</small>` : ""}${r.estSec ? `<small>/ ${fmtHM(r.estSec)}</small>` : ""}</div>
        </li>`;
    };

    const section = (title, rows, emptyText) => `
      <h4 class="sum-section">${title} <span class="muted">(${rows.length})</span></h4>
      ${rows.length ? `<ol class="sum-list">${rows.map(rowHtml).join("")}</ol>` : `<p class="muted small">${emptyText}</p>`}`;

    const breaksHtml =
      m.breaks.sec > 0
        ? `<div class="sum-breaks"><span>Przerwy</span><b>${fmtHM(m.breaks.sec)}</b>` +
          (m.showRanges && m.breaks.ranges.length ? `<span class="sum-ranges">${escapeHtml(m.breaks.ranges.join(", "))}</span>` : "") +
          (m.breaks.comments.length ? `<span class="sum-comment">${escapeHtml(m.breaks.comments.join("; "))}</span>` : "") +
          `</div>`
        : "";

    // zdarzenia dnia – zwinięte do jednej linii, rozwijane kliknięciem
    let eventsHtml = "";
    if (m.events.length) {
      const stops = m.events.filter((ev) => ev.type === "stop").length;
      const last = m.events[m.events.length - 1];
      const brief =
        `start <b>${m.events[0].at}</b>` +
        (stops ? ` · przerw: <b>${stops}</b>` : "") +
        (last.type !== "start" ? ` · ${last.label} <b>${last.at}</b>` : "");
      eventsHtml = `
        <details class="sum-events-box" ${eventsOpen ? "open" : ""}>
          <summary><span class="sum-events-title">Zdarzenia dnia (${m.events.length})</span><span class="sum-events-brief">${brief}</span></summary>
          <div class="sum-events">${m.events
            .map((ev) => `<span class="sum-event ${ev.type}">${ev.label} <b>${ev.at}</b></span>`)
            .join("")}</div>
        </details>`;
    }

    return `
      <div class="sum-head">
        <span class="sum-date">${escapeHtml(m.date)}</span>
        <span class="sum-hours ${m.running ? "running" : ""}">${m.start} – ${m.end}</span>
      </div>
      <ul class="sum-stats">${chips.join("")}</ul>
      ${eventsHtml}
      ${section("Lista zadań", m.work, "Brak zadań typu praca.")}
      ${m.org.length ? section("Organizacyjne", m.org, "") : ""}
      ${breaksHtml}`;
  }

  let lastSummaryHtml = "";
  function renderSummary() {
    el.summaryDate.value = selectedDate;
    const m = summaryModel(selectedDate);
    const html = summaryHtml(m);
    // przebudowa DOM tylko gdy treść się zmieniła – nie gubi stanu rozwinięcia ani zaznaczenia tekstu
    if (html !== lastSummaryHtml) {
      el.summaryView.innerHTML = html;
      lastSummaryHtml = html;
    }
    el.summaryText.textContent = summaryText(m);
  }

  function renderSettings() {
    el.sWork.value = settings().workHours;
    el.sBreak.value = settings().breakHours;
    el.sOrg.value = settings().orgUrl || "";
  }

  function render() {
    renderClock();
    renderWorkday();
    renderTimeline();
    renderSuggestions();
    renderSummary();
  }

  function tick() {
    renderClock();
    if (isWorking(todayDay())) {
      renderWorkday();
      updateRunningEntry();
      renderSummary();
    }
  }

  // ---------- Zdarzenia: dzień pracy ----------
  el.wdStart.addEventListener("click", startWork);
  el.wdResume.addEventListener("click", startWork);
  el.wdStop.addEventListener("click", stopWork);

  // ---------- Zdarzenia: nowe zadanie ----------
  el.form.addEventListener("submit", (e) => {
    e.preventDefault();
    const title = el.fTitle.value.trim();
    const wi = el.fWi.value.trim();
    if (!title && !wi) {
      el.fTitle.setCustomValidity("Podaj ID Azure lub tytuł zadania.");
      el.fTitle.reportValidity();
      return;
    }
    addTaskAndStart({
      wi,
      title,
      type: el.fType.value === "org" ? "org" : "praca",
      estimateH: parseEstimate(el.fEst.value),
    });
    el.form.reset();
    el.fWi.focus();
  });

  // autouzupełnianie z wcześniejszych zadań
  el.fTitle.addEventListener("input", () => el.fTitle.setCustomValidity(""));
  el.fWi.addEventListener("input", () => el.fTitle.setCustomValidity(""));
  el.fWi.addEventListener("change", () => {
    const wi = el.fWi.value.trim();
    const known = knownTasks().find((t) => t.wi === wi);
    if (known && !el.fTitle.value.trim()) fillForm(known);
  });
  el.fTitle.addEventListener("change", () => {
    const title = el.fTitle.value.trim().toLowerCase();
    const known = title && knownTasks().find((t) => (t.title || "").trim().toLowerCase() === title);
    if (known && !el.fWi.value.trim()) fillForm(known);
  });
  function fillForm(task) {
    el.fWi.value = task.wi || "";
    el.fTitle.value = task.title || "";
    el.fType.value = task.type;
    el.fEst.value = estimateToInput(task.estimateH);
  }

  // ---------- Zdarzenia: oś czasu ----------
  el.timeline.addEventListener("click", (e) => {
    const btn = e.target.closest("button[data-act]");
    if (!btn) return;
    const day = todayDay();
    const li = btn.closest("li[data-id]");
    const seg = day?.segments.find((s) => s.id === li.dataset.id);
    if (!seg) return;

    switch (btn.dataset.act) {
      case "stop-task":
        stopTask();
        return;
      case "resume-task":
        startTask(seg.taskId);
        return;
      case "edit":
        openEdit(today(), seg.id);
        return;
      case "delete": {
        const task = seg.kind === "task" ? findTask(day, seg.taskId) : null;
        const name = task ? `zadania "${taskTitle(task)}"` : "przerwy";
        if (!confirm(`Usunąć wpis ${name} (${fmtClock(seg.start)} – ${seg.end ? fmtClock(seg.end) : "…"})?`)) return;
        day.segments = day.segments.filter((s) => s.id !== seg.id);
        if (task && !day.segments.some((s) => s.taskId === task.id)) {
          day.tasks = day.tasks.filter((t) => t.id !== task.id);
        }
        commit();
        return;
      }
    }
  });

  el.timeline.addEventListener("input", (e) => {
    const input = e.target.closest("input[data-comment]");
    if (!input) return;
    const day = todayDay();
    const seg = day?.segments.find((s) => s.id === input.closest("li").dataset.id);
    if (!seg) return;
    seg.comment = input.value;
    save();
    renderSummary();
  });

  // ---------- Wyszukiwarka zadań ----------
  el.search.addEventListener("input", () => {
    searchQuery = el.search.value;
    el.searchClear.hidden = !searchQuery;
    renderTimeline();
  });
  el.search.addEventListener("keydown", (e) => {
    if (e.key === "Escape") {
      el.search.value = "";
      el.search.dispatchEvent(new Event("input"));
    }
  });
  el.searchClear.addEventListener("click", () => {
    el.search.value = "";
    el.search.dispatchEvent(new Event("input"));
    el.search.focus();
  });

  // ---------- Edycja wpisu ----------
  function openEdit(dayKey, segId) {
    const day = getDay(dayKey);
    const seg = day?.segments.find((s) => s.id === segId);
    if (!seg) return;
    editing = { dayKey, segId };
    const task = seg.kind === "task" ? findTask(day, seg.taskId) : null;

    el.eHeading.textContent = task ? "Edytuj wpis zadania" : "Edytuj przerwę";
    el.eTaskFields.hidden = !task;
    if (task) {
      el.eWi.value = task.wi || "";
      el.eTitle.value = task.title || "";
      el.eType.value = task.type;
      el.eEst.value = estimateToInput(task.estimateH);
    }
    el.eStart.value = fmtClockInput(seg.start);
    el.eEnd.value = seg.end === null ? "" : fmtClockInput(seg.end);
    el.eEndHint.textContent = seg.end === null ? "Wpis trwa – zostaw koniec pusty, aby dalej liczyć." : "";
    el.eComment.value = seg.comment || "";
    el.dialog.showModal();
  }

  el.eCancel.addEventListener("click", () => el.dialog.close());

  el.editForm.addEventListener("submit", (e) => {
    if (!editing) return;
    const day = getDay(editing.dayKey);
    const seg = day?.segments.find((s) => s.id === editing.segId);
    if (!seg) return;

    const start = timeOnDay(day.date, el.eStart.value);
    const end = el.eEnd.value ? timeOnDay(day.date, el.eEnd.value) : null;
    if (end !== null && end < start) {
      e.preventDefault();
      alert("Koniec nie może być wcześniejszy niż początek.");
      return;
    }
    const wasOpen = seg.end === null;
    seg.start = start;
    seg.end = end;
    seg.comment = el.eComment.value.trim();

    if (seg.kind === "task") {
      const task = findTask(day, seg.taskId);
      if (task) {
        const wi = el.eWi.value.trim();
        const title = el.eTitle.value.trim();
        if (!wi && !title) {
          e.preventDefault();
          alert("Podaj ID Azure lub tytuł zadania.");
          return;
        }
        task.wi = wi;
        task.title = title;
        task.type = el.eType.value === "org" ? "org" : "praca";
        task.estimateH = parseEstimate(el.eEst.value);
      }
    }

    // Zamknięto otwarty wpis ręcznie → dzień dalej trwa jako przerwa.
    if (wasOpen && end !== null && isWorking(day) && day.date === today()) {
      pushSegment(day, { kind: "break", start: Math.max(end, Date.now() - 1) });
    }
    if (day.startedAt && start < day.startedAt) day.startedAt = start;
    editing = null;
    commit();
  });

  // ---------- Podsumowanie ----------
  el.summaryDate.addEventListener("change", () => {
    if (el.summaryDate.value) selectedDate = el.summaryDate.value;
    renderSummary();
  });
  el.dayPrev.addEventListener("click", () => { selectedDate = shiftDate(selectedDate, -1); renderSummary(); });
  el.dayNext.addEventListener("click", () => { selectedDate = shiftDate(selectedDate, 1); renderSummary(); });
  el.dayToday.addEventListener("click", () => { selectedDate = today(); renderSummary(); });
  el.summaryView.addEventListener("toggle", (e) => {
    if (e.target.classList?.contains("sum-events-box")) eventsOpen = e.target.open;
  }, true);
  el.optionsToggle.addEventListener("click", () => {
    const open = el.options.hidden;
    el.options.hidden = !open;
    el.optionsToggle.classList.toggle("active", open);
    el.optionsToggle.setAttribute("aria-expanded", String(open));
  });
  el.refreshBtn.addEventListener("click", () => {
    renderSummary();
    el.refreshBtn.classList.add("spin");
    setTimeout(() => el.refreshBtn.classList.remove("spin"), 500);
  });
  el.summaryMode.addEventListener("change", renderSummary);
  el.summaryShowId.addEventListener("change", renderSummary);
  el.summaryShowRanges.addEventListener("change", renderSummary);

  // ---------- Ustawienia ----------
  el.sWork.addEventListener("change", () => {
    const v = parseFloat(el.sWork.value);
    if (Number.isFinite(v) && v > 0) settings().workHours = v;
    renderSettings();
    commit();
  });
  el.sBreak.addEventListener("change", () => {
    const v = parseFloat(el.sBreak.value);
    if (Number.isFinite(v) && v >= 0) settings().breakHours = v;
    renderSettings();
    commit();
  });
  el.sOrg.addEventListener("change", () => {
    settings().orgUrl = el.sOrg.value.trim();
    commit();
  });

  /** Pobranie pliku wygenerowanego w przeglądarce (bez bibliotek). */
  function downloadFile(name, content, type) {
    const blob = new Blob([content], { type });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = name;
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  }

  el.exportBtn.addEventListener("click", () => {
    save();
    downloadFile(`liczmiczas-${today()}.json`, JSON.stringify(state, null, 2), "application/json");
  });

  // ---------- Eksport CSV ----------
  // Separator ";" i BOM UTF-8 – tak otwiera się poprawnie w polskim Excelu.
  const CSV_SEP = ";";
  function csvCell(v) {
    const str = String(v ?? "");
    return /[";\n\r]/.test(str) ? `"${str.replace(/"/g, '""')}"` : str;
  }
  function csvFile(name, rows) {
    const text = "\uFEFF" + rows.map((r) => r.map(csvCell).join(CSV_SEP)).join("\r\n") + "\r\n";
    downloadFile(name, text, "text/csv;charset=utf-8");
  }
  const fmtHoursDec = (sec) => (Math.round((sec / 3600) * 100) / 100).toString().replace(".", ",");
  const sortedDays = () => Object.values(state.days).sort((a, b) => a.date.localeCompare(b.date));

  /** CSV: każdy wpis (segment zadania lub przerwa) jako osobny wiersz. */
  el.exportCsvEntries.addEventListener("click", () => {
    const now = Date.now();
    const rows = [["Data", "Typ", "ID Azure", "Tytuł", "Start", "Koniec", "Czas [h:mm]", "Czas [h]", "Estymata [h]", "Komentarz"]];
    for (const day of sortedDays()) {
      for (const seg of day.segments.slice().sort((a, b) => a.start - b.start)) {
        const task = seg.kind === "task" ? findTask(day, seg.taskId) : null;
        const sec = segDuration(seg, now);
        rows.push([
          fmtDatePl(day.date),
          task ? (task.type === "org" ? "organizacyjne" : "praca") : "przerwa",
          task?.wi || "",
          task ? task.title || "" : "Przerwa",
          fmtClock(seg.start),
          seg.end === null ? "" : fmtClock(seg.end),
          fmtHM(sec),
          fmtHoursDec(sec),
          task?.estimateH ? fmtHoursDec(task.estimateH * 3600) : "",
          seg.comment || "",
        ]);
      }
    }
    csvFile(`liczmiczas-wpisy-${today()}.csv`, rows);
  });

  /** CSV: zadania zsumowane per dzień + wiersz przerw i sum dnia. */
  el.exportCsvTasks.addEventListener("click", () => {
    const now = Date.now();
    const rows = [["Data", "Typ", "ID Azure", "Tytuł", "Czas [h:mm]", "Czas [h]", "Estymata [h]", "Komentarze"]];
    for (const day of sortedDays()) {
      const t = dayTotals(day, now, day.date < today());
      for (const task of day.tasks) {
        const sec = taskSecondsInDay(day, task.id, now);
        if (sec <= 0) continue;
        const comments = [...new Set(day.segments.filter((s) => s.taskId === task.id).map((s) => (s.comment || "").trim()).filter(Boolean))];
        rows.push([
          fmtDatePl(day.date),
          task.type === "org" ? "organizacyjne" : "praca",
          task.wi || "",
          task.title || "",
          fmtHM(sec),
          fmtHoursDec(sec),
          task.estimateH ? fmtHoursDec(task.estimateH * 3600) : "",
          comments.join("; "),
        ]);
      }
      if (t.idle > 0) rows.push([fmtDatePl(day.date), "przerwa", "", "Przerwy", fmtHM(t.idle), fmtHoursDec(t.idle), "", ""]);
      rows.push([fmtDatePl(day.date), "suma", "", "Łącznie", fmtHM(t.total), fmtHoursDec(t.total), "", ""]);
      if (t.overtime > 0) rows.push([fmtDatePl(day.date), "suma", "", "Nadgodziny", fmtHM(t.overtime), fmtHoursDec(t.overtime), "", ""]);
      if (t.owed > 0) rows.push([fmtDatePl(day.date), "suma", "", "Do odpracowania", fmtHM(t.owed), fmtHoursDec(t.owed), "", ""]);
    }
    csvFile(`liczmiczas-zadania-${today()}.csv`, rows);
  });

  el.importInput.addEventListener("change", async () => {
    const file = el.importInput.files?.[0];
    if (!file) return;
    try {
      const parsed = JSON.parse(await file.text());
      if (!parsed.days || typeof parsed.days !== "object") throw new Error("Brak danych dni w pliku.");
      if (!confirm("Zaimportować dane? Bieżące dane zostaną zastąpione.")) return;
      state = { settings: { ...DEFAULT_SETTINGS, ...(parsed.settings || {}) }, days: parsed.days };
      closeStaleDays();
      renderSettings();
      commit();
    } catch (err) {
      alert("Nie udało się zaimportować pliku: " + err.message);
    } finally {
      el.importInput.value = "";
    }
  });

  el.clearDay.addEventListener("click", () => {
    if (!todayDay()) return;
    if (!confirm(`Usunąć wszystkie wpisy z dnia ${fmtDatePl(today())}?`)) return;
    delete state.days[today()];
    commit();
    settingsDialog.close();
  });

  // Ctrl+Shift+S – zatrzymaj bieżące zadanie
  document.addEventListener("keydown", (e) => {
    if (e.ctrlKey && e.shiftKey && e.key.toLowerCase() === "s") {
      e.preventDefault();
      stopTask();
    }
  });

  // ---------- Dialog ustawień ----------
  const settingsDialog = $("#settings-dialog");
  $("#settings-open").addEventListener("click", () => {
    renderSettings();
    settingsDialog.showModal();
  });
  $("#settings-close").addEventListener("click", () => settingsDialog.close());

  // ---------- Motyw jasny / ciemny ----------
  const themeToggle = $("#theme-toggle");
  themeToggle.addEventListener("click", () => {
    const next = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
    document.documentElement.dataset.theme = next;
    try { localStorage.setItem("liczmiczas.theme", next); } catch { /* prywatny tryb */ }
  });

  // ---------- Start ----------
  closeStaleDays();
  save();
  renderSettings();
  render();
  setInterval(tick, TICK_MS);
})();
