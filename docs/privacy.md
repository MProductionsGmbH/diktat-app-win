# Privacy

Diktat has no backend. It does not collect, send or store any data on servers operated by Diktat or MProductions GmbH. This page is the Windows version of the Mac app's [privacy notes](https://github.com/MProductionsGmbH/diktat-app/blob/main/docs/privacy.md).

## What Stays On Your PC

- **Audio:** recorded and transcribed on your PC with a local model (Parakeet via sherpa-onnx or Whisper via whisper.cpp). The recording of a dictation is kept in memory only and is gone after the dictation.
- **Writing modes with the built-in AI model:** Gemma 4 runs on your PC (see [local-model.md](local-model.md)).
- **Settings, dictionary, prompts:** plain files in `%APPDATA%\Diktat`.
- **History (optional):** on your PC, kept as long as you choose (off, 24 hours, 7 days). The file is encrypted with DPAPI for your Windows account, so it cannot be read on another PC, by another Windows user or from a copy of the file alone. A program running in your Windows account can decrypt it; the encryption is no protection against that.
- **API keys** for an optional model provider: encrypted with DPAPI for your Windows account in `%APPDATA%\Diktat\secrets`, never in plain text. A key saved for "Eigene Adresse" belongs to that server: when you change the address to another server (another host, port or `http`/`https`), Diktat deletes the key, so it is not sent to the new one.
- **Text around the cursor:** when a dictation starts, Diktat reads the focused text field through Windows UI Automation to fit spaces and capitals. Password fields are never read. The text stays on your PC, unless you turn on "KI-Modi sehen den Text am Cursor" (see below).
- **Meetings:** the microphone and, for online meetings, the sound your PC plays are recorded and transcribed on your PC; speaker detection runs there too. The audio is deleted once the evaluation has succeeded. Until then (while a meeting waits for its evaluation, or after one failed) the recording lies in `%TEMP%\Diktat\meetings` as a plain WAV file, and the app shows the meeting as not evaluated; you can evaluate or delete it there. Transcript and summary are stored in `%APPDATA%\Diktat\Meetings`, encrypted like the history. Summaries use only the built-in AI model or a model server on this PC itself (Ollama or LM Studio on `localhost`, `127.0.0.1` or `::1`). They never use a server elsewhere in the network or a model provider on the internet, even if you entered one as your provider.
- **Browser address:** only if you added a website to "Modus je App", Diktat reads the host of the browser tab in front through UI Automation when you press the Diktat shortcut, to pick the mode. It stays on your PC.
- **Logs:** `%LOCALAPPDATA%\Diktat\Logs`, one file per day, kept for a week. They never contain transcripts, clipboard contents, field text or keys.

The sidebar of the main window says "Alles auf diesem PC" only while that is true. When the writing modes send text to a provider, it says "Sprache auf diesem PC" and names the provider.

## When Data Leaves Your PC

- **Model download:** loading a speech model or the built-in AI model downloads it from Hugging Face (huggingface.co) or GitHub. They see the request, as with any download. The models for telling speakers apart are downloaded the same way with the first meeting.
- **Optional model provider:** if you add your own provider and API key, the *recognized text* of the writing modes goes directly from your PC to that provider. Audio never does. Your contract and their privacy terms apply.
- **"Versprecher glätten" (Mittel, Stark):** the recognized text of word-for-word dictation goes through the same model as the writing modes. The built-in AI model and a server on this PC (`localhost`) keep it on your PC; a server on another computer in your network (an address ending in `.local`, or an IP address) receives the text, and so does a provider. "Leicht" never uses a model.
- **Text around the cursor for the writing modes (off by default):** with "KI-Modi sehen den Text am Cursor" on, the text around your cursor goes along to the writing mode, and so to a provider if one is used.
- **Update check:** Diktat asks the GitHub releases of this repository for a new version. GitHub sees the request, as with any download. Turn it off in Einstellungen › Allgemein ("Automatisch nach Updates suchen").

## Clipboard

To paste, Diktat puts the text on the clipboard and restores your previous clipboard afterwards. It marks the text so that the Windows clipboard history (`Win + V`) and the cloud clipboard leave it out; other clipboard managers may still see it while it is there.

## Deleting Everything

Einstellungen › Datenschutz › "Alle Daten löschen" removes:

- `%APPDATA%\Diktat`: settings, history, meetings, the provider and its API keys,
- `%LOCALAPPDATA%\Diktat`: the downloaded speech and AI models, the logs,
- `%TEMP%\Diktat`: meeting recordings and other temporary audio,
- the autostart entry (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `Diktat`).

Diktat then quits. From the moment you confirm, it writes nothing back, so what was deleted stays deleted. A running recording is thrown away. Uninstalling Diktat (Windows Settings › Apps) removes the program files but keeps your data; delete the data first if you want both gone.

## Sensitive Content

Transcription and the built-in AI model are local. If you configure an external model provider, check whether your content may be sent there.
