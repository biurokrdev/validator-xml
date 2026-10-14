{
  "tag": "tabela_wiadomosci",
  "model-property-path": "wiadomosci",
  "template-row-count": 4,
  "fields": [
    {
      "search-for": "<%data_czas_wiadomosci%>",
      "replacement-property-expression": "record.data_czas_wiadomosci.Replace(\"Odebrana\", \"\").Replace(\"Wysłana\", \"\").Trim()"
    }
  ],
  "collections": [
    { "tag-name": "tag_zalaczniki", "model-property-path": "nazwy_zalacznikow", "empty-text": "Brak załączników" }
  ],
  "content-controls": [
    { "tag-name": "tag_wyslano",  "visible-property-path": "data_czas_wiadomosci", "visible-when-matches": "^Wysłana",  "unwrap": true },
    { "tag-name": "tag_odebrano", "visible-property-path": "data_czas_wiadomosci", "visible-when-matches": "^Odebrana", "unwrap": true }
  ]
}