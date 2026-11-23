# Test checklist for a real Windows PC

CI builds the app on Windows, runs the unit tests and starts the app with `--smoke-test`, which opens every window and settings page, the recording pill, the tray icon and the keyboard hook. What CI cannot check needs a person, a microphone and a few apps. Logs are in `%LOCALAPPDATA%\Diktat\Logs` (Einstellungen › Allgemein › Logs öffnen).

## Setup

- [ ] `Diktat-Setup.exe` installs without admin rights; SmartScreen "Weitere Informationen › Trotzdem ausführen".
- [ ] Update while Diktat runs: start the old version, run the new `Diktat-Setup.exe`. It ends Diktat itself, replaces the files without "DeleteFile schlug fehl; Code 5" and starts the new version.
- [ ] The setup assistant opens, asks for the microphone, downloads Parakeet (about 670 MB) and the practice step detects a dictation.
- [ ] Step "KI-Modell": "Weiter" stays off until Gemma 4 E2B (3.3 GB) is downloading or a provider is set up; the download finishes after the assistant closes, and WTF then works without internet.
- [ ] Updating from 0.2.0 without an AI model: one tray hint at start, a click opens Einstellungen › Modelle; it does not come again.
- [ ] Autostart: after signing out and in, Diktat is in the tray without a window.

## Dictation

- [ ] Hold `Strg + Win`, speak, let go: the text appears in Notepad, Word, Outlook, Chrome/Edge (Gmail), Teams, Slack, WhatsApp.
- [ ] The Start menu does **not** open after releasing `Strg + Win`, also after a very short tap.
- [ ] `Win + E`, `Strg + Win + D` and other Windows shortcuts still work.
- [ ] `Strg + Win + Alt` (WTF) and `Strg + Win + Umschalt` (:-)) start the right mode, with a model provider or Ollama set up.
- [ ] The clipboard: copy something, dictate, then paste with `Strg + V`: the old content is back. Check the log for "Clipboard changed while waiting" on every paste (would mean the restore never runs).
- [ ] A big Excel range on the clipboard, then dictate: the paste is not delayed noticeably.
- [ ] Click into another window while an AI mode works: the text goes to the clipboard, not into the wrong field.
- [ ] An app running as administrator: the text lands on the clipboard and the pill says so.
- [ ] Music plays: it is muted while recording and comes back right after, also after an error.
- [ ] Escape while recording throws the recording away.
- [ ] Spaces and capitals fit the text around the cursor in Word and in a browser text field; password fields are never read.
- [ ] "Wortgenau statt …" in the tray panel replaces the AI text in Notepad, Word and a browser.

## Shortcuts over time

- [ ] After an hour of use and after waking the PC from sleep, the shortcut still works (Windows can silently drop a slow keyboard hook).

## Recording pill and tray

- [ ] The pill sits above the taskbar, never takes the focus, a click on it stops the recording.
- [ ] With two monitors of different scaling and with the taskbar on the left or top.
- [ ] Tray icon: left click opens the panel above the clock, right click the menu, "Beenden" quits without an error.
- [ ] Light and dark mode follow the Windows setting live.

## Meetings and audio files

- [ ] "Im Raum" with two people: transcript with Person 1 and Person 2 (the speaker package is downloaded once).
- [ ] "Online-Meeting" in Teams or Zoom: you are "Ich", the others come from the system audio; without headphones the echo is filtered out of your part.
- [ ] "Online-Meeting" with "Mein Mikrofon mit aufnehmen" off (a webinar on YouTube): no "Ich", the voices are told apart; the choice is still there after a restart.
- [ ] "Ton testen" on the online card and in Settings, Meetings: the level moves while a video plays, "Das System-Audio kommt an."; it stops by itself after 30 s.
- [ ] Online without microphone while nothing plays: the evaluation fails with the hint about the Windows default output device.
- [ ] During a meeting: unplug the USB microphone. The red banner "Das Mikrofon ist nicht erreichbar" shows on the meeting page, the pill says "Mikrofon prüfen", a notification comes; the recording goes on with the default input (notice "Das Mikrofon hat gewechselt") and the transcript keeps its timing.
- [ ] During a meeting: plug in a headset that becomes the default input, and connect a monitor with speakers. The recording goes on.
- [ ] During a meeting: mute the microphone in Windows (or close the laptop lid with an external monitor). After 5 s "Das Mikrofon liefert keinen Ton".
- [ ] During an online meeting: unplug the headphones the call plays on. "Das System-Audio ist nicht erreichbar" shows only while no output is there.
- [ ] Call detection: start a Teams, Zoom or Meet call in the browser. After about 6 s a notification suggests recording; its click opens the meeting page on "Online-Meeting". Not again for the same app within 15 minutes; never while a meeting records; never when switched off in Settings, Meetings.
- [ ] A one-hour meeting: memory stays low (audio goes to disk), the tracks stay in sync.
- [ ] Summary with Ollama running on this PC.
- [ ] Drop a WhatsApp voice message (`.opus`), an iPhone voice memo (`.m4a`) and an `.mp3`.

## Settings

- [ ] Import the settings export of the Mac app: custom terms and replacements arrive.
- [ ] Change a shortcut, also to a combination with a letter key; toggle mode "Umschalten".
- [ ] Second start of `Diktat.exe` brings the open window to the front.

## New in 0.2.3 (Mac 0.1.13 to 0.1.16)

- [ ] Meetings, "Im Raum" and "Online-Meeting": the right sources are recorded; with a headset the others still arrive from the system audio.
- [ ] Call detection on (Einstellungen › Meetings): a Teams or Zoom call shows a notification that offers to record; off, nothing appears.
- [ ] Problems shown large: pull the microphone out during a meeting; the meeting page, the pill and the tray warn at once, and the recording goes on after plugging in a monitor.
- [ ] Online-Meeting with "Mein Mikrofon mit aufnehmen" off: only the system audio is recorded, the transcript has no "Ich"; with nothing playing, a clear message instead of an empty meeting.
- [ ] "Ton testen" on the online card and in Einstellungen › Meetings: the level moves while a video plays, nothing is recorded, it stops by itself.
- [ ] Problem during a meeting: the tray icon shows the amber warning sign and goes back to the red dot once the microphone works again (the notification itself stays in the Windows notification centre).
- [ ] Dictation in German with "um" ("Wir treffen uns um 10 Uhr"): "um" stays; an English dictation with "um, I think" loses the filler.
- [ ] AI mode with the network unplugged: the word-for-word text is in the clipboard, the notice says so, the history has it.
- [ ] Record a shortcut: Strg+V shows "Belegt, anderes wählen" and keeps listening; switching to another window or waiting 15 s ends the recording.
- [ ] Hold Escape to cancel a dictation, start a new one from the tray while still holding it: the new one keeps running.
- [ ] German Windows with region Schweiz and app language "Automatisch": the interface shows "ss" (e.g. "Schliessen"); with "Deutsch" it shows "ß".
- [ ] Meeting summary: no "**" or "##" on screen or when copied; a long meeting with the built-in model is not cut off.
- [ ] The notification "Meeting ausgewertet" opens that meeting; "Meetings" in the sidebar leads back to the list.
- [ ] Delete a meeting that was not evaluated: its `%TEMP%\Diktat\meetings\<id>-*.wav` files are gone.
- [ ] Whisper Large v3 models in Einstellungen › Modelle: download, select, dictate a minute of Swiss German without losing passages.
- [ ] "Schweizer Schreibweise" on: dictation, AI modes and audio files write "ss" instead of "ß"; off, "ß" is back.
- [ ] Meetings list: rename a meeting and delete one; both hold after a restart.
- [ ] Drag an `.m4a` or `.opus` from Explorer onto the Verlauf page: a dashed frame appears, the drop opens "Audiodatei" and transcribes; a `.txt` shows no frame.
- [ ] Main window sidebar: "Alles auf diesem PC" with the built-in AI model; with OpenAI and "Immer Modell-Anbieter" it says "Sprache auf diesem PC" and names OpenAI.
- [ ] "Eigene Adresse" with a saved key: change only the path, the key stays; change the host or port, the key is gone.
- [ ] "Eigene Adresse" with a saved key, then OpenAI or "Keiner", then "Eigene Adresse" again with the default address: no key is shown or sent, and the key is gone (also back on the first address it has to be entered again).
- [ ] Einstellungen › Datenschutz › "Alle Daten löschen …" (with a model loaded and a meeting running): Diktat quits, `%APPDATA%\Diktat`, `%LOCALAPPDATA%\Diktat`, `%TEMP%\Diktat` and the Run entry are gone, and stay gone after Diktat has ended.

## Foundry Local

- [ ] With Foundry Local installed (`winget install Microsoft.FoundryLocal`) and a model downloaded (`foundry model run phi-4-mini`): Einstellungen › KI-Anbieter › "Auf diesem PC suchen" lists Foundry Local with its address and models; "Verwenden" sets it up, "Testen" answers, a KI mode rewrites, and a meeting gets a summary from it.
- [ ] Without Foundry Local installed, the search still finds Ollama and LM Studio and takes no longer than before.
- [ ] Restart Foundry Local (`foundry service restart`): if the port changed, the AI modes fail with a connection message, and searching again and "Verwenden" fixes it.
