# BLT 5.4.1 for 1.3.15 — "Uprising of the Peasants" (Composable Power Engine)

## 0. What changed from the previous draft (read this first)

The previous draft proposed building a brand-new `PowerComponent`/`PowerDefinition`/`PowerEngine` triad, including **one new `MissionBehavior` dispatcher** to replace "~15 near-duplicate `OnMissionTick` sweep loops." Grounding that against the actual code changes the design materially:

- **The dispatcher already exists and is battle-tested.** The fork has `BLTHeroPowersMissionBehavior` + `PowerHandler` (`.../BLTAdoptAHero/Behaviors/PowerHandler.cs`). Powers do not each own a mission behavior — they subscribe to a rich per-hero event bus (`handlers.OnDoDamage`, `OnSlowTick`, `OnMissionTick`, `OnGotKilled`, `OnMissionOver`, `OnDecideWeaponCollisionReaction`, `OnAddMissile`, …). Registration is **already eager** (per-hero via `ConfigureHandlers`, subscribed inside `OnActivation`). We must **not** build a parallel dispatcher — that would duplicate the bus and re-introduce the exact multi-registration risks we are trying to avoid.
- **What is actually duplicated** is the *body* of each power's `OnActivation`: the DoT tracking dictionary + per-tick `RegisterBlow`, the aura `GetNearbyAgents` sweep with `lastInRange` contour diffing, and the `SafeSetContourColor` bookkeeping. That is the real compression target — shared **helper objects**, not a new engine.
- **`AddDamagePower` (`.../BLTAdoptAHero/Powers/AddDamagePower.cs`) is already the "AddDamageAbility" the collaborators described**, and is already ~90% of a composable on-hit component: it exposes DamageModifier%, DamageToAdd, ignore-armor%, unblockable%, shatter-shield%, cut-through%, stagger%, add/remove `HitBehavior`, AoE, per-target filters, ranged/melee/charge gates, and missile trail — all riding existing handlers. It is **missing only DoT (poison/bleed/burn) and slow-on-hit**. The collaborators' instinct ("make poison/bleed/burn/slow part of AddDamageAbility") is therefore the cleanest path *and* the lowest-risk one, because it adds **zero new Harmony patches**.

Net effect: the pilot gets smaller, safer, and more aligned with collaborator feedback. "PowerEngine" is re-scoped from "a new MissionBehavior" to "a set of reusable effect helpers plus one composed `HeroPowerDefBase` subclass," all riding the existing `PowerHandler` bus.

## 1. Context (unchanged premise)

MBGA (`E:\BLT\source_codes\MakeBltGreatAgain\source\MakeBltGreatAgain.cs`, ~8600 lines) has ~30 hand-coded power classes. Structurally they fall into three shapes:

1. **On-hit strikes** (PoisonStrike, BleedStrike, BurningStrike, FrostStrike, VampirismStrike): subscribe `handlers.OnDoDamage` to tag a victim, then `handlers.OnSlowTick` to apply periodic `RegisterBlow` damage + contour. Near-identical to each other; differ only in damage type, color, tick count, and one twist (frost=slow, vampirism=heal attacker).
2. **Aura pulses** (HealAura, DamageAura, CurseAura, WeaknessAura, FearAura, SlowAura, BattleCryAura, BuffAura): `handlers.OnSlowTick` → `Mission.GetNearbyAgents(radius)` → filter ally/enemy → apply an effect + contour, with `lastInRange` diffing for cleanup.
3. **Bespoke mechanics** (Teleport, JumpAttack, Kick, Shadowstep, Ironskin, Berserk, LastStand, Taunt, Necromancy): mechanically unique, no shared sweep.

Groups 1 and 2 are the compression target. Group 3 is explicitly out of scope (Section 6).

## 2. Decision: separate additive package, not a rewrite (unchanged, reaffirmed)

Build **BLT 5.4.1 for 1.3.15 "Uprising of the Peasants"** as a distinct package from the current live "Reforged v8," from the same repo/build pipeline. The live named powers stay byte-for-byte intact; the composable pieces ship as **new, opt-in** `HeroPowerDefBase` subclasses that appear alongside the existing powers in the powers picker. No save-format migration: nothing about existing powers' serialization changes.

## 3. Architecture (tightened, grounded in real code)

### 3.1 The bus already exists — ride it

All new work subscribes to `PowerHandler.Handlers`. No new `MissionBehavior`, no new Harmony patches for the pilot. This is the single most important design decision because it neutralizes three of the four landmines (Section 5) by construction.

### 3.2 Reusable effect helpers (`PowerComponent` re-scoped)

Instead of an abstract `PowerComponent` with an `OnApply/OnTick/OnHit/OnExpire` lifecycle wired into a bespoke engine, define **plain reusable helper types** that a power's `OnActivation` composes onto the existing handlers. Two helpers cover the entire pilot:

- **`DamageOverTimeEffect`** — extracted verbatim from `PoisonStrikePower.OnActivation` (`MakeBltGreatAgain.cs:3221-3285`): owns a `Dictionary<Agent,int> ticksRemaining`, wires `OnDoDamage` (tag victim, set contour) + `OnSlowTick` (apply `RegisterBlow`, decrement, clear contour at 0) + cleanup on deactivate/mission-over. Parameterized by damage/tick, ticks, `DamageTypes`, contour color, apply-on (melee/ranged/both), and an optional per-tick side-effect hook (frost→slow, vampirism→heal source). Poison/Bleed/Burn/Frost/Vampirism all become one helper with different parameters.
- **`AuraSweepEffect`** — extracted from the shared body of `DamageAuraPower`/`HealAuraPower`/etc. (`MakeBltGreatAgain.cs:3658-3724` is the canonical copy): owns `GetNearbyAgents(radius)`, `lastInRange` diff, tick-interval gating, ally/enemy filter, max-agents cap, and contour apply/clear. Parameterized by radius, tick interval, target filter, max agents, contour color, and a per-agent effect callback (deal damage / heal / set speed limit / apply buff).

Both helpers call `ContourHelpers.SafeSetContourColor` (never their own copy) and expose a single `Cleanup()` the composing power hooks to `OnDeactivate`/`OnMissionOver`. This is where "centralize contour bookkeeping" actually lands: exactly two code paths touch contours instead of ~40.

### 3.3 `ComposedPower` (the `PowerDefinition`)

A single new `HeroPowerDefBase`-derived class (`ComposedPowerDef : DurationMissionHeroPowerDefBase, IHeroPowerPassive, IHeroPowerActive`) with BLTConfigure-editable fields:

- `TargetMode` enum flags: Personal / Targeted / Aura / RangedOnly / AoE.
- `TriggerCondition`: OnHit / OnKill / HpThreshold (+ `HpThresholdFraction`).
- `StackMode`: Stack / Refresh / Independent.
- Togglable effect blocks (each an `ExpandableObject` with an `IsEnabled` like `AreaOfEffectDef`/`HitBehavior` already do): DoT block, Slow block, Damage-modifier block, Heal block, Courage/Fear block, Buff block. Disabled blocks contribute nothing.

`OnActivation` reads the enabled blocks and wires the corresponding helper(s) (`DamageOverTimeEffect` for DoT, `AuraSweepEffect` for Aura target mode, etc.) onto `handlers`, honoring `TargetMode`. This is the "list-of-lists" the streamer assembles from dropdowns + checkboxes. One config entry can now express Blizzard (Aura + Slow-block + Stack) or a bleed-poison AoE peasant (DoT bleed-block + DoT poison-block + AoE) without new code.

### 3.4 Interfacing with `AddDamagePower` (investigated — recommendation: extend, don't reimplement)

The collaborators want poison/bleed/burn/slow-on-hit to live *in* `AddDamageAbility`. Grounded finding: `AddDamagePower` already owns the on-hit surface (`OnDoDamage`, `OnDecideWeaponCollisionReaction`, `OnPostDoMeleeHit`, AoE) and already composes with other `AddDamagePower` instances because each is an independent power def on the same bus. **Recommendation:** add two optional `ExpandableObject` blocks to `AddDamagePower` — `DamageOverTime` (reusing `DamageOverTimeEffect`) and `SlowOnHit` — guarded by `IsEnabled` so existing configs are untouched (both default-disabled → identical serialization/behavior). This is strictly additive to a fork file and needs verification (Section 4 exit criteria) that it does not perturb the existing `AddDamageProgressionPatch` (`MakeBltGreatAgain.cs:3162`, applied manually in `BLTAurasModule.OnSubModuleLoad`).

**Two-track decision to sanity-check with Krzysiek (see Section 7):** the pilot can prove the compression *either* by (A) building `ComposedPowerDef` as a new MBGA power, *or* (B) extending `AddDamagePower` in the fork. They are complementary, but for the *first* revertable diff the recommendation is track A (MBGA-local, zero fork changes, trivially deletable) to prove `DamageOverTimeEffect`/`AuraSweepEffect` in isolation, then track B (fold DoT/Slow into `AddDamagePower`) as phase 2 once the helpers are proven. Both share the same helper code.

### 3.5 Where composed powers appear in the picker

Powers surface via `HeroPowerDefBase.ItemSourcePassive/Active` → `GlobalHeroPowerConfig.PowerDefs` (`.../GlobalConfigs/GlobalHeroPowerConfig.cs`). A `ComposedPowerDef` instance shows up automatically once addable. The "less intimidating dropdown" payoff is realized by *replacing* many single-purpose power TYPES with one `ComposedPowerDef` type the streamer configures — not by touching the dropdown widget itself.

## 4. Phased task breakdown

Every phase: small verified diff → build → deploy → in-battle verify → only then proceed. Each phase is independently shippable/revertable.

### Phase 1 — Pilot (DoT helper + one combo), MBGA-local, zero fork edits
1. Add `DamageOverTimeEffect` helper (extract from `PoisonStrikePower`), plus `TargetMode`/`TriggerCondition`/`StackMode` enums.
2. Add `AuraSweepEffect` helper (extract from `DamageAuraPower`).
3. Add `ComposedPowerDef : DurationMissionHeroPowerDefBase, IHeroPowerPassive` with DoT + Aura + Slow blocks. Register it so it appears in the powers picker under a **new Beta surface** (a new `GlobalHeroPowerConfig`-visible entry, or a clearly-labelled "MBGA Composed (Beta)" power type) that does **not** disturb existing power lists.
4. Author 4 composed defs matching today's Poison/DamageAura/Flame/Bleed tuning exactly, run **side-by-side** with the untouched originals on two heroes in one battle.
5. Author **Blizzard** (Aura + Slow-block + Stack) purely from the new pieces.

**Exit criterion:** new DoT defs produce damage/contour/timing indistinguishable from originals (no double-fire, no double-damage); Blizzard visibly slows + damages an enemy cluster; no `StackOverflow`, no `Collection was modified`, no crash-to-desktop across ≥3 consecutive battles incl. a tournament transition (the historical contour-crash trigger). If the engine fights the bus or reintroduces any Section 5 class → **stop and reassess**; the diff is deletable in one commit.

### Phase 2 — Broaden + fold into `AddDamagePower` (fork edits begin)
6. Add default-disabled `DamageOverTime` + `SlowOnHit` blocks to `AddDamagePower` (fork). Verify a legacy `AddDamagePower` config serializes/behaves identically (blocks off).
7. Migrate Frost/Vampirism/Burning strikes to `DamageOverTimeEffect` parameterizations (originals stay until each replacement is verified 1:1, then optionally retire per-power in a later diff).
8. Collapse the aura family onto `AuraSweepEffect` + a single `ComposedPowerDef` with per-effect toggles (heal/damage/curse/weakness/slow/battlecry/buff), originals retained side-by-side until verified.

**Exit criterion:** each converted power verified 1:1 against its original before the original is considered for retirement; `AddDamageProgressionPatch` still scales correctly; no regression across a full battle + tournament.

### Phase 3 — `!buyattributeall` / `!buyfocusall` (independent, parallelizable with Phase 1)
9. `BuyAttributesAll` — new command. Iterate `CampaignHelpers.AllAttributes`, skip any at cap (10), compute total cost = per-attribute cost × count-of-improvable (mirror `AttributePoints.IncreaseAttribute` at `AttributePoints.cs:53`), gold-check the total once, `AddAttribute(+1)` each improvable, deduct total via `ChangeHeroGold`. **Design note:** do *not* reuse `ImproveAdoptedHero`'s flat single-`GoldCost` deduction path (it deducts one `GoldCost`); model cost explicitly like `FocusPoints` does.
10. `BuyFocusAll` — new command mirroring `FocusPoints.cs`. Iterate `Skills.All` (18), skip skills at focus 5, cost += `settings.GetFocusCost(currentFocus)` per skill, gold-check total, `AddFocus(skill, 1)` each, deduct via `ChangeHeroGold`.
11. Register both via `ActionManager.RegisterAll`; add loc strings.

**Exit criterion:** `!buyattributeall` raises every sub-cap attribute by 1 and charges the summed cost, no-op with clear message when all capped/insufficient gold; `!buyfocusall` same for the 18 skills with tiered summing. Placement recommendation: fork `BLTAdoptAHero/Actions/` next to the single-stat originals (new files, additive).

### Phase 4 — Command duplication in BLTConfigure (tracked backlog, non-blocking)
12. Investigate whether BLTConfigure's power-duplication affordance derives from `HeroPowerDefBase.Clone()` (regenerates `ID`) and whether the command/action list UI can gain the same clone action (commands are a different collection than `PowerDefs`). Do not block 5.4.1 on this. Deliver a spike note: is the clone mechanism generic (list-item clone) or power-specific?

## 5. Risk / regression-avoidance map (each landmine → concrete guardrail)

1. **Self-recursive contour helper → StackOverflow → CTD, no report.** Guardrail: helpers call **only** `ContourHelpers.SafeSetContourColor` (`MakeBltGreatAgain.cs:341`); they never define a local `SafeSetContourColor`. `AuraSweepEffect`/`DamageOverTimeEffect` are the *only* contour writers in composed powers, so the recursion surface shrinks from ~40 call sites to 2. Code-review gate: grep the new files for any method named `SafeSetContourColor`.
2. **Lazy `Mission.AddMissionBehavior` from combat callbacks → `Collection was modified`.** Guardrail: the pilot adds **no** mission behavior at all — it rides the existing eagerly-registered bus. Hard rule in the plan: composed powers may only subscribe to `handlers`; any future behavior must be added in `OnMissionBehaviorInitialize`, never from a handler.
3. **`PatchAll()` fan-out across merged modules.** Guardrail: the pilot adds **zero** `[HarmonyPatch]` classes. If Phase 2's `AddDamagePower` extension ever needs a new patch, it must go through `MbgaPatchGuard.ShouldApply()` (`MakeBltGreatAgain.cs:362`) or be applied manually once in a module's `OnSubModuleLoad` (the pattern already used for `AddDamageProgressionPatch`), never via attribute + `PatchAll`.
4. **`$version$` MSBuild placeholder.** Guardrail: the 5.4.1 package's `SubModule.xml` must be taken from MSBuild-processed build output, never the raw source template. Add this as an explicit packaging checklist item for the release step.

## 6. Hard non-goals (scope boundary)

Teleport, Ironskin, Kick, Shadowstep (and other Group-3 bespoke powers: JumpAttack, Necromancy, Berserk, LastStand, Taunt) **stay bespoke and individually coded, indefinitely.** They do not share the DoT or aura-sweep mechanic and forcing them into a generic component set produces a meaningless catch-all. Only families with a genuinely repeated mechanic (DoT, aura pulse+radius, on-hit modifier, stat modifier) are converted.

## 7. Open questions for Krzysiek (flag before/at pilot)

1. **Pilot track order (new):** OK to prove the helpers first as an MBGA-local `ComposedPowerDef` (deletable in one commit), *then* fold DoT/Slow into the fork's `AddDamagePower` in Phase 2? Or do you want the `AddDamagePower` extension to be the very first thing (higher value, but edits a live fork file)?
2. Pilot cluster confirmed as DoT family (Poison/DamageAura/Flame/Bleed) + Blizzard combo — anything to add/swap?
3. Authoring UX: dropdown + checkbox `ExpandableObject` blocks in BLTConfigure acceptable (consistent with existing `AoE`/`HitBehavior` editors), or a different flow?
4. "Uprising of the Peasants" final name or placeholder?
5. `!buyattributeall`/`!buyfocusall` placement: fork `Actions/` folder next to originals (recommended) vs. MBGA file?

### Critical Files for Implementation
- `C:\Users\krzysiek\AppData\Local\Temp\claude\E--BLT\11ae1525-b531-445f-ba1a-99dd2e71215e\scratchpad\blt531_clone\BLTAdoptAHero\Powers\AddDamagePower.cs`
- `C:\Users\krzysiek\AppData\Local\Temp\claude\E--BLT\11ae1525-b531-445f-ba1a-99dd2e71215e\scratchpad\blt531_clone\BLTAdoptAHero\Behaviors\PowerHandler.cs`
- `C:\Users\krzysiek\AppData\Local\Temp\claude\E--BLT\11ae1525-b531-445f-ba1a-99dd2e71215e\scratchpad\blt531_clone\BLTAdoptAHero\Powers\Core\DurationMissionHeroPowerDefBase.cs`
- `E:\BLT\source_codes\MakeBltGreatAgain\source\MakeBltGreatAgain.cs` (helpers at 341/362; PoisonStrike 3201; DamageAura 3635; AddDamageProgressionPatch 3162)
- `C:\Users\krzysiek\AppData\Local\Temp\claude\E--BLT\11ae1525-b531-445f-ba1a-99dd2e71215e\scratchpad\blt531_clone\BLTAdoptAHero\Actions\{AttributePoints,FocusPoints}.cs`
