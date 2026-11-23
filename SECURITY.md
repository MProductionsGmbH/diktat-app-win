# Security Policy

Diktat for Windows is free and open source software.

It is provided as-is under the MIT License, without warranty or support guarantees.

## Supported Versions

Only the current `main` branch is considered for security fixes.

## Reporting A Vulnerability

Please do not open a public issue with sensitive security details.

Use GitHub private vulnerability reporting for this repository. If it is not available, open a minimal public issue titled `Security contact request` without technical details.

Do not include API keys, access tokens, private recordings, or confidential transcripts in a report.

Include:

- what you found
- how to reproduce it
- what data or system access could be affected
- your suggested fix, if you have one

## Security Notes

- Speech recognition and the built-in AI model run on the PC. Audio is never sent anywhere.
- If a user adds their own model provider, the recognized text of the writing modes is sent directly to that provider. Its API key is encrypted with DPAPI for the Windows account. A key saved for a custom server is deleted when the address moves to another server (host, port or scheme).
- Speech and AI models are downloaded from Hugging Face and GitHub over HTTPS; files with a known SHA-256 checksum are checked before use.
- Meeting audio lies in `%TEMP%\Diktat` as plain WAV files until the meeting is evaluated.
- Diktat reads the focused text field through UI Automation (and, for "Modus je App" with websites, the host of the browser tab in front). Password fields are never read. It pastes through the clipboard with a simulated `Ctrl + V`.
- A low-level keyboard hook watches for the shortcuts; it does not record or store other keys.
- For online meetings the app records what the PC plays (WASAPI loopback).
- History and meeting transcripts are encrypted with DPAPI for the Windows account. That keeps a copied file unreadable for other users and on other PCs; it does not protect against software running in the same Windows account.
- The app installs per user without admin rights and runs without elevation. It cannot paste into apps running as administrator.
- Releases are not code-signed yet, so SmartScreen warns on first start. Check that the download comes from this repository's releases.
- "Alle Daten löschen" (Einstellungen › Datenschutz) removes `%APPDATA%\Diktat`, `%LOCALAPPDATA%\Diktat`, `%TEMP%\Diktat` and the autostart entry, then quits without writing anything back. See [docs/privacy.md](docs/privacy.md).

Do not use Diktat for confidential or regulated data without your own review.
