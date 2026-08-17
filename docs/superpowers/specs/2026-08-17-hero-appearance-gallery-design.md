# Hero Appearance Gallery — Design Spec

**Date:** 2026-08-17
**Status:** Approved, ready for implementation planning

## Problem

A streamer running BLT (and their viewers whose heroes are also active in the mod) currently
have no way to see what their adopted hero actually looks like — armor colors, shield, weapon,
mount — beyond glancing at it during a live mission. There's no persistent, shareable view of
hero appearance + stats.

## Goal

A "Hero Appearance Gallery" page, generated on demand by the streamer, showing a rendered
portrait plus key stats for every currently-adopted hero, published somewhere viewers can visit
on their own time (not just when the streamer happens to show it on stream).

## Non-goals

- Real-time / always-live updating gallery (explicitly rejected — this is an on-demand snapshot,
  not a live dashboard).
- Automatic re-render on every equipment change (explicitly rejected — streamer triggers it
  manually, "raz na jakiś czas").
- Per-viewer individual chat-command access to their own render (rejected in favor of one shared
  gallery page listing everyone).
- New hosting infrastructure (tunnels, external servers) — reuses the existing Neocities
  publishing pipeline already used for the documentation page.

## Architecture

### Trigger: new button in BLTConfigure

`BLTConfigureWindow` (`BLTConfigure/_Module`, a WPF window loaded as an in-process Bannerlord
module — same process as the running game, not a standalone app) gets a new button, **"Generate
Hero Gallery"**, alongside the existing Neocities-publishing controls it already has for the
documentation page.

Clicking it while the game is running (any screen — recommended on the campaign map, not
mid-mission) kicks off the full pipeline below. No save/reload required: this runs entirely
within the live game session, using the same `MainThreadSync.RunWaitAsync` main-thread dispatch
already proven working (and hardened against the pitfalls found) by today's documentation
generator debugging session.

### Step 1: Enumerate heroes

`BLTAdoptAHeroCampaignBehavior.GetAllAdoptedHeroes()` — the same method already used elsewhere
in the core fork (e.g. the new mount-restriction code) — returns every currently-adopted `Hero`.

### Step 2: Render each hero's portrait

Reuses the exact tableau-rendering technique already built and debugged today for
`DocumentationGenerator.cs`'s character portraits:

- Per hero, on the main thread (`MainThreadSync.RunWaitAsync`): create a `GauntletLayer`, load
  the `BLTItemTableauCapture`-style movie with a `CharacterImageIdentifierVM(hero.CharacterObject)`
  as the data source, poll `ImageIdentifierWidget.Texture` until populated (reuse the ~4s/80×50ms
  poll loop and the `TaleWorlds.Engine.GauntletUI.EngineTexture` unwrap already working), call
  `TextureComplete`, save to `gallery/{hero.StringId}.png` via `Texture.SaveToFile(path, true)`,
  tear down the layer in a `finally` block.
- Sequential, one hero at a time (not parallelized) — matches today's proven-safe pattern and
  avoids any risk of concurrent GauntletLayer/main-thread contention. Expect roughly 2-4 seconds
  per hero.

### Step 3: Compute stats per hero

For each hero, gather:
- Name, class (from `HeroClassDef`), equipment tier (1-8, via
  `BLTAdoptAHeroCampaignBehavior.GetEquipmentTier`)
- Kills, battles, level (already-tracked hero mission/campaign stats)
- Computed HP / Damage / Armor — the *effective* values after all active modifiers (perks,
  prestige, Tier 7/8 multipliers, class powers), not just base stats. Reuses the same
  bonus-lookup helpers (`PerkService.GetBonus`, `PrestigeStatPatches`/`T8HealthPatch` multiplier
  logic) already used to apply these bonuses live, just read instead of applied.

### Step 4: Generate gallery.html

A single static HTML page, styled to match the existing web-overlay dark theme
(`BannerlordTwitch/_Module/web/index-template.html`'s look), with one card per hero: portrait
image + name + class + tier + kills/battles/level + HP/Damage/Armor. Built the same way
`index-template.html` → `index.html` is today (string template with placeholders, not a full
templating engine) — but written fresh each generation (no `$version$`-style build-time tokens
needed, this is a runtime-generated file, not a build artifact).

### Step 5: Publish to Neocities

Reuses the existing Neocities upload code already in `BLTConfigureWindow.xaml.cs` (Basic Auth
with the username/password already stored in `AuthSettings`, `POST
https://neocities.org/api/upload`) — the exact mechanism that already publishes the
documentation page. `gallery.html` and the per-hero PNGs get uploaded alongside the existing
site content, as a new page (not replacing the docs page).

## Data flow summary

```
[Streamer clicks "Generate Hero Gallery" in BLTConfigure]
        |
        v
GetAllAdoptedHeroes() -> List<Hero>
        |
        v
For each hero (sequential, main-thread dispatched):
    render portrait PNG  ->  gallery/{heroId}.png
    compute stats
        |
        v
Build gallery.html (all heroes' cards)
        |
        v
Upload gallery.html + PNGs to Neocities (existing API client)
        |
        v
Public URL live, viewers can browse anytime
```

## Error handling

- Per-hero render failure (texture never populates within the timeout, same failure mode already
  seen and handled in the documentation generator): log and skip that hero, continue with the
  rest — one bad render shouldn't abort the whole gallery.
- Neocities upload failure (network/auth error): surface a clear error in the BLTConfigure
  window (reuse whatever error-display convention its existing Neocities upload code already
  uses for the docs page) rather than a silent failure. Local `gallery.html` and PNGs remain on
  disk either way, so a retry doesn't need to re-render.
- Missing/invalid Neocities credentials: same pre-flight check the existing docs-upload button
  presumably already does — surface the same "log in first" message rather than attempting doomed
  API calls.

## Testing

No automated test harness exists for this kind of in-game-rendering code (confirmed by today's
session — the documentation generator's tableau rendering was verified manually, in a live game
session, via screenshots and console feed). This feature will be verified the same way:
1. Build and deploy to the live game install.
2. Adopt/ensure a handful of heroes with varied classes/tiers are active.
3. Click "Generate Hero Gallery" from BLTConfigure while on the campaign map.
4. Confirm each hero's PNG renders (no stuck/blank portraits) and stats are correct.
5. Confirm the page uploads to Neocities and is reachable at its public URL.

## Open questions / follow-ups (not blocking this spec)

- Exact visual layout/styling of the gallery cards is left to implementation-time judgment,
  following the existing web-overlay dark theme rather than being pinned down here.
- Whether `GetAllAdoptedHeroes()` could return an unwieldy number of heroes on a large channel is
  a known theoretical scaling concern, explicitly deferred — not a blocker for this design.
