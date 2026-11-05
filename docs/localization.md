# Languages

Diktat for Windows speaks German and English, like the Mac app. Settings > Allgemein > Sprache has
the choice of the app language; the dictation language is under Settings > Modelle.

**Sprache der App:** Automatisch (the app follows the Windows display language: English with an
English Windows, German with any other), Deutsch or English. It is stored in `settings.json` as
`app.windowsAppLanguage` and applies after a restart (a button restarts the app).

## Texts of the app

The texts in the code are written in German and are the keys of
`src/Diktat.Core/Localization/en.json`, which holds the English version. Most English texts come
from the Mac app's `Localizable.xcstrings`, with "Mac" changed to "PC".

- C#: `L.T("Deutscher Text")`, with placeholders `L.F("Noch {0} Minuten", n)`. The first argument is
  always one plain string literal.
- XAML: `Text="{l:T 'Deutscher Text'}"` with `xmlns:l="clr-namespace:Diktat.Windows.Views"`.
- Translate where a text is shown, not in static fields.
- A new text needs its English entry in `en.json`. `LocalizationTests` fails for every text without
  one, for XAML texts without `{l:T …}` and for placeholders that differ.
- The smoke test (`Diktat.exe --smoke-test`) opens every window in German and in English and fails
  when a German text with an English version is on screen in English.
- Do not translate text that goes to a language model, word lists, file names, keys or log lines.
- Dates and numbers use `L.Culture` (de-CH or en-US).
