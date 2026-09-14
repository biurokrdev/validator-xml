# LiczMiCzas

Timer dnia pracy i zadań z Azure Boards – czysty HTML + CSS + JS, bez zależności i bez backendu.
Otwórz `index.html` w przeglądarce. Dane są zapisywane w `localStorage`.

## Wygląd

Styl na wzór Azure DevOps (Fluent UI). Domyślnie motyw **ciemny** (paleta „Dark” z Azure DevOps),
przycisk słońce/księżyc w prawym górnym rogu przełącza na jasny; wybór jest zapamiętywany w przeglądarce.
Karty na osi czasu mają kolorowy lewy brzeg jak na Azure Boards: żółty = praca (Task),
fioletowy = organizacyjne, szary = przerwa.

## Workflow

1. **Rozpocznij pracę** – licznik dnia startuje od `00:00:00`. Od tej chwili czas bez zadania
   liczy się jako **przerwa** (osobna karteczka na osi czasu z godziną początku i końca).
2. **Zadanie** – wpisz ID Azure lub tytuł (jedno z dwóch wymagane), typ i estymatę jako czas (`8:30` lub `8.5`), kliknij „▶ Zadanie”: wpis trafia na listę
   i od razu liczy czas (zamyka bieżącą przerwę). Jeśli dzień nie był rozpoczęty, startuje sam.
   - typ **praca** – schodzi z obliga pracy (domyślnie 6 h = 8 h − 2 h przerwy),
   - typ **organizacyjne** (daily, refinement…) – schodzi z puli przerwy (2 h).
3. **Zatrzymaj** na karcie zadania – zamyka wpis, czas leci dalej jako przerwa.
4. **Wznów** – dodaje **nową kartę** tego samego zadania (nie dubluje danych zadania), dzięki czemu
   widać, że w ciągu dnia zadania były robione naprzemiennie; podsumowanie sumuje czas per zadanie.
5. **Zakończ pracę** – zatrzymuje wszystko (czas do „Wznów pracę” nie jest liczony wcale).
6. **Wznów pracę** – kontynuuje liczenie dnia (jako przerwa, dopóki nie wystartujesz zadania).

Każde rozpoczęcie, zakończenie i wznowienie pracy jest zapisywane jako znacznik z godziną na osi czasu
i w podsumowaniu (linia „Zdarzenia”). Zadanie bez ID ma etykietę **Brak ID Azure**.

Każda karta (zadanie i przerwa) ma pole **komentarza**; komentarze trafiają do podsumowania.
Ikona ✎ pozwala poprawić godziny wpisu, ID/tytuł/typ/estymatę zadania i komentarz.

## Format czasu

W interfejsie (kafelki, karty, sformatowane podsumowanie) czas jest zawsze w formacie **godziny:minuty**,
np. `1:32`. Wersja tekstowa podsumowania (do kopiowania) używa **godzin dziesiętnych** (`1.54 h`),
bo tak wpisuje się czas w polach Completed / Remaining Work w Azure Boards. `1.54 h` = 1 h 32 min.

## Kafelki

- **Zadania (praca)** – suma zadań typu praca vs obligo (dzień − przerwa).
- **Przerwa + organizacyjne** – przerwy automatyczne + zadania organizacyjne vs limit przerwy.
- **Łącznie** – cały naliczony czas dnia vs 8 h; „do końca” = ile zostało do 8 h.
- **Nadgodziny** – wyłącznie praca wykonana po zaliczeniu dnia. Dzień jest zaliczony, gdy obecność
  osiągnie 8 h **i** praca nad zadaniami osiągnie obligo 6 h. Przerwa po zaliczeniu dnia nie jest nadgodziną.
- **Czas do odpracowania** – przed 8 h obecności: przerwa ponad limit 2 h (o tyle wydłuży się dzień);
  po 8 h obecności: brakująca praca do obliga 6 h. Przerwa nie zmniejsza tego czasu – tylko faktyczna praca.

Linia nad licznikiem: przed limitem „z 8:00 · do końca 3:30”, po 8 h obecności z brakującą pracą
„Odpracowujesz 45 min przerwy · pozostało 30 min pracy”, po zaliczeniu dnia „od 16:45 nadgodziny (1 h 20 min)”.

Limity (8 h / 2 h), adres projektu Azure DevOps (do linków `#ID`), eksport/import JSON, eksport CSV
i czyszczenie dnia są w oknie **Ustawienia** (zębatka w górnym pasku).

## Eksport CSV

Bez bibliotek – plik jest składany w JS i pobierany przez przeglądarkę. Separator `;`, kodowanie UTF-8 z BOM
(otwiera się poprawnie w polskim Excelu), godziny dziesiętne z przecinkiem.

- **CSV – wpisy**: każdy wpis osobno: Data; Typ; ID Azure; Tytuł; Start; Koniec; Czas [h:mm]; Czas [h]; Estymata [h]; Komentarz.
- **CSV – zadania**: zadania zsumowane per dzień plus wiersze „Przerwy”, „Łącznie”, „Nadgodziny”, „Do odpracowania”.

## Podsumowanie dnia

Zadania są łączone między dniami **wyłącznie po ID Azure** (czas „łącznie”, porównanie z estymatą).
Zadanie bez ID jest osobnym zadaniem każdego dnia, nawet gdy tytuł się powtarza.

Nawigacja po dniach, przycisk odświeżania, opcje: czas z dnia / łączny (to samo ID w innych dniach),
ID zadania, godziny wpisów. Podsumowanie ma widok sformatowany (lista sum, lista zadań, komentarze,
zdarzenia dnia) oraz rozwijaną **wersję tekstową** do skopiowania:

```
Dzień: 11.09.2026
Praca: 06:50 – 15:10 · łącznie 8.33 h / 8 h · nadgodziny 0.33 h
Zadania: 6.25 h / 6 h · przerwa: 2.08 h / 2 h (w tym organizacyjne 0.25 h)
Nadgodziny: 0.33 h · czas do odpracowania (przerwa ponad limit): 0.08 h · bilans: +0.25 h
Zdarzenia: rozpoczęto 06:50, zakończono 12:00, wznowiono 12:30

Lista zadań:
1 - #12345 Text text text [2.5 h / 10 h]
2 - Brak ID Import parametryzacji – walidator XML [3.75 h / 4 h] – czekam na dostęp do bazy

Organizacyjne:
1 - #777 Daily [0.25 h]

Przerwy: 1.83 h – kawa
```

Skrót `Ctrl+Shift+S` zatrzymuje bieżące zadanie. Eksport / import danych do JSON w ustawieniach.
