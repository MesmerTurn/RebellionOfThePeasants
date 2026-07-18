# Plan: Real Buff/Debuff for `ComposedAuraPower` (damage + armor on all agents, troops included)

## 0. Key discovery that reshapes the design

This codebase **already contains a working mechanism that applies stat modifiers to arbitrary agents, including regular troops, with no Harmony patch at all.**

- `MakeBltGreatAgain.cs` line ~4575 has a full, shipped `BuffAuraPower` class (section "9. BUFF AURA"). Its `ApplyBuff`/`RemoveBuff` (lines ~4651-4678) call `BLTAgentModifierBehavior.Current?.Add(agent, config)` with an `AgentModifierConfig` built from `DrivenProperty` entries (`ArmorHead`, `MaxSpeedMultiplier`, `SwingSpeedMultiplier`, `ThrustOrRangedReadySpeedMultiplier`).
- `BLTAgentModifierBehavior` is fork infrastructure (`BannerlordTwitch/Behaviors/BLTAgentModifierBehavior.cs`). It is an `AutoMissionBehavior` (self-registering, already alive every mission), operates on `agent.AgentDrivenProperties`, caches each agent's base driven-property array, restores-then-reapplies the active modifier stack every 2s, and **already purges dead agents itself** in `OnAgentDeleted` and in `UpdateAgents` (`!kv.Key.IsActive()`).
- `PropertyModifierDef.Apply(property) => property * ModifierPercent/100 + Add` (`BannerlordTwitch/Helpers/PropertyModifierDef.cs`). A debuff is just `ModifierPercent = 100 + bonus` with a negative `bonus` (e.g. -30% armor -> `ModifierPercent = 70`). No separate "negation" config is needed - you remove a buff by calling `BLTAgentModifierBehavior.Remove(agent, sameConfigReference)` and the behavior restores base and reapplies the remaining stack.

**Consequence for the two requested stats:**

| Requested stat | Mechanism | Needs a Harmony patch? |
|---|---|---|
| **Armor** buff/debuff | `DrivenProperty.ArmorHead/ArmorTorso/ArmorArms/ArmorLegs` via `BLTAgentModifierBehavior` (existing infra) | **No** |
| **Damage output** buff/debuff | No damage-output `DrivenProperty` exists -> must intercept the buffed agent's own outgoing blows via `Mission.RegisterBlow` prefix | **Yes - one prefix** |

So the correct minimal design is a **hybrid**: armor rides the existing driven-property infrastructure (zero new patch, zero new behavior, zero contour code), and only the damage half needs a single `Mission.RegisterBlow` prefix modeled verbatim on `MBGAPrestigeStatPatches.DamagePrefix` (lines ~1498-1515). This deliberately avoids patching `Agent.GetBaseArmorEffectivenessForBodyPart` (the exact getter whose patching broke character-creation preview and is the reason `MBGAPrestigeStatPatches` needs its `Prepare()` gate - see the Polish comment at line ~1472).

`DrivenProperty` names verified via reflection against the live game DLL (`TaleWorlds.Core.dll`): `ArmorHead`, `ArmorTorso`, `ArmorLegs`, `ArmorArms` all confirmed present.

---

## 1. Tracking mechanism

Two independent tracking stores, both driven by the aura's per-tick enter/exit reconciliation:

**Armor (existing infra, per-aura-instance):** a local `Dictionary<Agent, AgentModifierConfig> armorConfigs` living inside the aura's `OnActivation` closure. On enter: build one `AgentModifierConfig`, `BLTAgentModifierBehavior.Current?.Add(agent, config)`, store the reference. On exit: `BLTAgentModifierBehavior.Current?.Remove(agent, config)` with that **same reference**, drop the dict entry. (Note: the existing `BuffAuraPower.RemoveBuff` instead `Add`s an inverse config - that is redundant/wrong given the cache-and-reapply model in `UpdateAgents`; the new code must use `Remove(agent, sameConfig)` and not copy that bug.)

**Damage (new static store, mission-global):** a single static dictionary the `RegisterBlow` prefix can read:

```csharp
internal static class ComposedAuraDamageTracker
{
    private static readonly Dictionary<Agent, float> bonuses = new();   // agent -> bonus percent
    public static bool IsEmpty => bonuses.Count == 0;
    public static void Set(Agent a, float pct) { if (a != null) bonuses[a] = pct; }
    public static void Remove(Agent a) { if (a != null) bonuses.Remove(a); }
    public static bool TryGet(Agent a, out float pct) => bonuses.TryGetValue(a, out pct);
    public static void Clear() => bonuses.Clear();
}
```

Single-threaded game loop, so plain `Dictionary` is correct (no locking needed). The aura's enter/exit callbacks `Set`/`Remove`, and `OnMissionOver` calls `Clear()`.

**Handoff from the sweep:** `AuraSweepEffect` (line ~3322) already computes `nowInRange` each tick and maintains `lastInRange`, and already reconciles them for contour. Extend it with two optional callbacks so `ComposedAuraPower` can hook entry/exit **using the exact agents the aura already selected** (same `TargetEnemies` / `IncludeSelf` / `Radius` / `MaxAgentsPerTick` filter - no new targeting invented):

```csharp
// new optional ctor params, default null:
Action<Agent> onAgentEnter = null, Action<Agent> onAgentExit = null
```

Wire inside `Wire`:
- hero-inactive early branch and `Cleanup()`: call `onAgentExit` for every agent in `lastInRange` before clearing.
- exit loop: where an agent in `lastInRange` is not in `nowInRange`, call `onAgentExit(a)`.
- after `nowInRange` is built: for each `a` in `nowInRange` not in `lastInRange`, call `onAgentEnter(a)`.

This keeps `AuraSweepEffect` generic; the existing `DamageOverTime`/`Heal`/`Slow` callers pass nothing and are unaffected.

---

## 2. The patch itself

One new class, mirroring `MBGAPrestigeStatPatches.DamagePrefix` exactly - same `(Agent attacker, ref Blow b)` signature (Harmony binds by parameter name; both existing `RegisterBlow` prefixes use precisely `attacker` and `b`, so reuse those names verbatim), same try/catch-and-`Log.Exception` shape:

```csharp
[HarmonyPatch]
internal static class ComposedAuraDamagePatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Mission), "RegisterBlow")]
    private static void DamagePrefix(Agent attacker, ref Blow b)
    {
        try
        {
            if (ComposedAuraDamageTracker.IsEmpty) return;          // O(1) early-out, zero cost when unused
            if (attacker == null || !attacker.IsActive()) return;
            if (!ComposedAuraDamageTracker.TryGet(attacker, out float pct)) return;
            float mult = 1f + pct / 100f;
            if (mult < 0f) mult = 0f;                               // clamp so a -150% debuff can't flip sign
            b.BaseMagnitude *= mult;
            b.InflictedDamage = (int)(b.InflictedDamage * mult);
        }
        catch (Exception ex) { Log.Exception("ComposedAuraDamagePatch.DamagePrefix failed", ex); }
    }
}
```

**No `Prepare()` gate here - deliberate, and this is a grounded deviation from the `MBGAPrestigeStatPatches` template.** `Prepare()` runs once at `PatchAll()` time (`OnSubModuleLoad`), long before any mission or class assignment exists, and `MBGAPrestige` can gate on it only because it keys off a global config `Enabled` flag. An aura buff is assigned per-class and only becomes "active" mid-mission, so there is nothing for `Prepare()` to read at load time. The `IsEmpty` early-out is the functional equivalent of "costs nothing when unused": when no aura-buff power is active the dictionary is empty and the prefix returns after a single `Count == 0` comparison. Additionally, because this patch is on `RegisterBlow` and **not** on the `BaseHealthLimit`/`GetBaseArmorEffectivenessForBodyPart` getters, the character-creation-preview breakage that forced `MBGAPrestige`'s `Prepare()` gate cannot occur here at all.

Multiple `RegisterBlow` prefixes coexist fine - this becomes the third alongside the two Prestige/Tier78 ones; Harmony runs all prefixes and they compose multiplicatively on `b`.

---

## 3. New fields on `ComposedAuraPower`

Add two fields after the existing dismount field, reusing the aura's existing targeting rather than adding any:

```csharp
[DisplayName("Buff: Target Damage Bonus (%)"),
 Description("Percentage change to the OUTGOING damage of every agent caught in this aura (uses the same targeting as the aura: enemies if Target Enemies is on, allies otherwise, incl. self per Include Self). Positive = buff (Commander-style), negative = debuff (Curse-style). 0 = disabled. Applies to regular troops too."),
 PropertyOrder(14), UsedImplicitly]
public float TargetDamageBonusPercent { get; set; } = 0f;

[DisplayName("Buff: Target Armor Bonus (%)"),
 Description("Percentage change to the armor effectiveness of every agent caught in this aura (same targeting as above). Positive = tankier, negative = softer (Curse-style). 0 = disabled. Applies to regular troops too."),
 PropertyOrder(15), UsedImplicitly]
public float TargetArmorBonusPercent { get; set; } = 0f;
```

Targeting is **entirely reused**: whatever `TargetEnemies` / `IncludeSelf` / `Radius` / `MaxAgentsPerTick` already select is exactly the buffed/debuffed set. A "Commander" aura = `TargetEnemies` off, `TargetDamageBonusPercent = +25`. A "Curse" aura = `TargetEnemies` on, `TargetArmorBonusPercent = -30` (and/or negative damage). `MaxAgentsPerTick` caps the buffed set to the N closest - same as the existing damage/heal caps.

Wiring in `OnActivation`:

```csharp
float dmgBonus   = TargetDamageBonusPercent;
float armorBonus = TargetArmorBonusPercent;
var armorConfigs = new Dictionary<Agent, AgentModifierConfig>();

void OnAgentEnter(Agent a)
{
    if (dmgBonus != 0f) ComposedAuraDamageTracker.Set(a, dmgBonus);
    if (armorBonus != 0f)
    {
        try
        {
            var cfg = new AgentModifierConfig();
            float pct = 100f + armorBonus;
            cfg.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ArmorHead,  ModifierPercent = pct });
            cfg.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ArmorTorso, ModifierPercent = pct });
            cfg.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ArmorArms,  ModifierPercent = pct });
            cfg.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ArmorLegs,  ModifierPercent = pct });
            BLTAgentModifierBehavior.Current?.Add(a, cfg);
            armorConfigs[a] = cfg;
        }
        catch { }
    }
}

void OnAgentExit(Agent a)
{
    if (dmgBonus != 0f) ComposedAuraDamageTracker.Remove(a);
    if (armorConfigs.TryGetValue(a, out var cfg))
    {
        try { BLTAgentModifierBehavior.Current?.Remove(a, cfg); } catch { }
        armorConfigs.Remove(a);
    }
}
```

Pass `OnAgentEnter`/`OnAgentExit` into the extended `AuraSweepEffect` ctor, and add `handlers.OnMissionOver += ComposedAuraDamageTracker.Clear;` alongside the existing `aura.Cleanup` registration. Update the `Description` getter to append buff/debuff text.

---

## 4. Cleanup correctness

- **Agent leaves radius:** next tick it is absent from `nowInRange` -> `AuraSweepEffect` exit loop fires `OnAgentExit` -> damage-tracker entry removed and armor `Remove(agent, cfg)` called (base restored by `BLTAgentModifierBehavior.UpdateAgents`).
- **Agent dies / is deleted:** (a) armor - `BLTAgentModifierBehavior.OnAgentDeleted` purges it, and its `UpdateAgents` also drops `!IsActive()` agents, so no leak even if our exit is missed; (b) damage - a dead agent fails the `IsActive()` filter in `GetNearbyAgents`, so it drops out of `nowInRange` and triggers `OnAgentExit`; belt-and-suspenders, the `RegisterBlow` prefix itself guards `attacker.IsActive()`, and `OnMissionOver` `Clear()` wipes any residue.
- **Aura deactivates / hero agent goes inactive / mission ends:** `AuraSweepEffect.Cleanup` and the inactive-hero branch both now call `OnAgentExit` for every `lastInRange` agent, and `OnMissionOver` additionally calls `ComposedAuraDamageTracker.Clear()`. No entry survives into the next mission.
- **Overlapping auras (two heroes buffing the same troop):** the damage tracker is `Set`-per-agent, so it is last-writer-wins per tick (they do not sum). Acceptable for a beta; flagged as an open question if summing is desired.

---

## 5. Phased implementation (small, buildable, deployable steps)

Each step: `dotnet build source/MakeBltGreatAgain.csproj -c Release -p:DefineConstants=BLT_1315 --nologo -v:minimal` (expect `Liczba błędów: 0`), then copy `MakeBltGreatAgain.dll` to `C:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord\Modules\BLTAdoptAHero\bin\Win64_Shipping_Client\MakeBltGreatAgain.dll`.

**Phase 1 - inert patch + tracker.** Add `ComposedAuraDamageTracker` and `ComposedAuraDamagePatch` only. No aura wiring, so the tracker is always empty and the prefix is a no-op. Build/deploy. *In-game verify:* start a battle, confirm combat damage is unchanged and there are no new log exceptions.

**Phase 2 - extend `AuraSweepEffect`.** Add the two optional `onAgentEnter`/`onAgentExit` params and wire them into the enter loop, exit loop, inactive-hero branch, and `Cleanup`. All existing callers pass nothing. Build/deploy. *In-game verify:* an existing Composed Aura (damage/heal/slow) still behaves identically.

**Phase 3 - new fields + wiring.** Add `TargetDamageBonusPercent` / `TargetArmorBonusPercent` to `ComposedAuraPower`, the `OnAgentEnter`/`OnAgentExit` closures, pass them into the aura, add `OnMissionOver += ComposedAuraDamageTracker.Clear`. Build/deploy. *In-game verify:* (a) "Commander" - `TargetEnemies` off, +50% damage: nearby allied troops visibly hit harder; (b) "Curse" - `TargetEnemies` on, -40% armor: nearby enemy troops die faster; (c) walk the hero away / end the battle / let buffed troops die, then start a fresh battle and confirm no lingering damage or armor changes on anyone.

**Phase 4 - polish.** Confirm negative clamp, update the `Description` string, and (optional) `ScaleFloat` the two new fields.

---

## 6. Guardrail mapping (explicit)

1. **`PatchAll()` fan-out (`MbgaPatchGuard`).** The new `[HarmonyPatch] ComposedAuraDamagePatch` is discovered by the **same single guarded `PatchAll()`** already gated by `MbgaPatchGuard.ShouldApply()`. We add **no** new `harmony.PatchAll()` call and **no** new `Harmony` instance.
2. **Self-recursive contour helper.** This feature touches **no contour code**. The armor and damage paths never call any `SetContourColor`.
3. **Lazy `MissionBehavior` registration mid-combat.** We register **no new `MissionBehavior`**. Armor uses the fork's already-alive self-registering `BLTAgentModifierBehavior`; the damage store is a plain static `Dictionary`, not a behavior.
4. **Performance on `RegisterBlow` (fires for every blow).** First line is `if (ComposedAuraDamageTracker.IsEmpty) return;` - O(1) check; when active, a single `Dictionary.TryGetValue`.

---

## 7. Open questions / verified during implementation

- `DrivenProperty.ArmorHead/ArmorTorso/ArmorArms/ArmorLegs` - **VERIFIED** via reflection against `TaleWorlds.Core.dll`, all 4 present.
- Whether `AgentDrivenProperties` armor modification actually reduces incoming damage as intended - verify empirically in Phase 3.
- `Mission.RegisterBlow` signature stability - both existing prefixes capture exactly `(Agent attacker, ref Blow b)` and work; parameter names `attacker`/`b` are load-bearing for Harmony injection, do not rename.
- Overlapping damage auras do not stack (last-writer-wins) - acceptable for beta, flag to user if summing wanted later.
- Relationship to existing `BuffAuraPower` (section 9) - it already does something similar but mislabels `SwingSpeedMultiplier` as "Damage Bonus (%)" (that's attack speed, not damage output) and only touches `ArmorHead`. Out of scope here.
- `BLTAgentModifierBehavior.Current` can be null during some mission transition states - all calls use `?.` so this is a no-op when null.

### Critical Files
- `E:\BLT\source_codes\MakeBltGreatAgain\source\MakeBltGreatAgain.cs` (all edits: `AuraSweepEffect` ~3322, `ComposedAuraPower` ~3464, new `ComposedAuraDamageTracker` + `ComposedAuraDamagePatch`, reference patch `MBGAPrestigeStatPatches` ~1470)
- `blt531_clone\BannerlordTwitch\Behaviors\BLTAgentModifierBehavior.cs` (armor mechanism - Add/Remove/OnAgentDeleted semantics)
- `blt531_clone\BannerlordTwitch\Helpers\PropertyModifierDef.cs` (`Apply` = `property * ModifierPercent/100 + Add`)
- `blt531_clone\BLTAdoptAHero\Behaviors\PowerHandler.cs` (per-hero bus constraint - why this needs the sweep + patch instead of riding OnDoDamage/OnTakeDamage)
