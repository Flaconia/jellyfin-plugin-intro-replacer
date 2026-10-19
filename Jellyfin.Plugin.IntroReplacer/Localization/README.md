# Translations

The plugin UI and API messages are localized with flat JSON dictionaries in this
folder. `en.json` is the **source language**; every other file is named after the
Jellyfin locale code (`it.json`, `de.json`, `pt-BR.json`, …).

The files are embedded in the plugin DLL (`Localization/*.json`), so adding a
language does **not** require a code change.

## Adding or updating translations with Weblate

Weblate can manage this folder as a **monolingual JSON file** component:

- File format: **JSON file**
- Base language file: `Jellyfin.Plugin.IntroReplacer/Localization/en.json`
- File mask: `Jellyfin.Plugin.IntroReplacer/Localization/*.json`
- Source language: English

Copy `en.json` to a new locale file and translate the values, keeping the keys
unchanged. Missing keys automatically fall back to English at runtime.

## Rules for translators

- Keep placeholder names exactly as they are: `{path}`, `{count}`, `{mode}`,
  `{parts}`, `{library}`, `{rendered}`, `{total}`, `{number}`, `{name}`,
  `{label}`. They are replaced at runtime.
- Keys ending in `.one` / `.other` are plural forms. `en.json` contains both;
  translate both (some languages need more forms — Weblate can add plural
  variants, and the loader will use `.one`/`.other` if the extra forms are absent).
- Only `rules.help` may contain HTML (`<em>`). Do not add HTML to other values.
- Do not translate technical values stored in configuration (`RandomOne`,
  `SequentialAll`, `Replace`, `Prepend`, `EveryTime`, `OncePerInterval`,
  `OncePerSession`, `Targets`, `Tags`) — only the visible labels around them.

## How the language is selected

- The dashboard page asks `GET /IntroReplacer/Localization?lang=<Jellyfin locale>`;
  the server resolves exact locales first (`pt-BR`), then the language (`pt`), then
  falls back to `en`.
- API error messages use the request's `Accept-Language` header the same way.
