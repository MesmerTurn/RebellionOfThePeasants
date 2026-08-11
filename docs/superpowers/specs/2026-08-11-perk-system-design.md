# Perk System Design

## Overview

A new, standalone perk system for Peasant Rebellion: small permanent combat/utility bonuses,
unlocked with points earned from a hero's kills/battles, spent by the viewer via a `!perk`
chat command. Distinct from the existing Power Progression system (which auto-scales class
powers by tier) - this is a separate point pool the player actively chooses how to spend,
giving real build variety. Paired with a full visual redesign of the existing Neocities
documentation page, including a new "Perks" section rendering each branch as a constellation
diagram with a numbered legend.

Two previously-brainstormed, related features inform this design without being duplicated
here: [[idea_blt_boss_event]] (separate topic, not affected by this work) and the sibling
project BannerlordTTV's own perk-tree implementation, which served as a proven reference for
category shapes and hook points (`docs/superpowers/plans/2026-08-07-perk-tree-redesign.md` in
that repo) - **BannerlordTTV is reference only; nothing in that project is touched by this
design.**

## 1. Architecture

**New sub-module, same DLL.** `BLTPerkModule` joins the existing `BLTFormationModule` /
`BLTGuardModule` / `BLTUpgradeModule` / `BLTDuelModule` / `BLTClanGoldModule` / `BLTAurasModule`
- all compiled into the single `MakeBltGreatAgain.dll`, one build, one package, one
`SubModule.xml` entry added alongside the existing six. This matches the established pattern
for every major feature area in this codebase; a separate addon/DLL was considered and
rejected (no requirement surfaced for toggling perks independently of the rest of the mod).

**Separate point pool from Power Progression.** Reuses the same kill/battle counters Power
Progression already tracks per hero (no new tracking mechanism needed), but perks draw from
their own spendable point balance - investing in perks never affects Power Progression tier
scaling, and vice versa.

**Single source of truth for perk definitions**, rendered two ways: `!perk` shows it as chat
text, the Neocities page shows it as a visual constellation. Both read the same
`PerkDefsConfig`/`PerkDef` data - no duplicate content to keep in sync.

## 2. Data model

```csharp
internal sealed class PerkDef
{
    public string Key { get; set; }
    public string DisplayName { get; set; }
    public string Branch { get; set; }          // groups nodes for rendering/bonus lookup
    public int MaxRank { get; set; }             // 3-4 typical, not BTTV's up-to-6
    public float BonusPerRank { get; set; }      // ~1-2% per rank, deliberately weak
    public string RequiredPerkKey { get; set; }  // empty = branch root (free, 0 points)
    public int RequiredRank { get; set; } = 1;
    public int MinLevel { get; set; }            // 0 = no gate
    public int MinTier { get; set; }              // 0 = no gate
    public string RequiredAchievementKey { get; set; } // capstones only; empty = not gated
}
```

Host-editable via a new "Perks" tab in `BLTConfigure`, same list/detail editing shape as the
existing Power Progression / Upgrade editors. `PerkGlobalConfig` additionally exposes:
`KillsPerPoint`, `BattlesPerPoint` (mirrors `PowerProgressionGlobalConfig`'s
`TierKillThresholds`/`TierBattleThresholds` pattern), fully configurable by the streamer - no
values are hardcoded in a way the host can't change.

### 2.1 Default branch catalog (16 branches, seed data - all host-editable after)

**Universal** (proven shape, carried over conceptually from BannerlordTTV, values re-tuned
weaker): HP, Damage, Evade, Regen, Berserk, Loot, Speed, Accuracy, Mounted Armor, Mounted
Charge, Weapon Mastery (two-handed / polearm / thrown - three separate branches).

**BLT-unique** (no equivalent exists anywhere else, because the underlying mechanics only
exist in this project):
- **Lance Mastery** - synergizes with the existing Couched Lance / Lancer Charge mechanic
  (couch damage multiplier, stay-couched-on-kill chance).
- **Wanderer Bond** - strengthens the player's own hired Wanderer (stats or survival chance),
  synergizing with the existing Wanderer tier/death-chance system.
- **Aura Potency** - boosts radius/magnitude of the hero's aura power, if one is equipped.
- **Adrenaline Surge** - faster/stronger Adrenaline trigger at low HP.
- **Weapon Swap Speed** - a distinct engine stat from attack speed; exact
  `AgentDrivenProperties` field name needs reflection verification during implementation
  (flagged, not resolved here - see Open Questions).

Each branch: 3-4 ranks, ~1-2% per rank (weak by design - meant to feel rewarding over a long
campaign, not to power-creep combat). A handful of capstone nodes (one per BLT-unique branch,
to start) are gated by `RequiredAchievementKey` instead of points - not purchasable at all
until the specific achievement fires, reusing BLT's existing native achievement system.

## 3. Player interaction (`!perk`)

- **`!perk`** - points available, points invested, current rank in every branch (compact list).
- **`!perk <branch>`** - detail view for one branch: what each rank gives, what's unlocked,
  what's locked and why (missing prerequisite rank / level / tier / achievement).
- **`!perk buy <branch>`** - spends 1 point on the next rank, if requirements are met.
  Follows the existing reply-message conventions used by every other command in this codebase.
- **No refunds** - once a point is spent it stays spent, consistent with how every other
  purchase system in this mod already works (no take-backs).

## 4. Neocities page - visual redesign

### 4.1 Current state (verified by loading the live page)

The existing auto-generated documentation page (native `DocumentationGenerator`/
`IDocumentable` system, already covers every command/reward/class/config/achievement/upgrade/
campaign map) is published via a **pre-existing, already-working Neocities publish button in
BLTConfigure** (direct calls to `neocities.org/api/upload` using a stored username/password -
not the same mechanism BannerlordTTV uses, and not something this design needs to build).
Verified visually: dark grey background with **black body text** (a real legibility bug),
Times New Roman, plain black table borders - essentially unstyled.

**No new publish infrastructure is needed anywhere in this design** - only the CSS and a new
generator section change.

### 4.2 Approved visual direction: "Starry Realm"

Chosen via visual companion mockup review (2026-08-11), compared against two alternatives
("Parchment & Iron" - aged paper/wax-seal theme, "Terminal Grimoire" - green monospace
hacker-reads-a-grimoire theme). Dark navy-to-purple radial gradient background, gold
(`#ffd700`/`#d4af37`) text with subtle glow, purple (`#6b46c1`) borders in a
repeating-diagonal-stripe pattern mixing purple and gold, Georgia serif typography. Replaces
`Bannerlord-Twitch-Documentation.css` in place - same file, same generator, new rules.

### 4.3 Perk tree rendering: "Constellation"

Chosen over a conventional circles-and-lines tree layout (also mocked up and compared) because
it fits the Starry Realm theme directly - each perk renders as a glowing star (pulsing opacity
animation when unlocked, dimmed when locked) with a numbered label, connected to its
prerequisite by a thin gold line, small background stars scattered for atmosphere. Below each
branch's constellation, a numbered legend table (①②③...) states what each perk gives and what
it requires - identical content to what `!perk <branch>` shows in chat, just laid out
visually instead of as chat lines.

**Implementation approach:** `DocumentationGenerator` has no primitive for drawing shapes or
lines today - it builds pages from a structured tag builder (`H1`/`H2`/`Table`/`Div`/`P`, etc.)
that the rest of the page already uses throughout. It does, however, already solve an
analogous problem for the campaign map (`MapLabel`/`MapSegment` - absolutely-positioned `<div>`
elements simulating markers and connecting lines, not real SVG, because that's the established
pattern in this exact file). The perk constellation follows the same approach: two new
generator methods, `PerkNode(x, y, number, name, unlocked)` and `PerkLine(x1, y1, x2, y2)`,
built the same way `MapLabel`/`MapSegment` already are. Per-branch layout renders by iterating
that branch's perks, emitting one `PerkNode` + one `PerkLine` to each perk's prerequisite, then
a `Table` legend below built from the same per-perk description text `!perk <branch>` uses.

Node coordinates: computed automatically from each perk's position in its branch's requirement
chain to start (simpler, no manual layout work) rather than manually assigned `Row`/`Column`
per node (the approach BannerlordTTV's redesign used). If auto-layout looks bad against real
data once built, manual coordinate override can be added later - not a blocking decision now.

## 5. Open questions (deferred to the implementation plan, not blocking this spec)

- Exact `AgentDrivenProperties` field name for Weapon Swap Speed - needs a reflection check
  against the 1.4.7 assemblies (same verification style already used throughout this
  codebase's history) before that branch's hook can be written.
- Exact default point costs/thresholds/bonus percentages per branch - the shape (weak,
  ~1-2%/rank, 3-4 ranks, fully host-editable) is set; the literal seed numbers are an
  implementation-time detail, not a design decision.
- Which specific achievement key gates each BLT-unique capstone - to be chosen per-branch
  during implementation, reusing BLT's existing achievement key set where a good fit exists.

## Out of scope

- No respec/refund mechanism (matches this project's existing no-take-backs purchase pattern
  throughout).
- [[idea_blt_boss_event]] is unrelated and untouched by this work.
- No changes anywhere in BannerlordTTV - reference only.
