# Übernahme Mac 0.1.13 bis 0.1.16: Kriterien

Bestätigt von der Projektleitung im Gespräch («Ja, 0.1.13 bis 0.1.16»). Massstab ist das Verhalten der Mac-App in den Commits 811ec15, 445459b, c2b90fb und d8bceea von `MProductionsGmbH/diktat-app`, angepasst an Windows («Mac» wird «PC»). Mac-eigenes (Bluetooth-Telefonmodus, Core-Audio-Taps, Time Machine, Signatur, Paket-Pinning, Release-Skript) gehört nicht dazu.

## MTG Meetings, Aufnahme

| ID | Kriterium |
|---|---|
| MTG-01-AK1 | Die Meeting-Seite bietet zwei Quellen, «Im Raum» und «Online-Meeting». Die Online-Karte hat den Schalter «Mein Mikrofon mit aufnehmen». Die Wahl bleibt gespeichert. |
| MTG-01-AK2 | Online ohne eigenes Mikrofon nimmt nur das System-Audio auf und lässt sich auswerten. Im Transkript gibt es dann kein «Ich». Kommt nichts an, erscheint eine klare Meldung. |
| MTG-01-AK3 | Die Oberfläche sagt «System-Audio» statt «Ton des PCs». |
| MTG-02-AK1 | Probleme während der Aufnahme (Mikrofon weg, Mikrofon liefert 5 s nur Stille, System-Audio weg) werden gross angezeigt: Meeting-Seite, schwebende Anzeige, Infobereich und Mitteilung. |
| MTG-02-AK2 | Nach einem Wechsel der Audiogeräte läuft die Mikrofonaufnahme weiter, die Lücke wird Stille. |
| MTG-03-AK1 | «Ton testen» zeigt einen Pegel des System-Audios, ohne etwas aufzunehmen. |
| MTG-04-AK1 | Hat eine Anruf-App das Mikrofon offen, schlägt Diktat per Mitteilung eine Aufnahme vor. Browser zählen nur mit Ton in beide Richtungen. Die Erkennung lässt sich in den Einstellungen abschalten und liest nur Programmnamen. |
| MTG-05-AK1 | Die Einstellungen haben einen Bereich Meetings mit Standardquelle, Anruf-Erkennung und «Ton testen». |

## MTL Meetings, Liste und Ansicht

| ID | Kriterium |
|---|---|
| MTL-01-AK1 | Zusammenfassungen erscheinen ohne rohe Markdown-Zeichen, auch beim Kopieren. |
| MTL-01-AK2 | Eine abgeschnittene Zusammenfassung wird erkannt und neu geschrieben. |
| MTL-02-AK1 | Meetings lassen sich umbenennen und einzeln löschen. Beim Löschen eines unausgewerteten Meetings verschwindet auch dessen Aufnahme. |
| MTL-02-AK2 | Vor dem Verwerfen einer Aufnahme und vor dem Löschen fragt Diktat nach. |
| MTL-02-AK3 | Verwaiste Tondateien werden entfernt. |
| MTL-03-AK1 | «Meetings» führt von einem offenen Meeting zurück zur Liste. Die Mitteilung zu einem fertigen Meeting öffnet dieses Meeting. |

## DIK Diktat

| ID | Kriterium |
|---|---|
| DIK-01-AK1 | «um» bleibt in deutschen Sätzen stehen. Englische Füllwörter werden nur bei klar englischem Text entfernt. |
| DIK-01-AK2 | Ein neues Diktat zerstört das vorige nicht. Späte Meldungen eines ersetzten oder abgebrochenen Diktats werden ignoriert. |
| DIK-01-AK3 | Scheitert der KI-Schritt, liegt der wortgenaue Text in der Zwischenablage und ein Hinweis sagt es. |
| DIK-01-AK4 | Die Aufnahme eines eigenen Kürzels endet nach 15 s oder beim Verlassen des Fensters und lehnt belegte Kürzel (etwa Strg+V) ab. |
| DIK-01-AK5 | Escape gedrückt halten bricht das Diktat nicht mehrfach ab und stört ein neues Diktat nicht. |

## STT Spracherkennung und Schweizer Schreibweise

| ID | Kriterium |
|---|---|
| STT-01-AK1 | Zwei weitere Whisper-Modelle stehen zur Wahl: Large v3 Turbo (1,6 GB) und Large v3 (3 GB), mit geprüfter Grösse und Prüfsumme. |
| STT-01-AK2 | Bei «Automatisch» wird die Sprache einmal pro Aufnahme bestimmt. Lange Aufnahmen werden an Sprechpausen geteilt und verlieren keine Passagen. |
| STT-02-AK1 | Die Einstellung «Schweizer Schreibweise» (ss statt ß) gilt für Diktate, KI-Modi und Meetings. In der Schweiz und in Liechtenstein ist sie von Anfang an ein. |
| STT-02-AK2 | Die Oberfläche hat eine Variante de-CH (ss statt ß), wann sie gilt, entscheidet die Sprache der App wie auf dem Mac. |

## DSU Datenschutz und Oberfläche

| ID | Kriterium |
|---|---|
| DSU-01-AK1 | «Alle Daten löschen» fragt nach, räumt Einstellungen, Verlauf, Meetings, Schlüssel, Modelle, Protokolle, Meeting-Aufnahmen und Autostart vollständig ab und beendet die App, ohne etwas zurückzuschreiben. |
| DSU-01-AK2 | Der gespeicherte Schlüssel eines eigenen Servers wird gelöscht, wenn die Serveradresse wechselt. |
| DSU-01-AK3 | Die Texte sagen klar, was auf diesem PC bleibt und was ein Anbieter bekommt. |
| DSU-02-AK1 | Audiodateien lassen sich ins Hauptfenster ziehen und werden transkribiert. |
| DSU-03-AK1 | README, Datenschutz-Doku und Windows-Testliste beschreiben die neuen Funktionen. |
