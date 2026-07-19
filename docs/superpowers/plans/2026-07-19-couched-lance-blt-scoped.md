# Plan: BLT-Scoped Couched-Lance Reward Mechanic (Approaches A & B)

## 0. Key discovery that reshapes the design

The hard part is already solved elsewhere in this file. `AdrenalineChargePatch` (`MakeBltGreatAgain.cs` ~line 1829) is a **fully working, shipped** `Mission.RegisterBlow` prefix that: captures the complete blow signature `(Agent attacker, Agent victim, WeakGameEntity realHitEntity, ref Blow b, ref AttackCollisionData collisionData, in MissionWeapon attackerWeapon)`, gates to `attacker.GetAdoptedHero() != null` (BLT-scoping), requires `attacker.HasMount`, checks a per-agent mission-behavior state, gates on a blow-type predicate (`IsChargeBlow` = `b.AttackType == AgentAttackType.Collision`), and then multiplies both `b.InflictedDamage`/`b.BaseMagnitude` **and** `collisionData.InflictedDamage`/`collisionData.BaseMagnitude`. It is registered **manually once** via `harmony.Patch(...)` in `OnSubModuleLoad` (~line 3127-3134), deliberately *not* via `[HarmonyPatch]`/`PatchAll`, to avoid the 9-module fan-out.

Both requested approaches are the **same patch shape as `AdrenalineChargePatch`, with a different gate.** Nothing new needs to be invented for the hook mechanism, damage application, contour flash, or dismount — all already exist in this file.

**The critical timing question for Approach A is answered by the decompiled source:** `IsDoingPassiveAttack` is still readable at blow-registration time.
- `AttackInformation.cs:330` reads `IsAttackerAgentDoingPassiveAttack = attackerAgent.IsDoingPassiveAttack` while building the attack-info struct **inside the same blow pipeline**.
- `Mission.cs:5269` reads `attacker.IsDoingPassiveAttack` inside `MeleeHitCallback`, and the `RegisterBlow` call for that same blow happens a few lines later at `Mission.cs:5308`. So during our `RegisterBlow` prefix the native flag is still live.
- The canonical engine test for "this is a couched-lance hit" is at `HighlightsController.cs:244`: `affectedAgent.HasMount && blow.AttackType == AgentAttackType.Standard && affectorAgent.HasMount && affectorAgent.IsDoingPassiveAttack`. Note the couch hit is `AttackType.Standard` (a weapon thrust), **not** `Collision` (the mount body-check that `AdrenalineChargePatch` keys on) — so this feature does not collide with Adrenaline charge.
- The braced-infantry variant is the same flag with `!HasMount` (`Mission.cs:5282-5289`, `ui_..._braced_polearm_damage`).

**Consequence:** Approach A is genuinely feasible read-only (no forcing, confirmed by exact call-site timing). Approach B is trivially feasible (it's `AdrenalineChargePatch` with a velocity+weapon gate instead of a Collision gate).

| Approach | Detection channel | Native field dependency | Feasible? |
|---|---|---|---|
| **A** — react to the real couch | `attacker.IsDoingPassiveAttack` read at `RegisterBlow` | `Agent.IsDoingPassiveAttack` (getter, `Agent.cs:702`) — read-only, confirmed live at hook time | **Yes** |
| **B** — our own charge mechanic | mount + `MovementVelocity` magnitude + polearm `WeaponClass`/`WeaponFlags.WideGrip` | `Agent.MovementVelocity` (`Agent.cs:682`), `WieldedWeapon.CurrentUsageItem.WeaponClass`/`.WeaponFlags` | **Yes** |

---

## 1. Native fields confirmed present (do not assume — these were grepped)

- `Agent.IsDoingPassiveAttack` — `Agent.cs:702`, `public bool ... => MBAPI.IMBAgent.GetIsDoingPassiveAttack(GetPtr())`. Read-only pass-through to native; **not** settable (this is the confirmed dead-end for forcing, but perfect for reading).
- `Agent.MovementVelocity` — `Agent.cs:682`, `public Vec2 MovementVelocity => MBAPI.IMBAgent.GetMovementVelocity(GetPtr())`. This is the rider's own velocity; use `.Length` for speed. (`Agent.Velocity` (Vec3) at `Agent.cs:1501` and `MountAgent` at `Agent.cs:995` are alternatives.)
- `Agent.HasMount` — `Agent.cs:716`. Already used by `AdrenalineChargePatch`.
- `WeaponFlags.WideGrip` — the actual engine marker for couch-capable lances; used at `AgentStatCalculateModel.cs:83` (`weapon.WeaponFlags.HasAllFlags(WeaponFlags.WideGrip)`). This is the most reliable "is a lance" test, better than enumerating `WeaponClass`.
- `WeaponClass.LowGripPolearm` — `CosmeticsManagerHelper.cs:132`; confirms the polearm sub-classes exist on `WeaponComponentData.WeaponClass` (a `TaleWorlds.Core` enum; not in this decompile but reachable via the fork's existing `WieldedWeapon.CurrentUsageItem`, already used at `MakeBltGreatAgain.cs:3304`, `4885`, `5106`).
- `AgentAttackType.Standard` / `AgentAttackType.Collision` — `Mission.cs:5441-5445`, `HighlightsController.cs:244`. Couch = `Standard`; mount-body charge = `Collision`.
- `Agent.KillInfo.CouchedLance` — `Agent.cs:360`. **Rejected as a detection channel:** `KillInfo` is computed native-side and only handed to `MBAPI.IMBAgent.Die(...)` (`Agent.cs:4542`); it is not exposed on the `Blow`/`collisionData` we see at `RegisterBlow`, so it is not usable pre-death. Use `IsDoingPassiveAttack` instead.

---

## 2. Approach A — react to the real native couch flag (recommended primary)

### 2.1 Feasibility
Confirmed feasible, read-only, zero forcing. We add a `RegisterBlow` prefix (or extend the pattern) that fires the extra effect only when the engine itself says the hero is mid-couch.

### 2.2 The gate (models `HighlightsController.cs:244` exactly)
Inside a prefix with the same signature as `AdrenalineChargePatch.Prefix`:
1. `attacker != null && attacker.IsActive()`
2. `attacker.GetAdoptedHero() != null` — **BLT scoping** (identical to `AdrenalineChargePatch` line 1842).
3. `attacker.IsDoingPassiveAttack` — the real native couch/brace flag.
4. `b.AttackType == AgentAttackType.Standard` — excludes the mount-body `Collision` case (which is Adrenaline's domain).
5. Optional split: `attacker.HasMount` → mounted couched lance; `!attacker.HasMount` → braced polearm (mirrors `Mission.cs:5271` vs `5282`). Expose both or gate to mounted-only via config.

### 2.3 The reward (all mechanisms already exist in-file)
- **Bonus damage:** multiply `b.BaseMagnitude`/`b.InflictedDamage` and `collisionData.*` exactly as `AdrenalineChargePatch` lines 1847-1851.
- **Guaranteed / boosted dismount:** reuse the fork's `HitBehavior` struct with `DismountChance`, then `blow.BlowFlag = hitBehavior.AddFlags(victim, blow.BlowFlag)` — the exact pattern already used at `MakeBltGreatAgain.cs:3334-3335`. (In a `RegisterBlow` prefix, mutate `b.BlowFlag` before the original runs.)
- **Contour flash:** `SafeSetContourColor(victim, color, true)` (used throughout, e.g. line 3309). Because it is a momentary visual, schedule clear via the existing `OnSlowTick` tracked-dict pattern (as `DamageOverTimeEffect` does) or a short timed set; do **not** hand-roll a new contour helper (contour self-recursion is a named regression class — see the comment at ~3258).

### 2.4 How it hooks into the existing pattern
Two viable wirings — pick based on whether this should be a global toggle or a per-hero power:

- **(A1) Global config patch** — clone `AdrenalineChargePatch`: new `internal static class CouchedLanceRewardPatch` with a `public static void Prefix(...)` full signature, registered manually once in `OnSubModuleLoad` right beside the Adrenaline registration (~line 3127) via `harmony.Patch(chargeTarget-equivalent, prefix: ...)`. Gate reads a global `AdrenalineGlobalConfig`-style config. This matches how Adrenaline charge already ships.
- **(A2) Per-hero power** — a `ComposedX`-style power that, in `OnActivation`, registers the hero into a mission-global `Dictionary<Agent,float>` (identical to `ComposedAuraDamageTracker`, `MakeBltGreatAgain.cs:1541`), and a single shared `[HarmonyPatch] RegisterBlow` prefix reads that tracker with an `IsEmpty` O(1) early-out. This is the cleaner fit for a per-viewer power and reuses the exact tracker+patch idiom just built for the aura buff/debuff feature. **Recommended** if this is meant to be a purchasable/assignable hero power rather than a global rule.

Note: `PowerHandler.Handlers.OnDoDamage` alone is **not** sufficient here — it fires only for the owning hero pair and does not pass a mutable `ref Blow`. The `RegisterBlow` prefix is required to actually change damage/flags. `OnDoDamage`/`OnSlowTick` can still be used for bookkeeping (e.g. contour-clear scheduling), same division of labor as `DamageOverTimeEffect`.

### 2.5 Main risk / uncertainty
- **Flag semantics breadth:** `IsDoingPassiveAttack` is true for both couched lance (mounted) and braced polearm (infantry) — that is a feature here, but the config text must say so. Confirmed by `Mission.cs:5271` vs `5282`.
- **Rarity:** the real couch pose triggers infrequently in normal play (mount at speed, low-grip lance, correct approach). The reward will feel rare-but-spectacular; that is the intended "react, don't force" behavior. If the user later finds it too rare, that is the argument for also shipping Approach B.
- **`AttackType` for braced infantry:** `HighlightsController` only special-cases the mounted `Standard` case; verify empirically whether infantry brace hits also arrive as `Standard` at `RegisterBlow` (they should, per `Mission.cs:5282` sharing the same `!IsAlternativeAttack && IsDoingPassiveAttack` branch). If ambiguous, gate mounted-only in Phase 1.

---

## 3. Approach B — our own parallel "charge bonus" mechanic (recommended as complementary)

### 3.1 Feasibility
Trivially feasible; it is `AdrenalineChargePatch` with the gate swapped. Fully within existing patterns, zero dependency on the native couch flag.

### 3.2 The gate (detect the ingredients ourselves)
1. `attacker != null && attacker.IsActive() && attacker.GetAdoptedHero() != null` — BLT scoping.
2. `attacker.HasMount` (mounted lance) — or make optional for a braced-infantry variant.
3. Speed: `attacker.MovementVelocity.Length >= threshold` (`Agent.cs:682`). Tunable config float; couch in vanilla wants real speed, so a threshold around a canter/gallop value (verify empirically).
4. Weapon is a lance/polearm: prefer `attacker.WieldedWeapon.CurrentUsageItem` non-null and `CurrentUsageItem.WeaponFlags.HasAnyFlag(WeaponFlags.WideGrip)` (the engine's own couch marker, `AgentStatCalculateModel.cs:83`); fall back to `CurrentUsageItem.WeaponClass` being a polearm class (`OneHandedPolearm`/`TwoHandedPolearm`/`LowGripPolearm`). Use the existing `WieldedWeapon.CurrentUsageItem?.` access idiom (`MakeBltGreatAgain.cs:3304`).
5. `b.AttackType == AgentAttackType.Standard` (a thrust, not a `Collision` body-check) and ideally a thrust direction — optional refinement.

### 3.3 The reward
Identical toolset to §2.3 (damage multiply on `b`+`collisionData`, `HitBehavior.DismountChance`, `SafeSetContourColor`).

### 3.4 Hook
Same two wiring options as §2.4. Because Approach B is entirely self-computed, the **global-config clone of `AdrenalineChargePatch` (B1)** is the most natural and least code.

### 3.5 Main risk / uncertainty
- **False positives / double-dip:** a self-computed charge bonus can stack with the real engine couch bonus and with Adrenaline's `Collision` bonus. Because B keys on `Standard` and Adrenaline keys on `Collision`, they will not double-fire on the same blow, but B *will* also fire on ordinary fast lance thrusts that the engine did not treat as couched. That is the point (more frequent, more forgiving) but must be documented, and the multiplier should be smaller than Approach A's to keep the "real couch" more rewarding.
- **Threshold tuning is empirical:** `MovementVelocity` units and a good gallop threshold must be measured in-game; ship with a conservative default and a config field.
- **Weapon detection edge cases:** thrown polearms / couch-incapable polearms — `WideGrip` is the safest single test; enumerating `WeaponClass` risks including non-couchable spears.

---

## 4. Recommendation

**Build both, phased, A first.** They are complementary, not competing:
- **Approach A** is the "authentic, dramatic, rare" reward — it is honest (reacts to the engine's real state) and cheap. It is the right *primary* because it can never feel fake.
- **Approach B** is the "reliable, tunable, frequent" reward, for when the user finds A too rare or wants a couch-style bonus on hero builds that never trigger the native pose. Ship it as a separate config/power with a **smaller** multiplier so A stays the marquee moment.

Both are the `AdrenalineChargePatch` shape, so incremental risk is low and each phase is independently buildable/deployable.

---

## 5. Phased implementation

Each step builds `source/MakeBltGreatAgain.cs` in Release (per the sibling repo's convention: `dotnet build ... -c Release -p:DefineConstants=BLT_1315`, expect zero errors) and deploys `MakeBltGreatAgain.dll` to the `BLTAdoptAHero\bin\Win64_Shipping_Client` module folder, then verify in a real battle.

**Phase 1 — Approach A, damage only, global config, mounted-only.**
Clone `AdrenalineChargePatch` → `CouchedLanceRewardPatch` with the §2.2 gate (require `IsDoingPassiveAttack`, `HasMount`, `AttackType.Standard`, adopted hero). Apply only a damage multiplier from a new `CouchedLanceConfig` (model on `AdrenalineGlobalConfig.Get()` + `Enabled`). Register manually once in `OnSubModuleLoad` beside the Adrenaline `harmony.Patch` block (~line 3127). *Verify:* mount a BLT hero with a lance, land a real couched hit (watch for the vanilla `ui_delivered_couched_lance_damage` message from `Mission.cs:5275` as ground-truth that the flag was set) — confirm extra damage lands only on those hits and normal thrusts are unaffected; confirm no new log exceptions.

**Phase 2 — Approach A, dismount + contour flash.**
Add `HitBehavior { DismountChance }` via `AddFlags` on `b.BlowFlag` (pattern at line 3334) and a brief `SafeSetContourColor` on the victim, cleared via an `OnSlowTick` tracked-dict (pattern in `DamageOverTimeEffect`). *Verify:* couched hits now dismount/flash; walk away / end battle / next battle shows no lingering contour.

**Phase 3 — Approach A, braced-infantry toggle.**
Add a config bool to also reward `!HasMount` couch (`Mission.cs:5282` braced-polearm case). *Verify:* dismounted lancer bracing produces the reward when the `braced_polearm` message fires.

**Phase 4 — Approach B, self-computed charge bonus.**
New `CouchStyleChargePatch` (clone of `AdrenalineChargePatch`) with the §3.2 gate (`HasMount` + `MovementVelocity.Length >= threshold` + `WeaponFlags.WideGrip`/polearm `WeaponClass` + `AttackType.Standard`). Separate config with its own (smaller) multiplier + threshold. *Verify:* fast lance thrusts that did **not** trigger the vanilla couch message still get the (smaller) bonus; tune threshold; confirm it does not double-apply with Adrenaline (`Collision`) or with A on the same blow.

**Phase 5 — optional per-hero power form.**
If this should be an assignable viewer power rather than a global rule, refactor to the `ComposedAuraDamageTracker`-style mission-global `Dictionary<Agent,float>` + single shared `[HarmonyPatch]` prefix with `IsEmpty` early-out (idiom at `MakeBltGreatAgain.cs:1541-1579`), wired from a `Composed…Power.OnActivation` using `hero.GetAgent()` and cleared on `OnMissionOver`.

---

## 6. Guardrail mapping (explicit — same four regression classes as the aura plan)

1. **`PatchAll()` fan-out.** Both new patches are registered **manually once** via `harmony.Patch` in the single `OnSubModuleLoad` (like Adrenaline/AddDamage/HumanChild), OR discovered by the single already-guarded `PatchAll()` (`MbgaPatchGuard.ShouldApply()`) if using the `[HarmonyPatch]` tracker form. No new `Harmony` instance, no extra `PatchAll`.
2. **Contour self-recursion.** Only `SafeSetContourColor` is used; no new contour helper is defined.
3. **Lazy mid-combat `AddMissionBehavior`.** No new `MissionBehavior`; state (if any) is a plain static `Dictionary` on the single-threaded game loop, mirroring `ComposedAuraDamageTracker`.
4. **`RegisterBlow` per-blow cost.** First lines are cheap early-outs (config `Enabled`/`IsEmpty`, `attacker == null`, `GetAdoptedHero() == null`) before any `IsDoingPassiveAttack`/velocity read — identical to `AdrenalineChargePatch`.

---

## 7. Open questions to resolve during implementation

- Exact `MovementVelocity` gallop threshold for Approach B (measure in-game).
- Whether infantry brace hits arrive as `AttackType.Standard` at `RegisterBlow` (Phase 3; gate mounted-only until confirmed).
- Multiplier balance so Approach A (real couch) stays more rewarding than Approach B (self-computed).
- Whether `WeaponFlags.WideGrip` alone is the right lance test vs. also allowing specific `WeaponClass` polearm values (verify against the mod's actual lance items).
- Confirm `collisionData` mutation is honored the same way `AdrenalineChargePatch` relies on (it already ships doing this, so low risk).

---

### Critical Files for Implementation
- E:\BLT\source_codes\RebellionOfThePeasants\source\MakeBltGreatAgain.cs (all edits: clone `AdrenalineChargePatch` ~1829; manual `harmony.Patch` registration ~3127; reuse `ComposedAuraDamageTracker`/`ComposedAuraDamagePatch` ~1541-1579; reward idioms `HitBehavior.AddFlags` ~3334 and `SafeSetContourColor` ~3309)
- C:\Users\krzysiek\AppData\Local\Temp\claude\E--BLT\11ae1525-b531-445f-ba1a-99dd2e71215e\scratchpad\mb_decompiled\TaleWorlds.MountAndBlade\Agent.cs (`IsDoingPassiveAttack` :702, `MovementVelocity` :682, `HasMount` :716, `KillInfo.CouchedLance` :360)
- C:\Users\krzysiek\AppData\Local\Temp\claude\E--BLT\11ae1525-b531-445f-ba1a-99dd2e71215e\scratchpad\mb_decompiled\TaleWorlds.MountAndBlade\HighlightsController.cs (:244 canonical couch-hit test; :201 mounted-charge test)
- C:\Users\krzysiek\AppData\Local\Temp\claude\E--BLT\11ae1525-b531-445f-ba1a-99dd2e71215e\scratchpad\mb_decompiled\TaleWorlds.MountAndBlade\Mission.cs (:5269-5289 couch/brace message branch; :5308 `RegisterBlow` call proving flag timing; :5441-5445 `AttackType` assignment)
- C:\Users\krzysiek\AppData\Local\Temp\claude\E--BLT\11ae1525-b531-445f-ba1a-99dd2e71215e\scratchpad\mb_decompiled\TaleWorlds.MountAndBlade\AgentStatCalculateModel.cs (:83 `WeaponFlags.WideGrip` = engine lance marker for Approach B)
