# Built-in AI model

On the Mac, the AI modes run with Apple Intelligence on the device. Windows has nothing like it on
most PCs, so Diktat for Windows brings its own model: Gemma 4 by Google, run by llama.cpp inside
the app (LLamaSharp, project `src/Diktat.LocalModel`). Nothing else needs to be installed.

## Models

Einstellungen › Modelle › KI-Modell offers two files, Google's own 4-bit QAT GGUF, pinned to one
revision with a SHA-256 checksum (`ModelCatalog.GemmaE2B`, `GemmaE4B`):

| Model | Download | Needs | For |
|---|---|---|---|
| Gemma 4 E2B (recommended) | 3.3 GB | 8 GB memory | fast, also without a graphics card |
| Gemma 4 E4B | 5.2 GB | 16 GB memory | more thorough, e.g. WTF and long meetings |

The download resumes after a broken connection, like the speech models. Without a model the app
works as before (a provider or Ollama for the AI modes).

## What uses it

- KI-Schreibstil, WTF, :-) and the cleanup levels Mittel and Stark (`TextRewriter`).
  "Modell auf dem PC zuerst" (engine `automatic`) asks the built-in model first and the provider
  if it fails; "Immer Modell-Anbieter" asks the provider first and the built-in model if it fails.
  An answer cut off by the token limit counts as failed, so no dictation loses its end.
- Meeting summaries and live notes (`MeetingModelBackend.BuiltIn`, `LiveNotesBackend.BuiltIn`).
  They never go to an internet provider.

## Running

- Weights load on the first request (a few seconds) and are freed after 6 minutes without one.
- Greedy decoding (temperature 0), Gemma 4 chat format with thinking off (`GemmaChatFormat`, the same
  as on the Mac), 16K context: enough for a meeting part of 40,000 characters.
- Processor only for now (llama.cpp picks AVX, AVX2 or AVX-512 by itself; ARM64 PCs are supported).

## Testing

`tests/Diktat.LocalModel.Tests` runs the real model when `DIKTAT_TEST_GGUF` names a GGUF file:

```bash
DIKTAT_TEST_GGUF=/path/gemma-4-E2B_q4_0-it.gguf dotnet test tests/Diktat.LocalModel.Tests --logger "console;verbosity=detailed"
```

It prints the answers of the cleanup level Stark and WTF with timings. Without the variable these
tests do nothing, so CI stays fast. The smoke test (`Diktat.exe --smoke-test`) loads llama.cpp in
the published app to prove the native libraries ship with it.
