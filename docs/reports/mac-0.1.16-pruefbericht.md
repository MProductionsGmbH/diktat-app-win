# Prüfbericht Übernahme Mac 0.1.13 bis 0.1.16

Durchlauf 1, geprüft gegen `docs/reports/mac-0.1.16-kriterien.md` auf Stand 680166f, Prüfer Anforderungen und Datenregeln (statt Rechte, die App hat keine Rollen). Ergebnisse unverändert von den Prüfern. Die WPF-App läuft nicht auf macOS und die Windows-VM war für die Projektleitung reserviert: Alle Kriterien sind nur über Code, Unit-Tests und CI geprüft.

**Bestanden (nur Code geprüft).** MTG-01-AK1, MTG-01-AK2, MTG-01-AK3, MTG-02-AK2, MTG-03-AK1, MTG-04-AK1, MTG-05-AK1, MTL-01-AK1, MTL-01-AK2, MTL-02-AK1, MTL-02-AK2, MTL-02-AK3, MTL-03-AK1, DIK-01-AK1 bis AK5, STT-01-AK1, STT-01-AK2, STT-02-AK1, STT-02-AK2, DSU-01-AK1, DSU-01-AK3, DSU-02-AK1

## Nicht bestanden

| Spec-ID | Schritte zum Nachstellen | Erwartet | Tatsächlich | Schweregrad |
|---|---|---|---|---|
| DSU-01-AK2 | «Eigene Adresse» `https://a.example.com/v1` mit Schlüssel, dann Anbieter «OpenAI» oder «Keiner», dann wieder «Eigene Adresse» (Standardadresse localhost:11434), KI-Modus nutzen | Der Schlüssel von A geht an keinen anderen Server | Anfrage an localhost:11434 mit dem Schlüssel von A, ebenso über «Server suchen» › «Übernehmen». Im Test bewiesen. | Wichtig |
| DSU-01-AK2 | Neue Serveradresse eintragen, innerhalb von 0,7 s Schlüssel einfügen und Enter | Schlüssel gilt für die neue Adresse | `OnSaveKey` schreibt die wartende Adressänderung nicht vorher fest, danach löscht der Timer den eben gespeicherten Schlüssel | Wichtig |
| MTG-04-AK1 | Anruf-Erkennung an, Zoom-Mikrofon 6 s offen, Protokoll öffnen | Die Erkennung behält nichts (docs/privacy.md) | `CallMonitor` schreibt `Call detected: <App>` mit Uhrzeit ins Protokoll, eine Woche lang | Wichtig |
| MTG-02-AK1 | Meeting starten, Mikrofon ausstecken | Das Symbol im Infobereich zeigt eine Warnung, die Mitteilung verschwindet, wenn das Problem vorbei ist | Symbol bleibt «Aufnahme», nur Tooltip und Tray-Panel nennen das Problem. Mitteilungen lassen sich nicht zurückziehen. | Wichtig |
| DSU-03-AK1 | README und Testliste lesen | Beide beschreiben die neuen Funktionen | README ohne Quellenwahl, «Ton testen», Anruf-Erkennung, Probleme, Umbenennen/Löschen, Schweizer Schreibweise. Testliste ohne «um», KI-Fehler, Kürzel, Escape, de-CH, Zusammenfassungen. | Wichtig |

**Nicht prüfbar.** Alle IDs an der laufenden App (WPF nur unter Windows, VM reserviert). MTG-02-AK2 und MTG-04-AK1 brauchen echte Geräte, Registry und Audio-Sitzungen. DPAPI.

**Tests.** `dotnet test -v q` je Projekt: Core 202, AI 194, Transcription 82, LocalModel 15, alle grün. CI-Lauf 45158697360 auf 6dde2b5: Windows-Build, 46 Windows-Tests, Smoke-Test und Installer-Test grün. Build 0 Fehler, 0 Warnungen.

**Datenregeln.** «Alle Daten löschen» löscht vollständig und nur Diktats Ordner, auch bei leeren, relativen und verlinkten Pfaden (bewiesen auf macOS). Logs ohne Schlüssel und Diktattexte. Meeting-Aufnahmen beim Löschen und als Waisen entfernt. Neue Netzwerkziele nur Hugging Face mit festem Commit und SHA-256, dokumentiert.

**Auffälligkeiten.** Keine gelöschten oder abgeschwächten Tests, eine angepasste Assertion (Mitteilungstext MTL-03). Neu `SECURITY.md`, nicht in der Spec. Lokaler `main` war veraltet, gegen `origin/main` keine Änderungen an CI, Lockfiles oder Regeldateien. Hinweise ohne Schweregrad: «Alle Daten löschen», während ein Meeting gerade das Mikrofon öffnet, kann eine Tondatei hinterlassen, die erst der nächste Start entfernt. Ist ein Diktat-Ordner eine Junction, entfernt die Löschung beim Ziel das Schreibschutz-Flag und meldet Erfolg, obwohl das Ziel bleibt.

**Für die Projektleitung.** Fünf wichtige Befunde, kein Blocker. Alle fünf werden vor der Freigabe behoben und in einem zweiten Durchlauf geprüft.

# Durchlauf 2

Geprüft auf Stand 4e1509c gegen 680166f, nur DSU-01-AK2, MTG-04-AK1, MTG-02-AK1, DSU-03-AK1 und Regression, Prüfer Anforderungen. Ergebnisse unverändert.

**Bestanden (nur Code geprüft).** MTG-04-AK1, MTG-02-AK1 (die Mitteilung selbst lässt sich unter Windows nicht zurückziehen, Windows-Grenze)

## Nicht bestanden

| Spec-ID | Schritte zum Nachstellen | Erwartet | Tatsächlich | Schweregrad |
|---|---|---|---|---|
| DSU-01-AK2 | Mit 0.2.2 Schlüssel für `https://a.example.com/v1` sichern, dann «OpenAI» oder «Keiner», aktualisieren, dann «Eigene Adresse» mit Standardadresse oder «Server suchen» › «Übernehmen» | Der Schlüssel von A geht an keinen anderen Server | Der Schlüssel ohne Serverbindung wird an die neue Adresse gebunden und gesendet. Mit Wegwerf-Test bewiesen. | Wichtig |
| DSU-01-AK2 | Neue Adresse eintragen, innerhalb von 0,7 s Schlüssel einfügen und sichern | Schlüssel gilt für die neue Adresse | Das Festschreiben der Adresse leert das Schlüsselfeld vorher, gespeichert wird nichts | Wichtig |
| DSU-03-AK1 | Neue Testlisten-Zeile zum Umweg abarbeiten | Liste beschreibt das Verhalten | Liste sagt, der Schlüssel sei auf der ersten Adresse wieder da. Tatsächlich ist er gelöscht (so auch der Test). | Wichtig |

**Tests.** Core 202, AI 198, Transcription 82, LocalModel 15, alle grün. CI-Lauf 45164920312 auf 4e1509c grün, 46 Windows-Tests.

**Auffälligkeiten.** Keine gelöschten oder abgeschwächten Tests. Die Testlisten-Zeile «Dictation fixes: the first word is kept, a missing or very quiet microphone is reported …» ist gestrichen.

**Behoben nach Durchlauf 2.** Ein Schlüssel ohne Serverbindung, der nicht unter «Eigene Adresse» liegt, gilt als Schlüssel eines unbekannten Servers und wird nie gesendet (drei neue Tests, vorher rot). Das Schlüsselfeld wird vor dem Festschreiben der Adresse gelesen. Die Testlisten-Zeile ist richtiggestellt. Die gestrichene Zeile beschrieb Diktat-Korrekturen, die in Mac 0.1.13 bis 0.1.16 nicht vorkommen, und bleibt gestrichen. Diese Korrekturen sind durch Tests belegt, aber nicht in einem dritten unabhängigen Durchlauf geprüft.
