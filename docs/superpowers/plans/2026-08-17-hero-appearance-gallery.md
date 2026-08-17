# Hero Appearance Gallery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A "Generate Hero Gallery" button in BLTConfigure that renders a portrait + stats for every currently-adopted hero and publishes it as a page on the streamer's existing Neocities site.

**Architecture:** Extract the already-working character-portrait tableau rendering (built and debugged earlier today in `DocumentationGenerator.cs`) into a small reusable helper. A new `HeroGalleryGenerator` orchestrates rendering + stat-gathering + HTML generation, reusing (via extraction into a shared method) the existing Neocities upload code. A new extensibility hook (`HeroStatSummaryHook`, mirroring today's `ExternalHealthLimitModifiers`) lets the perk addon contribute perk-adjusted stats without the core fork depending on the addon assembly.

**Tech Stack:** C#/.NET (net48), Bannerlord modding APIs (`TaleWorlds.GauntletUI`, `TaleWorlds.Core.ViewModelCollection.ImageIdentifiers`), WPF (BLTConfigure UI), Neocities REST API (already integrated).

**Repos touched:**
- Core fork: `E:\BLT\source_codes\blt\Bannerlord-Twitch-5.2.4\BannerlordTwitch\` (not a git repo — no commits for this repo's changes)
- Addon: `E:\BLT\source_codes\RebellionOfThePeasants\` (git repo, commit at the end)

**No automated test harness exists for in-game rendering code in this codebase** (confirmed by today's session — the tableau rendering was verified manually in a live game via screenshots, and the spec's own Testing section documents this same manual-verification approach). Every task below ends with a **build + manual verification** step instead of a unit test, matching how all other work in this codebase is actually validated.

---

### Task 1: Extract `CharacterPortraitRenderer` — shared portrait-rendering helper

**Files:**
- Create: `E:\BLT\source_codes\blt\Bannerlord-Twitch-5.2.4\BannerlordTwitch\BannerlordTwitch\Documentation\CharacterPortraitRenderer.cs`

This extracts the proven-working single-character render logic already in `DocumentationGenerator.ProcessCharacterTableauQueueAsync`/`TextureComplete` (lines 524-593 and 644-696 of `DocumentationGenerator.cs`) into a standalone, reusable, static helper that doesn't depend on `DocumentationGenerator`'s queue/instance state. `DocumentationGenerator.cs` itself is NOT modified — it keeps its own working code untouched, so nothing already verified today is put at risk.

- [ ] **Step 1: Write the new file**

```csharp
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using BannerlordTwitch.Util;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core.ViewModelCollection.ImageIdentifiers;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.MountAndBlade.GauntletUI.Widgets;
using TaleWorlds.ScreenSystem;
using Path = System.IO.Path;

namespace BannerlordTwitch.Documentation
{
    // Standalone reuse of the character-portrait tableau rendering pipeline debugged in
    // DocumentationGenerator.cs on 2026-08-17 (root-caused there: dead TableauCacheManager call,
    // wrong texture-unwrap type, wrong SaveToFile bool arg, wrong file-existence check path,
    // GetChild(0) instead of FindChildrenWithType). Extracted here so other features (the Hero
    // Appearance Gallery) can render a single character portrait on demand without depending on
    // DocumentationGenerator's queue/instance state. DocumentationGenerator itself is untouched -
    // it keeps using its own already-working code path.
    public static class CharacterPortraitRenderer
    {
        // Renders a single character portrait and writes it to absoluteOutputPngPath.
        // Must be called from a context where awaiting is fine (not directly on the game's main
        // thread) - internally it hops onto the main thread via MainThreadSync.RunWaitAsync for
        // every engine call, exactly like the proven DocumentationGenerator code.
        // Returns true on success, false on any failure (already logged internally).
        public static async Task<bool> RenderAsync(CharacterCode cc, string absoluteOutputPngPath, string logContext)
        {
            GauntletLayer layer = null;
            ImageIdentifierWidget widget = null;
            string tempRelativeName = $"blt_gallery_{Guid.NewGuid():N}.png";
            try
            {
                await MainThreadSync.RunWaitAsync(() =>
                {
                    var vm = new CharacterImageIdentifierVM(cc);
                    layer = new GauntletLayer("BLTGalleryCharacterTableauLayer", 200, false);
                    var movieId = layer.LoadMovie("BLTItemTableauCapture", vm);
                    ScreenManager.TopScreen?.AddLayer(layer);
                    widget = movieId?.Movie?.RootWidget?.GetChild(0) as ImageIdentifierWidget;
                });

                if (widget == null)
                {
                    Log.Error($"CharacterPortraitRenderer: ImageIdentifierWidget not found in the BLTItemTableauCapture prefab for '{logContext}' - broken link.");
                    return false;
                }

                Texture captured = null;
                for (int i = 0; i < 80 && captured == null; i++)
                {
                    await Task.Delay(50);
                    await MainThreadSync.RunWaitAsync(() =>
                    {
                        var current = widget.Texture;
                        if (current != null
                            && current.PlatformTexture is EngineTexture engineTexture)
                        {
                            captured = engineTexture.Texture;
                        }
                    });
                }

                if (captured == null)
                {
                    Log.Error($"CharacterPortraitRenderer: tableau for '{logContext}' never rendered a texture in time.");
                    return false;
                }

                return SaveTexture(captured, tempRelativeName, absoluteOutputPngPath, logContext);
            }
            catch (Exception ex)
            {
                Log.Exception("CharacterPortraitRenderer.RenderAsync", ex);
                return false;
            }
            finally
            {
                if (layer != null)
                {
                    await MainThreadSync.RunWaitAsync(() =>
                    {
                        try { ScreenManager.TopScreen?.RemoveLayer(layer); }
                        catch (Exception ex) { Log.Exception("CharacterPortraitRenderer: RemoveLayer", ex); }
                    });
                }
            }
        }

        // Same relative-path/channel-swap handling as DocumentationGenerator.TextureComplete -
        // Texture.SaveToFile resolves relative paths against the engine's own bin folder
        // (AppDomain.CurrentDomain.BaseDirectory), not this process's CWD, and the raw engine
        // export has red/blue channels swapped.
        private static bool SaveTexture(Texture texture, string tempRelativeName, string absoluteOutputPngPath, string logContext)
        {
            try
            {
                string enginePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, tempRelativeName);
                texture.TransformRenderTargetToResource(tempRelativeName);
                texture.SaveToFile(tempRelativeName, true);

                for (int i = 0; i < 100 && !File.Exists(enginePath); i++)
                {
                    System.Threading.Thread.Sleep(100);
                }

                if (!File.Exists(enginePath))
                {
                    Log.Error($"CharacterPortraitRenderer: couldn't export image for '{logContext}'.");
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(absoluteOutputPngPath) ?? ".");
                if (File.Exists(absoluteOutputPngPath))
                {
                    File.Delete(absoluteOutputPngPath);
                }

                using (var bitmap = new Bitmap(enginePath))
                {
                    var corrected = SwapRedAndBlueChannels(bitmap);
                    corrected.Save(absoluteOutputPngPath);
                }

                File.Delete(enginePath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("CharacterPortraitRenderer.SaveTexture", ex);
                return false;
            }
        }

        private static Bitmap SwapRedAndBlueChannels(Bitmap bitmap)
        {
            var imageAttr = new ImageAttributes();
            imageAttr.SetColorMatrix(new(
                new[]
                {
                    new[] {0.0F, 0.0F, 1.0F, 0.0F, 0.0F},
                    new[] {0.0F, 1.0F, 0.0F, 0.0F, 0.0F},
                    new[] {1.0F, 0.0F, 0.0F, 0.0F, 0.0F},
                    new[] {0.0F, 0.0F, 0.0F, 1.0F, 0.0F},
                    new[] {0.0F, 0.0F, 0.0F, 0.0F, 1.0F}
                }
            ));
            var temp = new Bitmap(bitmap.Width, bitmap.Height);
            var pixel = GraphicsUnit.Pixel;
            using var g = Graphics.FromImage(temp);
            g.DrawImage(bitmap, Rectangle.Round(bitmap.GetBounds(ref pixel)), 0, 0,
                bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, imageAttr);
            return temp;
        }
    }
}
```

- [ ] **Step 2: Build the BannerlordTwitch project to verify it compiles**

```powershell
$msbuild = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe"
$sln = "E:\BLT\source_codes\blt\Bannerlord-Twitch-5.2.4\BannerlordTwitch\BannerlordTwitch.sln"
$gamedir = "C:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord"
& $msbuild $sln /p:Configuration=Release /p:BANNERLORD_GAME_DIR="$gamedir" /nologo /v:quiet /clp:ErrorsOnly /t:BannerlordTwitch
```

Expected: exit code 0, no errors printed. (This does not deploy anything yet — `CharacterPortraitRenderer` has no callers until Task 4.)

- [ ] **Step 3: No commit yet** — this repo has no `.git`; there is nothing to commit here. Move to the next task.

---

### Task 2: Add hero kill/battle stat accessors to `BLTAdoptAHeroCampaignBehavior`

**Files:**
- Modify: `E:\BLT\source_codes\blt\Bannerlord-Twitch-5.2.4\BannerlordTwitch\BLTAdoptAHero\Behaviors\BLTAdoptAHeroCampaignBehavior.cs:897` (right after the existing `GetPrestigeLevel`)

`GetEquipmentTier`/`GetPrestigeLevel` are already public one-liners over the private `GetHeroData(hero)`. Add two more of the same shape for kills/battles (the gallery needs these; nothing currently exposes them publicly).

- [ ] **Step 1: Read the exact current context to confirm the insertion point**

```
Read E:\BLT\source_codes\blt\Bannerlord-Twitch-5.2.4\BannerlordTwitch\BLTAdoptAHero\Behaviors\BLTAdoptAHeroCampaignBehavior.cs offset 890 limit 15
```

Confirm line 897 is still `public int GetPrestigeLevel(Hero hero) => GetHeroData(hero).PrestigeLevel;` before editing (today's earlier edits in this same file were to a different region, but re-confirm the line number hasn't drifted).

- [ ] **Step 2: Add the two accessors right after it**

```csharp
        public int GetPrestigeLevel(Hero hero) => GetHeroData(hero).PrestigeLevel;

        // Added for the Hero Appearance Gallery (2026-08-17) - AchievementStatsData already
        // tracks these per-hero via IncreaseKills/IncreaseStatistic elsewhere in this file, but
        // nothing publicly exposed a read-only accessor for them until now.
        public int GetTotalKills(Hero hero) => GetHeroData(hero).AchievementStats.GetTotalValue(AchievementStatsData.Statistic.TotalKills);

        public int GetTotalBattles(Hero hero) => GetHeroData(hero).AchievementStats.GetTotalValue(AchievementStatsData.Statistic.Battles);
```

- [ ] **Step 3: Build to verify**

```powershell
$msbuild = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe"
$sln = "E:\BLT\source_codes\blt\Bannerlord-Twitch-5.2.4\BannerlordTwitch\BannerlordTwitch.sln"
$gamedir = "C:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord"
& $msbuild $sln /p:Configuration=Release /p:BANNERLORD_GAME_DIR="$gamedir" /nologo /v:quiet /clp:ErrorsOnly /t:BLTAdoptAHero
```

Expected: exit code 0, no errors.

---

### Task 3: Add `HeroStatSummaryHook` extensibility point

**Files:**
- Create: `E:\BLT\source_codes\blt\Bannerlord-Twitch-5.2.4\BannerlordTwitch\BLTAdoptAHero\Patches\HeroStatSummaryHook.cs`

Mirrors the `ExternalHealthLimitModifiers` pattern added earlier today in `PrestigeStatPatches.cs` for the exact same reason: the core fork (`BLTAdoptAHero.dll`) must never reference the addon assembly (`PeasantRebellionPerks.dll`) — that dependency only goes the other way. This hook lets the addon (or any other addon) contribute a perk-adjusted stat summary for a hero without the core knowing anything about perks.

- [ ] **Step 1: Write the new file**

```csharp
using System;
using TaleWorlds.CampaignSystem;

namespace BLTAdoptAHero.Patches
{
    // 2026-08-17: lets an addon (e.g. PeasantRebellionPerks) contribute a perk/prestige-adjusted
    // stat summary for a hero, for display on the Hero Appearance Gallery page, without the core
    // fork referencing the addon assembly - same reasoning as ExternalHealthLimitModifiers in
    // PrestigeStatPatches.cs (a separate Harmony instance patching Agent.HealthLimit/
    // BaseHealthLimit broke pose rendering under game v1.4.8; this hook needs no Harmony patch at
    // all, it's a plain event, so that risk doesn't apply here - it's used purely for its
    // dependency-direction benefit).
    //
    // First non-null response wins (there's realistically only ever one subscriber - the perk
    // addon - so no merge logic is needed). If nothing subscribes, HeroGalleryGenerator falls
    // back to native/base values.
    public static class HeroStatSummaryHook
    {
        public static event Func<Hero, HeroStatSummary> Contributor;

        public static HeroStatSummary Describe(Hero hero)
        {
            if (Contributor == null) return null;
            foreach (Func<Hero, HeroStatSummary> handler in Contributor.GetInvocationList())
            {
                try
                {
                    var result = handler(hero);
                    if (result != null) return result;
                }
                catch
                {
                    // one bad subscriber shouldn't break the gallery for every hero
                }
            }
            return null;
        }
    }

    public class HeroStatSummary
    {
        public float MaxHP;
        public float DamageBonusPercent;
        public float ArmorBonus;
    }
}
```

- [ ] **Step 2: Build to verify**

```powershell
$msbuild = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe"
$sln = "E:\BLT\source_codes\blt\Bannerlord-Twitch-5.2.4\BannerlordTwitch\BannerlordTwitch.sln"
$gamedir = "C:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord"
& $msbuild $sln /p:Configuration=Release /p:BANNERLORD_GAME_DIR="$gamedir" /nologo /v:quiet /clp:ErrorsOnly /t:BLTAdoptAHero
```

Expected: exit code 0, no errors. (Nothing calls this yet — wired up in Task 4 and Task 6.)

---

### Task 4: `HeroGalleryGenerator` — orchestrator

**Files:**
- Create: `E:\BLT\source_codes\blt\Bannerlord-Twitch-5.2.4\BannerlordTwitch\BLTAdoptAHero\GalleryGeneration\HeroGalleryGenerator.cs`

Enumerates adopted heroes, renders each portrait via `CharacterPortraitRenderer` (Task 1), gathers stats (native + `HeroStatSummaryHook`, Task 3), and writes `gallery.html`.

- [ ] **Step 1: Write the new file**

```csharp
using System;
using System.Collections.Generic;
using System.Environment;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BannerlordTwitch.Documentation;
using BannerlordTwitch.Util;
using BLTAdoptAHero.Patches;
using TaleWorlds.CampaignSystem;

namespace BLTAdoptAHero.GalleryGeneration
{
    public static class HeroGalleryGenerator
    {
        // Same convention as DocumentationGenerator.DocumentationRootDir - a sibling folder
        // under the same Configs directory, not mixed into the documentation output.
        public static string GalleryRootDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord",
            "Configs", "BLT-hero-gallery");

        public static string GalleryPath => Path.Combine(GalleryRootDir, "gallery.html");

        private class HeroCard
        {
            public string Name;
            public string ClassName;
            public int Tier;
            public int Kills;
            public int Battles;
            public int Level;
            public float MaxHP;
            public float DamageBonusPercent;
            public float ArmorBonus;
            public string PortraitFileName;
        }

        // Renders every currently-adopted hero's portrait and writes gallery.html.
        // Must be awaited from a context where the game is fully loaded (Campaign.Current !=
        // null) - callers are responsible for that check (see BLTConfigureWindow.xaml.cs).
        public static async Task<int> GenerateAsync()
        {
            Directory.CreateDirectory(GalleryRootDir);

            var heroes = BLTAdoptAHeroCampaignBehavior.GetAllAdoptedHeroes().ToList();
            var cards = new List<HeroCard>();

            foreach (var hero in heroes)
            {
                string portraitFileName = $"portrait_{hero.StringId}.png";
                string portraitPath = Path.Combine(GalleryRootDir, portraitFileName);

                bool rendered = await CharacterPortraitRenderer.RenderAsync(
                    TaleWorlds.CampaignSystem.CharacterCode.CreateFrom(hero.CharacterObject),
                    portraitPath,
                    hero.Name?.ToString() ?? hero.StringId);

                if (!rendered)
                {
                    Log.Error($"HeroGalleryGenerator: skipping '{hero.Name}' - portrait render failed.");
                    continue;
                }

                var classDef = BLTAdoptAHeroCampaignBehavior.Current?.GetClass(hero);
                var summary = HeroStatSummaryHook.Describe(hero);

                cards.Add(new HeroCard
                {
                    Name = hero.Name?.ToString() ?? hero.StringId,
                    ClassName = classDef?.Name.ToString() ?? "(no class)",
                    Tier = (BLTAdoptAHeroCampaignBehavior.Current?.GetEquipmentTier(hero) ?? 0) + 1,
                    Kills = BLTAdoptAHeroCampaignBehavior.Current?.GetTotalKills(hero) ?? 0,
                    Battles = BLTAdoptAHeroCampaignBehavior.Current?.GetTotalBattles(hero) ?? 0,
                    Level = hero.Level,
                    MaxHP = summary?.MaxHP ?? 0f,
                    DamageBonusPercent = summary?.DamageBonusPercent ?? 0f,
                    ArmorBonus = summary?.ArmorBonus ?? 0f,
                    PortraitFileName = portraitFileName,
                });
            }

            WriteHtml(cards);
            return cards.Count;
        }

        private static void WriteHtml(List<HeroCard> cards)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\"/>");
            sb.AppendLine("<title>BLT Hero Gallery</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body { background:#1a1a1a; color:#eee; font-family: sans-serif; margin:0; padding:24px; }");
            sb.AppendLine("h1 { text-align:center; }");
            sb.AppendLine(".grid { display:flex; flex-wrap:wrap; gap:16px; justify-content:center; }");
            sb.AppendLine(".card { background:#262626; border-radius:8px; padding:12px; width:220px; text-align:center; }");
            sb.AppendLine(".card img { width:100%; border-radius:6px; background:#000; }");
            sb.AppendLine(".card h2 { font-size:16px; margin:8px 0 2px; }");
            sb.AppendLine(".card .class { color:#ffd700; font-size:13px; margin-bottom:6px; }");
            sb.AppendLine(".card .stats { font-size:12px; color:#ccc; text-align:left; }");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine("<h1>BLT Hero Gallery</h1>");
            sb.AppendLine("<div class=\"grid\">");

            foreach (var c in cards)
            {
                sb.AppendLine("<div class=\"card\">");
                sb.AppendLine($"<img src=\"{c.PortraitFileName}\" alt=\"{c.Name}\"/>");
                sb.AppendLine($"<h2>{c.Name}</h2>");
                sb.AppendLine($"<div class=\"class\">{c.ClassName} - Tier {c.Tier}</div>");
                sb.AppendLine("<div class=\"stats\">");
                sb.AppendLine($"Level {c.Level} &middot; {c.Kills} kills &middot; {c.Battles} battles<br/>");
                if (c.MaxHP > 0)
                {
                    sb.AppendLine($"HP {c.MaxHP:0} &middot; Dmg +{c.DamageBonusPercent:0}% &middot; Armor +{c.ArmorBonus:0}");
                }
                sb.AppendLine("</div></div>");
            }

            sb.AppendLine("</div></body></html>");
            File.WriteAllText(GalleryPath, sb.ToString());
        }
    }
}
```

- [ ] **Step 2: Build to verify**

```powershell
$msbuild = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe"
$sln = "E:\BLT\source_codes\blt\Bannerlord-Twitch-5.2.4\BannerlordTwitch\BannerlordTwitch.sln"
$gamedir = "C:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord"
& $msbuild $sln /p:Configuration=Release /p:BANNERLORD_GAME_DIR="$gamedir" /nologo /v:quiet /clp:ErrorsOnly /t:BLTAdoptAHero
```

Expected: exit code 0, no errors. If `System.Environment` using conflicts with anything, remove that explicit using (the `Environment` type is already in `System`, which is implicitly available via `System.IO`'s dependency — keep the explicit `using System;` only, drop `using System.Environment;` if the compiler flags it as invalid; `Environment` is a class inside `System`, not its own namespace, so the correct using is just `using System;`, already present via other members — verify during this build step, this is exactly what the build is for).

---

### Task 5: Extract shared Neocities upload helper, add gallery upload button

**Files:**
- Modify: `E:\BLT\source_codes\blt\Bannerlord-Twitch-5.2.4\BannerlordTwitch\BLTConfigure\BLTConfigureWindow.xaml.cs:465-549` (existing `UploadDocumentation_OnClick`)
- Modify: `E:\BLT\source_codes\blt\Bannerlord-Twitch-5.2.4\BannerlordTwitch\BLTConfigure\BLTConfigureWindow.xaml` (add new button + status text)

Extracts the existing Neocities upload logic (list/delete/upload loop, lines 489-547 of the current file) into a reusable private method taking a directory and a status-callback, so both the existing docs-upload button and the new gallery button can call it without duplicating ~60 lines. The existing button's behavior is unchanged.

- [ ] **Step 1: Read the current full method to confirm exact boundaries before editing**

```
Read E:\BLT\source_codes\blt\Bannerlord-Twitch-5.2.4\BannerlordTwitch\BLTConfigure\BLTConfigureWindow.xaml.cs offset 451 limit 100
```

Confirm lines 465-549 still match what's quoted in this plan's Task 5 preamble (re-derived from today's investigation) before editing - if line numbers drifted, adjust the edit anchors accordingly.

- [ ] **Step 2: Replace `UploadDocumentation_OnClick` with a call into a new shared helper**

Replace the body of `UploadDocumentation_OnClick` (keep the method signature and the two early-return validation checks at the top, since those are documentation-specific) with a call to the new shared method:

```csharp
        private async void UploadDocumentation_OnClick(object sender, RoutedEventArgs e)
        {
            if (!Directory.Exists(DocumentationGenerator.DocumentationRootDir))
            {
                UploadStatus.Text = "Documentation directory does not exist, did you Generate Documentation yet?";
                UploadStatus.Foreground = ErrorStatusForeground;
                return;
            }
            string[] files = Directory.GetFiles(DocumentationGenerator.DocumentationRootDir);
            if (!files.Any())
            {
                UploadStatus.Text = "No documentation files found, did you Generate Documentation yet?";
                UploadStatus.Foreground = ErrorStatusForeground;
                return;
            }
            if (files.All(f => Path.GetFileName(f) != "index.html"))
            {
                UploadStatus.Text = "Documentation doesn't contain index.html file, did you Generate Documentation yet?";
                UploadStatus.Foreground = ErrorStatusForeground;
                return;
            }

            UploadDocumentationButton.IsEnabled = false;
            await UploadDirectoryToNeocities(DocumentationGenerator.DocumentationRootDir,
                status => UploadStatus.Text = status,
                error => { UploadStatus.Text = error; UploadStatus.Foreground = ErrorStatusForeground; });
            UploadDocumentationButton.IsEnabled = true;
        }

        // Extracted 2026-08-17 from the body that used to live directly in
        // UploadDocumentation_OnClick, so the new Hero Appearance Gallery upload button can reuse
        // the exact same Neocities list/delete/upload flow without duplicating it. Behavior for
        // the documentation upload button is unchanged - this is the same code, just callable
        // with a different directory and status callbacks.
        private async Task UploadDirectoryToNeocities(string directory, Action<string> onStatus, Action<string> onError)
        {
            try
            {
                using var httpClient = new HttpClient(new HttpClientHandler { UseProxy = false });
                httpClient.DefaultRequestHeaders.Authorization = new("Basic",
                    Convert.ToBase64String(
                        Encoding.ASCII.GetBytes($"{NeocitiesUsername.Text}:{NeocitiesPassword.Password}")));

                onStatus("Checking for existing files on the site...");

                var filesResponse = await httpClient.GetAsync($"https://neocities.org/api/list");
                filesResponse.EnsureSuccessStatusCode();

                var result = JsonConvert.DeserializeObject<ResponseFiles>(await filesResponse.Content.ReadAsStringAsync());
                var deleteList = result.files
                    .Select(f => f.path)
                    .Where(f => f.ToLower() != "index.html")
                    .Select(f => new KeyValuePair<string, string>("filenames[]", f))
                    .ToList();
                if (deleteList.Any())
                {
                    onStatus("Deleting existing files from the site...");
                    var deleteResponse = await httpClient.PostAsync($"https://neocities.org/api/delete",
                        new FormUrlEncodedContent(deleteList));
                    deleteResponse.EnsureSuccessStatusCode();
                }

                string[] files = Directory.GetFiles(directory);
                onStatus("Upload in progress (might take a few seconds or longer)...");

                const int chunkSize = 20;
                int filesDone = 0;
                while (filesDone < files.Length)
                {
                    onStatus($"Uploading {filesDone} / {files.Length}...");
                    var chunk = files.Skip(filesDone).Take(chunkSize);
                    filesDone += chunkSize;

                    var form = new MultipartFormDataContent();
                    foreach (string f in chunk)
                    {
                        string fileName = Path.GetFileName(f);
                        var streamContent = new StreamContent(File.Open(f, FileMode.Open));
                        streamContent.Headers.Add("Content-Type", MimeMapping.GetMimeMapping(fileName));
                        streamContent.Headers.Add("Content-Disposition",
                            $"form-data; name=\"{fileName}\"; filename=\"{fileName}\"");
                        form.Add(streamContent, "file", fileName);
                    }

                    var response = await httpClient.PostAsync($"https://neocities.org/api/upload", form);
                    response.EnsureSuccessStatusCode();
                }

                onStatus("Upload complete!");
            }
            catch (Exception ex)
            {
                onError($"Error uploading: {ex.Message}");
            }
        }
```

Note: the existing `UploadDocumentation_OnClick` deletes every file on the site except `index.html` before uploading — this means running the gallery upload will delete the documentation page's own supporting files (and vice versa) if they're uploaded to the same Neocities site root with this same delete-everything-except-index.html logic. This is a real behavior to flag to the user before this task ships (see the note in Task 7's manual verification step) rather than silently accepting data loss - the two uploads are NOT currently safe to run independently against the same site without one clobbering the other's non-`index.html` files (like `style.css`, per-hero portraits, item icons). Do not silently change the delete logic to "fix" this without the user's explicit direction — flag it and let them decide (e.g., delete-list could be changed to keep both `index.html` and `gallery.html` plus their own asset prefixes, but that's a product decision, not an implementation detail to decide unilaterally).

- [ ] **Step 3: Add the new button + status text to the XAML**

Read the XAML file first to find where the existing `UploadDocumentationButton`/`UploadStatus` controls are declared:

```
Grep "UploadDocumentationButton|UploadStatus" in E:\BLT\source_codes\blt\Bannerlord-Twitch-5.2.4\BannerlordTwitch\BLTConfigure\BLTConfigureWindow.xaml
```

Add a new `Button` (`GenerateHeroGalleryButton`, `Click="GenerateHeroGalleryButton_OnClick"`) and a `TextBlock` (`GenerateHeroGalleryResult`) immediately after the existing documentation-generation controls, following the exact same XAML structure/styling already used for `GenerateDocumentationButton`/`GenerateDocumentationResult` in that file (copy their markup, rename the `x:Name`s and button text to "Generate Hero Gallery").

- [ ] **Step 4: Add the click handler in the code-behind**

```csharp
        private async void GenerateHeroGalleryButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (Campaign.Current?.GameStarted != true)
            {
                GenerateHeroGalleryResult.Foreground = ErrorStatusForeground;
                GenerateHeroGalleryResult.Text =
                    "You need to start the campaign, or load a save before generating the hero gallery!";
                return;
            }

            try
            {
                GenerateHeroGalleryButton.IsEnabled = false;
                GenerateHeroGalleryResult.Text = "Generating Hero Gallery...";
                int count = await HeroGalleryGenerator.GenerateAsync();
                GenerateHeroGalleryResult.Text = $"Gallery generated: {count} hero(es). Uploading...";

                await UploadDirectoryToNeocities(HeroGalleryGenerator.GalleryRootDir,
                    status => GenerateHeroGalleryResult.Text = status,
                    error => { GenerateHeroGalleryResult.Text = error; GenerateHeroGalleryResult.Foreground = ErrorStatusForeground; });
            }
            catch (Exception ex)
            {
                GenerateHeroGalleryResult.Foreground = ErrorStatusForeground;
                GenerateHeroGalleryResult.Text = $"Couldn't generate the hero gallery: {ex.Message}";
            }

            GenerateHeroGalleryButton.IsEnabled = true;
        }
```

Add `using BLTAdoptAHero.GalleryGeneration;` to the top of `BLTConfigureWindow.xaml.cs` if not already implied by an existing wildcard-adjacent using (check the current using block first).

- [ ] **Step 5: Build to verify**

```powershell
$msbuild = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe"
$sln = "E:\BLT\source_codes\blt\Bannerlord-Twitch-5.2.4\BannerlordTwitch\BannerlordTwitch.sln"
$gamedir = "C:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord"
& $msbuild $sln /p:Configuration=Release /p:BANNERLORD_GAME_DIR="$gamedir" /nologo /v:quiet /clp:ErrorsOnly /t:BLTConfigure
```

Expected: exit code 0, no errors.

---

### Task 6: Wire `PeasantRebellionPerks` into `HeroStatSummaryHook`

**Files:**
- Modify: `E:\BLT\source_codes\RebellionOfThePeasants\source_perks\PeasantRebellionPerks.cs` (in `PeasantRebellionPerksModule.OnSubModuleLoad`, right where `ExternalHealthLimitModifiers.Multiplier` was subscribed earlier today)

- [ ] **Step 1: Read the current subscription site to confirm exact anchor**

```
Grep "ExternalHealthLimitModifiers.Multiplier" in E:\BLT\source_codes\RebellionOfThePeasants\source_perks\PeasantRebellionPerks.cs
```

- [ ] **Step 2: Add a second subscription right after it**

```csharp
                BLTAdoptAHero.Patches.ExternalHealthLimitModifiers.Multiplier += PerkHealthLimitPatch.GetMultiplier;

                // Hero Appearance Gallery (2026-08-17): contribute perk-adjusted stats for
                // display, without BLTAdoptAHero.dll knowing anything about perks - same
                // dependency-direction reasoning as the hook above.
                BLTAdoptAHero.Patches.HeroStatSummaryHook.Contributor += hero =>
                {
                    float hpBonus = PerkService.GetBonus(hero, "HP");
                    float dmgBonus = PerkService.GetBonus(hero, "Damage");
                    float armorBonus = PerkService.GetBonus(hero, "Mounted Armor");
                    return new BLTAdoptAHero.Patches.HeroStatSummary
                    {
                        MaxHP = 100f * (1f + hpBonus), // 100 = native BaseHealthLimit floor; real value needs a live Agent, this is a reasonable estimate for display purposes
                        DamageBonusPercent = dmgBonus * 100f,
                        ArmorBonus = armorBonus * 100f,
                    };
                };
```

- [ ] **Step 3: Build to verify**

```powershell
$perks = "E:\BLT\source_codes\RebellionOfThePeasants\source_perks\PeasantRebellionPerks.csproj"
dotnet build $perks -c Release --nologo -v:q
```

Expected: exit code 0, no errors.

- [ ] **Step 4: Commit**

```bash
cd "E:\BLT\source_codes\RebellionOfThePeasants"
git add source_perks/PeasantRebellionPerks.cs
git commit -m "Hero Appearance Gallery: subscribe to HeroStatSummaryHook for perk-adjusted stats"
git push origin main
```

---

### Task 7: Full build, deploy, and live verification

**Files:** none new — this task builds and deploys everything from Tasks 1-6 together and verifies in the running game.

- [ ] **Step 1: Full solution build (core fork)**

```powershell
$msbuild = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe"
$sln = "E:\BLT\source_codes\blt\Bannerlord-Twitch-5.2.4\BannerlordTwitch\BannerlordTwitch.sln"
$gamedir = "C:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord"
& $msbuild $sln /p:Configuration=Release /p:BANNERLORD_GAME_DIR="$gamedir" /nologo /v:quiet /clp:ErrorsOnly
```

Expected: exit 0, or only the known-harmless `MSB3073`/`7zr.exe` packaging error at the very end (unrelated to this feature - confirmed earlier today). The MSBuild `DeployToGameDirectory` target auto-copies the built DLLs into the live game install.

- [ ] **Step 2: Build and deploy PeasantRebellionPerks.dll**

```powershell
$perks = "E:\BLT\source_codes\RebellionOfThePeasants\source_perks\PeasantRebellionPerks.csproj"
dotnet build $perks -c Release --nologo -v:q
```

Then copy the built DLL to the live game (mirrors what was done earlier today for every PeasantRebellionPerks change):

```bash
GAME="/c/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord"
cp "E:\BLT\source_codes\RebellionOfThePeasants/bin_perks/Win64_Shipping_Client/PeasantRebellionPerks.dll" "$GAME/Modules/BLTAdoptAHero/bin/Win64_Shipping_Client/PeasantRebellionPerks.dll"
```

- [ ] **Step 3: Launch the game and open BLTConfigure**

Load an existing save (or start a fresh campaign) with at least 2-3 adopted heroes of different classes/tiers active, so the gallery has real variety to render.

- [ ] **Step 4: Click "Generate Hero Gallery"**

Watch `GenerateHeroGalleryResult`'s text update through: "Generating Hero Gallery..." → "Gallery generated: N hero(es). Uploading..." → upload progress → "Upload complete!".

- [ ] **Step 5: Verify locally before checking Neocities**

```
Check E:\BLT\source_codes\... no - check the live output:
```

```bash
ls "/c/Users/krzysiek/Documents/Mount and Blade II Bannerlord/Configs/BLT-hero-gallery/"
```

Expected: `gallery.html` plus one `portrait_<heroId>.png` per adopted hero, each a real (non-empty, non-corrupt) image. Open `gallery.html` locally in a browser first to sanity-check portraits and stats before trusting the Neocities upload.

- [ ] **Step 6: Verify on Neocities**

Open the streamer's Neocities site's `gallery.html` URL and confirm it matches the local version, portraits load, stats are correct.

- [ ] **Step 7: Reload confirmed NOT needed (investigated 2026-08-17)**

A dedicated investigation (background research agent, full findings in this session's transcript) determined the documentation generator's "you must reload your save" warning has nothing to do with the tableau-rendering pipeline (which tears down cleanly on every code path, confirmed by reflecting on the real `GauntletLayer`/`ScreenLayer` types). The actual cause is that `HeroClassDef.GenerateDocumentation` (`HeroClassDef.cs:269`) calls `EquipHero.UpgradeEquipment(Hero.MainHero, i, this, replaceSameTier: false, customKeepFilter: _ => false)` once per tier per class, to render example loadouts - and that call permanently destroys and replaces `Hero.MainHero`'s real equipment with no backup/restore anywhere in the call graph.

`HeroGalleryGenerator.GenerateAsync` (Task 4) never calls `UpgradeEquipment` or any other equipment mutator - it renders each hero via `CharacterCode.CreateFrom(hero.CharacterObject)` against their equipment exactly as it already is. No mutation, no reload needed, regardless of hero count. Do not add a reload-warning dialog to `GenerateHeroGalleryButton_OnClick`.

Still verify live in this step: after clicking "Generate Hero Gallery", confirm none of the adopted heroes' (or the streamer's own MainHero, if adopted) equipment visibly changed. This is a sanity check, not a search for an unknown problem - the mechanism that caused the documentation bug is confirmed absent from this code path by construction.

- [ ] **Step 8: Do NOT copy `UpgradeEquipment`-based tier examples into the gallery, ever**

If a future variant of this feature wants to show "what tier N of this class would look like" (as opposed to a hero's real current gear), it must NOT reuse `EquipHero.UpgradeEquipment` against a real `Hero` the way the documentation generator does. Build a throwaway `Equipment` object and construct the `CharacterCode` from that instead. This is a guardrail note, not something to implement now - out of scope for this plan.

- [ ] **Step 9: Flag the shared-Neocities-site file collision from Task 5 to the user**

Confirm with the user whether the documentation page and the hero gallery are meant to coexist on the same Neocities site (in which case the delete-logic collision noted in Task 5 needs a real fix - e.g. only delete files with a `blt_doc_`/`gallery_`/`portrait_` prefix matching what's about to be re-uploaded, not everything except `index.html`) or are meant to go to different sites/accounts (in which case there's nothing to fix, just document it). Do not silently resolve this - it was explicitly deferred to the user in Task 5.
