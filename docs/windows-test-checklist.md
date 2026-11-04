# Test checklist for a real Windows PC

CI builds the app on Windows, runs the unit tests and starts the app with `--smoke-test`, which opens every window and settings page, the recording pill, the tray icon and the keyboard hook. What CI cannot check needs a person, a microphone and a few apps. Logs are in `%LOCALAPPDATA%\Diktat\Logs` (Einstellungen › Allgemein › Logs öffnen).

## Setup

- [ ] `Diktat-Setup.exe` installs without admin rights; SmartScreen "Weitere Informationen › Trotzdem ausführen".
- [ ] The setup assistant opens, asks for the microphone, downloads Parakeet (about 670 MB) and the practice step detects a dictation.
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
- [ ] A one-hour meeting: memory stays low (audio goes to disk), the tracks stay in sync.
- [ ] Summary with Ollama running on this PC.
- [ ] Drop a WhatsApp voice message (`.opus`), an iPhone voice memo (`.m4a`) and an `.mp3`.

## Settings

- [ ] Import the settings export of the Mac app: custom terms and replacements arrive.
- [ ] Change a shortcut, also to a combination with a letter key; toggle mode "Umschalten".
- [ ] Second start of `Diktat.exe` brings the open window to the front.
