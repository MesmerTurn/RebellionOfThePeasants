# Perk System Expansion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add 6 new perk branches (Cleave, Ignore Armor, Cut Through, Shrug Off, ranged AOE,
One-Handed Weapon Mastery) and 4 cross-branch hybrid capstones to `PeasantRebellionPerks.dll`,
replacing the single-`RequiredPerkKey` prerequisite model with a multi-entry `Requirements` list
so capstones can require ranks in two different branches at once.

**Architecture:** All new branches follow the exact patterns the file already uses for its 12
existing branches - a `[HarmonyPatch(typeof(MissionCombatMechanicsHelper), "ComputeBlowDamage")]`
prefix/postfix for anything that modifies a single hit's damage (Ignore Armor, Cut Through,
Cleave, ranged AOE all extend this), and `PerkStatMissionBehavior.BuildConfig`'s
`AgentModifierConfig`/`DrivenProperty` list for continuous stat bonuses (One-Handed Mastery). No
new mission behaviors, no new Harmony patch targets beyond one (Shrug Off, if research confirms
it needs its own hook - see Task 4).

**Tech Stack:** C#/.NET Framework 4.8, Harmony 2.x (manual patch registration in
`PeasantRebellionPerksModule.OnSubModuleLoad`, not `PatchAll` - this project's fork has a known
`PatchAll` duplicate-patch bug across multiple `MBSubModuleBase`s in one DLL, already worked
around here), no test framework - verification is `dotnet build` + reflection-confirm the built
DLL's types load, matching every prior plan in this project's history.

**Spec:** `docs/superpowers/specs/2026-08-12-perk-system-expansion-design.md`

---

## Before you start

All perk code lives in `E:\BLT\source_codes\RebellionOfThePeasants\source_perks\PeasantRebellionPerks.cs`
(~660 lines as of this plan - class list: `PerkDef`, `PerkGlobalConfig`, `PerkCatalog`,
`PerkHeroRecord`, `PerkBehavior`, `PeasantRebellionPerksModule`, `PerkService`,
`PerkDamageEvadePatch`, `PerkHealthLimitPatch`, `PerkRegenMissionBehavior`,
`PerkLootMissionBehavior`, `PerkStatMissionBehavior`, `PerkCommand`). This is a **standalone
module**, independent of `MakeBltGreatAgain.dll` - it only depends on `BannerlordTwitch.dll` and
`BLTAdoptAHero.dll`. Per `PACKAGING.md` in this repo, its built DLL ships inside
`BLTAdoptAHero\bin\Win64_Shipping_Client\`, not its own top-level module folder - already true of
the live install, no packaging changes needed by this plan.

Build command (game must be closed - the DLL is locked while Bannerlord is running):

```bash
cd "E:/BLT/source_codes/RebellionOfThePeasants/source_perks"
dotnet build PeasantRebellionPerks.csproj -c Release
```

Expected: `Kompilacja powiodła się. Liczba błędów: 0` (or `Build succeeded. 0 Error(s)` depending
on locale). Deploy after each successful build:

```bash
cp "bin/Release/net48/PeasantRebellionPerks.dll" "C:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord/Modules/BLTAdoptAHero/bin/Win64_Shipping_Client/PeasantRebellionPerks.dll"
cp "bin/Release/net48/PeasantRebellionPerks.dll" "E:/BLT/source_codes/RebellionOfThePeasants/bin_perks/Win64_Shipping_Client/PeasantRebellionPerks.dll"
```

The user (Krzysiek) does not compile or deploy this project themselves - that is the implementing
agent's job end-to-end, same as every prior session in this project's history.

---

### Task 1: Data model — `RequiredPerkKey` → `Requirements` list

**Files:**
- Modify: `E:\BLT\source_codes\RebellionOfThePeasants\source_perks\PeasantRebellionPerks.cs`

- [ ] **Step 1: Read the current `PerkDef` and `PerkService.CanBuy` in full**

```bash
grep -n "public class PerkDef" -A 32 "E:/BLT/source_codes/RebellionOfThePeasants/source_perks/PeasantRebellionPerks.cs"
grep -n "public static bool CanBuy" -A 25 "E:/BLT/source_codes/RebellionOfThePeasants/source_perks/PeasantRebellionPerks.cs"
```

Confirm they match what's quoted in Step 2/3 below exactly (line numbers will have drifted from
this plan's writing time - match by content, not line number).

- [ ] **Step 2: Replace `RequiredPerkKey`/`RequiredRank` with a `Requirements` list**

Find in `PerkDef` (around line 44-73):

```csharp
        [DisplayName("Required Perk Key"), Description("Empty = this is a branch root (buyable with 0 prerequisites). Otherwise the Key of the perk that must be ranked first."), UsedImplicitly]
        public string RequiredPerkKey { get; set; } = "";

        [DisplayName("Required Rank"), Description("Minimum rank the required perk must have before this one can be bought."), UsedImplicitly]
        public int RequiredRank { get; set; } = 1;
```

Replace with:

```csharp
        [DisplayName("Required Perk Key"), Description("Empty = this is a branch root (buyable with 0 prerequisites). Otherwise the Key of the perk that must be ranked first. Deprecated - kept only so old saved catalogs deserialize; new code should use Requirements."), UsedImplicitly]
        public string RequiredPerkKey { get; set; } = "";

        [DisplayName("Required Rank"), Description("Minimum rank the required perk must have before this one can be bought. Deprecated - see RequiredPerkKey."), UsedImplicitly]
        public int RequiredRank { get; set; } = 1;

        [DisplayName("Requirements"), Description("Every entry must be met before this perk can be bought. Empty = branch root. A normal perk has exactly one entry (the previous rank in its own branch). A hybrid capstone has two or more, possibly in different branches."),
         ExpandableObject, UsedImplicitly]
        public List<PerkRequirement> Requirements { get; set; } = new List<PerkRequirement>();
```

`RequiredPerkKey`/`RequiredRank` are kept (not deleted) so any already-saved `Bannerlord-Twitch-*.yaml`
with the old single-key shape still deserializes without error - `PerkCatalog.Default()` (Task 8)
populates `Requirements` going forward, and `PerkService.CanBuy` (Step 4 below) only reads
`Requirements`, never the deprecated fields.

- [ ] **Step 3: Add the `PerkRequirement` class**

Add immediately above `PerkDef`:

```csharp
    public class PerkRequirement
    {
        [DisplayName("Perk Key"), Description("The Key of the perk that must have at least MinRank ranks purchased."), UsedImplicitly]
        public string PerkKey { get; set; } = "";

        [DisplayName("Min Rank"), Description("Minimum rank required in PerkKey."), UsedImplicitly]
        public int MinRank { get; set; } = 1;
    }
```

- [ ] **Step 4: Update `PerkService.CanBuy` to check the `Requirements` list**

Find (around line 349-357):

```csharp
            if (!string.IsNullOrEmpty(perk.RequiredPerkKey) &&
                perkBeh.GetRank(hero, perk.RequiredPerkKey) < perk.RequiredRank)
            {
                var reqDef = cfg.Perks.FirstOrDefault(p => p.Key == perk.RequiredPerkKey);
                reason = $"Requires {(reqDef?.DisplayName ?? perk.RequiredPerkKey)} rank {perk.RequiredRank}+.";
                return false;
            }
```

Replace with:

```csharp
            foreach (var req in perk.Requirements ?? new List<PerkRequirement>())
            {
                if (string.IsNullOrEmpty(req.PerkKey)) continue;
                if (perkBeh.GetRank(hero, req.PerkKey) >= req.MinRank) continue;
                var reqDef = cfg.Perks.FirstOrDefault(p => p.Key == req.PerkKey);
                reason = $"Requires {(reqDef?.DisplayName ?? req.PerkKey)} rank {req.MinRank}+.";
                return false;
            }
```

This is a straight generalization: one requirement (normal perk) behaves identically to the old
single-key check; two or more (hybrid capstone) means every single one must be satisfied before
`CanBuy` returns true - the loop's `return false` on the first unmet requirement enforces that.

- [ ] **Step 5: Build**

```bash
cd "E:/BLT/source_codes/RebellionOfThePeasants/source_perks"
dotnet build PeasantRebellionPerks.csproj -c Release
```

Expected: build error - `PerkCatalog.Default()` still sets the now-deprecated `RequiredPerkKey`/
`RequiredRank` fields on every entry, which still compiles fine (those properties still exist),
so expected result is actually **0 errors** here. If you see errors, they mean Step 2 or Step 4
was transcribed wrong - stop and re-check against the real file content from Step 1, don't guess.

- [ ] **Step 6: Commit**

```bash
git add source_perks/PeasantRebellionPerks.cs
git commit -m "Perk system: replace single RequiredPerkKey with multi-entry Requirements list"
```

---

### Task 2: Reflection-verify `ComputeBlowDamage`'s full parameter list

**Files:** none (research only - produces the exact parameter name Tasks 3, 5, 6 need)

- [ ] **Step 1: Dump the method signature**

Every later task in this plan that reads `AttackCollisionData` (armor absorbed, shield-block
flag, missile flag, hit world position) needs to know the *exact parameter name* Harmony must
bind to - guessing it risks a silent no-op patch (Harmony skips binding a parameter it can't
match by name, and neither compiles nor logs a warning for a mismatched optional binding in some
Harmony versions - the existing `PerkDamageEvadePatch.Prefix`/`Postfix` above only bind
`attackInformation` and 2 others precisely because those names are already confirmed correct).

Write a small scratch console step (do NOT leave this in the shipped file - delete after use).
Add temporarily to the very top of `OnSubModuleLoad` in `PeasantRebellionPerksModule`, before the
two existing `harmony.Patch(...)` calls:

```csharp
                var computeBlowDamageMethod = AccessTools.Method(typeof(MissionCombatMechanicsHelper), "ComputeBlowDamage");
                Log.Info("[Perk][DEBUG] ComputeBlowDamage params: " + string.Join(", ",
                    computeBlowDamageMethod.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")));
```

- [ ] **Step 2: Build, run once in-game, read the log line**

```bash
cd "E:/BLT/source_codes/RebellionOfThePeasants/source_perks"
dotnet build PeasantRebellionPerks.csproj -c Release
```

Deploy (see "Before you start"), start/load a campaign (this logs on module load, not per-hit, so
a main menu load is enough - no battle needed). Find the log line - check
`C:\Users\krzysiek\AppData\Local\Temp\Mount and Blade II Bannerlord\...` for a log file, or
whatever this project's established `Log.Info` sink already routes to (grep other `Log.Info`
calls in this same file to confirm where they surface if unsure).

Record the full parameter list here in this plan file (edit this bullet in place once known) -
it must include, at minimum, a parameter of type `AttackCollisionData` and a parameter of type
`AttackInformation` (already known-good from the existing patch) or the addon's own damage
patching only ever had one of the two, in which case Tasks 3/5/6 need `AttackCollisionData`
added as a **new prefix/postfix parameter Harmony binds by name** - use the exact name found here.

- [ ] **Step 3: Remove the scratch debug log line**

```bash
git diff source_perks/PeasantRebellionPerks.cs
```

Confirm only the temporary `Log.Info("[Perk][DEBUG]...")` line (and the `computeBlowDamageMethod`
local it needed) is being removed, nothing else - then delete both lines.

- [ ] **Step 4: Build to confirm the removal didn't break anything**

```bash
dotnet build PeasantRebellionPerks.csproj -c Release
```

Expected: 0 errors, no commit needed for this task (nothing shipped changes) - the confirmed
parameter name is used directly in Tasks 3, 5, 6's real code instead of being committed as a
separate step.

---

### Task 3: Ignore Armor + Cut Through

**Files:**
- Modify: `E:\BLT\source_codes\RebellionOfThePeasants\source_perks\PeasantRebellionPerks.cs`

Both are damage-postprocessing tweaks on the same hit `PerkDamageEvadePatch.Postfix` already
adjusts for Damage/Berserk - extending the same method keeps one single place that computes
"final inflicted damage for this hit," matching this file's own established pattern instead of
introducing a second competing damage-adjustment patch.

- [ ] **Step 1: Add the `AttackCollisionData` parameter to `PerkDamageEvadePatch.Postfix`**

Using the exact parameter name confirmed in Task 2, extend the existing signature (around line
460):

```csharp
        public static void Postfix(ref AttackInformation attackInformation, bool cancelDamage, ref int inflictedDamage, in AttackCollisionData collisionData)
```

(`in AttackCollisionData collisionData` here is the Task 2 answer, written literally once
confirmed - if Task 2 found a different parameter name than `collisionData`, use that name
instead, consistently, everywhere in this task.)

- [ ] **Step 2: Add Ignore Armor - blend raw pre-armor damage back in**

Inside `Postfix`, after the existing `dmgBonus`/`berserkBonus` block, before the final
`inflictedDamage = (int)(inflictedDamage * (1f + totalBonus));` line:

```csharp
                float ignoreArmorBonus = PerkService.GetBonus(attackerHero, "Ignore Armor");
                if (ignoreArmorBonus > 0f && collisionData.AbsorbedByArmor > 0f)
                {
                    // AbsorbedByArmor is how much this specific hit's damage the target's armor
                    // already ate. Ignore Armor gives back a percentage of that as extra damage -
                    // 100% would mean armor did nothing at all, 30% means the hit deals 30% of
                    // what armor absorbed on top of whatever normally got through.
                    inflictedDamage += (int)(collisionData.AbsorbedByArmor * ignoreArmorBonus);
                }
```

- [ ] **Step 3: Add Cut Through - partial damage through a shield block**

Immediately after (still inside `Postfix`, still before the final multiply line):

```csharp
                float cutThroughChance = PerkService.GetBonus(attackerHero, "Cut Through");
                if (cutThroughChance > 0f && collisionData.AttackBlockedWithShield && inflictedDamage <= 0
                    && MBRandom.RandomFloat < cutThroughChance)
                {
                    // A shield-blocked hit normally deals 0 damage (inflictedDamage is already 0
                    // by the time Postfix runs for a fully blocked attack). Cut Through rolls a
                    // chance to push some of the blow's original magnitude through anyway.
                    inflictedDamage = Math.Max(1, (int)(collisionData.BaseMagnitude * 0.25f));
                }
```

Note the `inflictedDamage <= 0` guard: if the hit already dealt damage (not fully blocked), Cut
Through has nothing to do - it only matters for hits that were *fully* absorbed by the shield.

- [ ] **Step 4: Build**

```bash
cd "E:/BLT/source_codes/RebellionOfThePeasants/source_perks"
dotnet build PeasantRebellionPerks.csproj -c Release
```

Expected: 0 errors. If `AbsorbedByArmor`, `AttackBlockedWithShield`, or `BaseMagnitude` don't
exist on the confirmed `AttackCollisionData` type, that's a real compile error here - these three
names were confirmed present via grep of this project's own `BLTAdoptAHero` source (Task 2's
context-gathering, done during this plan's writing, not guessed) but re-confirm against the
actual field types (float vs int) if the compiler disagrees on a numeric conversion.

- [ ] **Step 5: Commit**

```bash
git add source_perks/PeasantRebellionPerks.cs
git commit -m "Add Ignore Armor and Cut Through perk branches to the damage postfix patch"
```

---

### Task 4: Shrug Off

**Files:**
- Modify: `E:\BLT\source_codes\RebellionOfThePeasants\source_perks\PeasantRebellionPerks.cs`

- [ ] **Step 1: Research where the native stagger/knockdown decision actually happens**

`BlowFlags.ShrugOff` (confirmed to exist, seen in
`BLTAdoptAHero/Powers/Core/HitBehavior.cs:56` and `BLTAdoptAHero.cs:385`'s
`DecideAgentShrugOffBlow` override) is read by the engine *before* `ComputeBlowDamage` finishes -
it affects whether the victim staggers, not how much damage they take, so it is almost certainly
decided at a different call site than Task 2's patch target. Search for the real hook:

```bash
grep -n "DecideAgentShrugOffBlow" "E:/BLT/source_codes/blt/Bannerlord-Twitch-5.2.4/BannerlordTwitch/BLTAdoptAHero/BLTAdoptAHero.cs"
```

Read the full method and its containing class (`CustomAgentDamageModel` or similar - confirm the
exact class name by reading the file around that line). This is a virtual method override on a
`GetAgentApplyDamageModel`/`AgentApplyDamageModel`-family class BLTAdoptAHero already
subclasses - the perk system, being a *separate* addon DLL, cannot subclass the same base a
second time (only one damage model can be active). Two options, in order of preference:

1. If `DecideAgentShrugOffBlow` is a **Harmony-patchable virtual method** on a type reachable by
   name (not requiring a second subclass), patch it directly with a postfix that ORs in a
   perk-driven chance roll, exactly like `PerkHealthLimitPatch` patches `Agent.HealthLimit`'s
   getter today.
2. If it's only reachable through BLTAdoptAHero's own subclass instance (not a clean Harmony
   target), patch `BlowFlags`-setting closer to `ComputeBlowDamage` instead - check whether
   `AttackCollisionData` (Task 2) or `Blow` itself exposes a settable flags field reachable from
   the already-proven `PerkDamageEvadePatch.Postfix` patch point, and add `BlowFlags.ShrugOff` to
   it there instead of via a second patch target.

Write down which option applies before writing Step 2 - do not guess which one works without
having read `DecideAgentShrugOffBlow`'s real signature and containing type first.

- [ ] **Step 2: Implement the chosen option**

If Option 1 (separate Harmony patch on the real virtual method):

```csharp
    [HarmonyPatch(/* the real declaring type from Step 1 */, "DecideAgentShrugOffBlow")]
    internal static class PerkShrugOffPatch
    {
        public static void Postfix(Agent victimAgent, ref bool __result)
        {
            try
            {
                if (__result) return; // already shrugging off for another reason
                var hero = victimAgent?.GetAdoptedHero();
                if (hero == null) return;
                float bonus = PerkService.GetBonus(hero, "Shrug Off");
                if (bonus > 0f && MBRandom.RandomFloat < bonus) __result = true;
            }
            catch (Exception ex) { Log.Exception("PerkShrugOffPatch.Postfix", ex); }
        }
    }
```

Register it in `OnSubModuleLoad` alongside the other two `harmony.Patch(...)` calls, same
try/catch block, same `Log.Info` line updated to mention it.

If Option 2 (folded into the existing damage postfix), write the equivalent flag-setting logic
directly inside `PerkDamageEvadePatch.Postfix` instead, using whatever settable flags field Step
1 found - there is no separate code sample for this branch since the exact field/API depends on
what Step 1's research actually finds; write it following the same try/catch and
`PerkService.GetBonus(..., "Shrug Off")` roll shape as Option 1's example above.

- [ ] **Step 3: Build**

```bash
cd "E:/BLT/source_codes/RebellionOfThePeasants/source_perks"
dotnet build PeasantRebellionPerks.csproj -c Release
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add source_perks/PeasantRebellionPerks.cs
git commit -m "Add Shrug Off perk branch (stagger/knockdown immunity chance)"
```

---

### Task 5: Cleave

**Files:**
- Modify: `E:\BLT\source_codes\RebellionOfThePeasants\source_perks\PeasantRebellionPerks.cs`

Melee-only. On a successful (non-missile) hit, find other enemy agents within a short radius AND
within a forward-facing arc from the attacker, and deal a percentage of the same hit's damage to
each. Uses `collisionData.CollisionGlobalPosition` (confirmed present, Task 2's grep) for the hit
location and `attackInformation.AttackerAgent`'s look direction for the arc.

- [ ] **Step 1: Add the Cleave block to `PerkDamageEvadePatch.Postfix`**

After the Cut Through block from Task 3 (still inside `Postfix`, before the final multiply):

```csharp
                float cleaveBonus = PerkService.GetBonus(attackerHero, "Cleave");
                if (cleaveBonus > 0f && !collisionData.IsMissile && inflictedDamage > 0)
                {
                    ApplyCleave(atkAgent, collisionData.CollisionGlobalPosition.AsVec2, inflictedDamage, cleaveBonus);
                }
```

- [ ] **Step 2: Add the `ApplyCleave` helper**

Add as a new private static method on `PerkDamageEvadePatch` (same class), after `Postfix`:

```csharp
        // Cleave: hit every OTHER enemy agent within CleaveRadius of the original hit location
        // AND within +/-CleaveHalfAngleDegrees of the attacker's forward direction, for
        // cleaveBonus% of the original hit's damage each. Deliberately excludes the agent that
        // was already hit (that damage was already applied by the normal Postfix flow above).
        private const float CleaveRadius = 2.5f;
        private const float CleaveHalfAngleDegrees = 60f;

        private static void ApplyCleave(Agent attacker, Vec2 hitPosition, int originalDamage, float cleaveBonus)
        {
            if (attacker?.Mission == null) return;
            Vec2 forward = attacker.LookDirection.AsVec2.Normalized();
            int cleaveDamage = Math.Max(1, (int)(originalDamage * cleaveBonus));

            foreach (var other in attacker.Mission.Agents)
            {
                if (other == null || other == attacker || !other.IsActive()) continue;
                if (other.Team == null || attacker.Team == null || !attacker.Team.IsEnemyOf(other.Team)) continue;

                Vec2 toOther = (other.Position.AsVec2 - hitPosition);
                float distance = toOther.Length;
                if (distance > CleaveRadius || distance <= 0.01f) continue;

                float angleBetween = MathF.Abs(MathF.Acos(MBMath.ClampFloat(Vec2.DotProduct(forward, toOther.Normalized()), -1f, 1f))) * (180f / MathF.PI);
                if (angleBetween > CleaveHalfAngleDegrees) continue;

                other.Health = Math.Max(0f, other.Health - cleaveDamage);
            }
        }
```

- [ ] **Step 3: Reflection-verify `Agent.Position`, `Agent.LookDirection`, and `Vec2`/`MBMath` availability**

```bash
grep -rn "\.LookDirection\|Agent\.Position\b" "E:/BLT/source_codes/blt/Bannerlord-Twitch-5.2.4/BannerlordTwitch/BLTAdoptAHero" --include=*.cs | head -10
```

Confirm both members exist with those exact names on `Agent` (they're extremely commonly used
Bannerlord APIs, expected to be trivially confirmed, not a real risk - but confirm before
trusting, per this project's established convention, rather than assuming).

- [ ] **Step 4: Build**

```bash
cd "E:/BLT/source_codes/RebellionOfThePeasants/source_perks"
dotnet build PeasantRebellionPerks.csproj -c Release
```

Expected: 0 errors. If `AttackInformation` doesn't directly expose `AttackerAgent` under that
exact name (it should - `Postfix` already reads `attackInformation.AttackerAgent` earlier in the
same method today), no change needed there.

- [ ] **Step 5: Commit**

```bash
git add source_perks/PeasantRebellionPerks.cs
git commit -m "Add Cleave perk branch (forward-arc multi-target melee damage)"
```

---

### Task 6: Ranged AOE

**Files:**
- Modify: `E:\BLT\source_codes\RebellionOfThePeasants\source_perks\PeasantRebellionPerks.cs`

Bow/crossbow/thrown only (`collisionData.IsMissile`). On hit, a percentage *chance* (not
guaranteed like Cleave) to deal splash damage to every enemy in a full circle around the hit -
reuses the same `ApplyCleave`-style agent search but without the forward-arc restriction.

- [ ] **Step 1: Add the ranged AOE block to `PerkDamageEvadePatch.Postfix`**

Immediately after the Cleave block from Task 5:

```csharp
                float aoeChance = PerkService.GetBonus(attackerHero, "AOE");
                if (aoeChance > 0f && collisionData.IsMissile && inflictedDamage > 0
                    && MBRandom.RandomFloat < aoeChance)
                {
                    ApplyRangedAoe(atkAgent, collisionData.CollisionGlobalPosition.AsVec2, inflictedDamage);
                }
```

- [ ] **Step 2: Add the `ApplyRangedAoe` helper**

Add alongside `ApplyCleave`:

```csharp
        // Ranged AOE: same nearby-enemy search as Cleave, full circle instead of a forward arc
        // (a thrown/loosed projectile has no "swing direction" to restrict to), fixed 50% of the
        // original hit's damage to each enemy caught in the radius - deliberately not
        // perk-tunable per rank the way Cleave's percentage is, since AOE's real lever is its
        // trigger *chance* (ranked via aoeChance above), not its magnitude.
        private const float RangedAoeRadius = 3f;

        private static void ApplyRangedAoe(Agent attacker, Vec2 hitPosition, int originalDamage)
        {
            if (attacker?.Mission == null) return;
            int splashDamage = Math.Max(1, originalDamage / 2);

            foreach (var other in attacker.Mission.Agents)
            {
                if (other == null || other == attacker || !other.IsActive()) continue;
                if (other.Team == null || attacker.Team == null || !attacker.Team.IsEnemyOf(other.Team)) continue;

                float distance = (other.Position.AsVec2 - hitPosition).Length;
                if (distance > RangedAoeRadius) continue;

                other.Health = Math.Max(0f, other.Health - splashDamage);
            }
        }
```

- [ ] **Step 3: Build**

```bash
cd "E:/BLT/source_codes/RebellionOfThePeasants/source_perks"
dotnet build PeasantRebellionPerks.csproj -c Release
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add source_perks/PeasantRebellionPerks.cs
git commit -m "Add ranged AOE perk branch (splash damage on bow/crossbow/thrown hits)"
```

---

### Task 7: One-Handed Weapon Mastery

**Files:**
- Modify: `E:\BLT\source_codes\RebellionOfThePeasants\source_perks\PeasantRebellionPerks.cs`

- [ ] **Step 1: Extend `PerkStatMissionBehavior.BuildConfig`**

Find (around line 588-593):

```csharp
            float wm2h = PerkService.GetBonus(hero, "Weapon Mastery: Two-Handed");
            float wmPole = PerkService.GetBonus(hero, "Weapon Mastery: Polearm");
            float wmThrow = PerkService.GetBonus(hero, "Weapon Mastery: Thrown");
            AddPct(DrivenProperty.MeleeWeaponDamageMultiplierBonus, Math.Max(wm2h, wmPole));
            AddPct(DrivenProperty.ThrowingWeaponDamageMultiplierBonus, wmThrow);
```

Replace with:

```csharp
            float wm2h = PerkService.GetBonus(hero, "Weapon Mastery: Two-Handed");
            float wmPole = PerkService.GetBonus(hero, "Weapon Mastery: Polearm");
            float wmThrow = PerkService.GetBonus(hero, "Weapon Mastery: Thrown");
            float wm1h = PerkService.GetBonus(hero, "Weapon Mastery: One-Handed");
            AddPct(DrivenProperty.MeleeWeaponDamageMultiplierBonus, Math.Max(Math.Max(wm2h, wmPole), wm1h));
            AddPct(DrivenProperty.ThrowingWeaponDamageMultiplierBonus, wmThrow);
```

Follows the existing (already-shipped) pattern exactly: none of the three existing weapon-mastery
branches check which weapon the hero currently has equipped either - `MeleeWeaponDamageMultiplierBonus`
is a flat modifier applied regardless, with `Math.Max` between the melee-mastery branches so they
don't stack additively with each other. One-Handed joins that same `Math.Max` group.

- [ ] **Step 2: Build**

```bash
cd "E:/BLT/source_codes/RebellionOfThePeasants/source_perks"
dotnet build PeasantRebellionPerks.csproj -c Release
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add source_perks/PeasantRebellionPerks.cs
git commit -m "Add One-Handed Weapon Mastery perk branch"
```

---

### Task 8: Catalog entries for the 6 new branches

**Files:**
- Modify: `E:\BLT\source_codes\RebellionOfThePeasants\source_perks\PeasantRebellionPerks.cs`

- [ ] **Step 1: Read `PerkCatalog.Default()` in full**

```bash
grep -n "public static class PerkCatalog" -A 60 "E:/BLT/source_codes/RebellionOfThePeasants/source_perks/PeasantRebellionPerks.cs"
```

Confirm the exact shape of the existing `Branch(...)` local function it uses to generate 3-rank
ladders, and that it currently sets `RequiredPerkKey`/`RequiredRank` per entry (Task 1 left those
fields in place for backward-compat, but new entries should populate `Requirements` instead).

- [ ] **Step 2: Update the `Branch` local function to populate `Requirements`**

Find the existing `Branch` local function (shape matches this, confirm against Step 1's real
read):

```csharp
            void Branch(string branchName, string keyPrefix, float bonusPerRank, int ranks = 3)
            {
                string prevKey = "";
                for (int r = 1; r <= ranks; r++)
                {
                    string key = $"{keyPrefix}_{r}";
                    list.Add(new PerkDef
                    {
                        Key = key,
                        DisplayName = $"{branchName} {r}",
                        Branch = branchName,
                        MaxRank = 1,
                        BonusPerRank = bonusPerRank,
                        RequiredPerkKey = prevKey,
                        RequiredRank = 1,
                    });
                    prevKey = key;
                }
            }
```

Replace the `RequiredPerkKey = prevKey, RequiredRank = 1,` lines with:

```csharp
                        Requirements = string.IsNullOrEmpty(prevKey) ? new List<PerkRequirement>() : new List<PerkRequirement> { new PerkRequirement { PerkKey = prevKey, MinRank = 1 } },
```

(Leave `RequiredPerkKey`/`RequiredRank` assignment lines in place too if present, for the
deprecated-field backward-compat reasoning from Task 1 - don't remove them, just add
`Requirements` alongside.)

- [ ] **Step 3: Add the 6 new branch calls**

Find where the existing `Branch(...)` calls are listed (the universal 11-13 branches), and add
these 6 immediately after the last existing one:

```csharp
            Branch("Cleave",         "cleave",     0.15f); // % of the hit's damage also dealt to nearby enemies in a forward arc
            Branch("Ignore Armor",   "ignorearmor",0.10f); // % of armor-absorbed damage given back as extra damage
            Branch("Cut Through",    "cutthrough", 0.10f); // % chance a shield-blocked hit still deals partial damage
            Branch("Shrug Off",      "shrugoff",   0.10f); // % chance to not stagger/knock down when hit
            Branch("AOE",            "aoe",        0.08f); // % chance a ranged hit also splashes nearby enemies
            Branch("Weapon Mastery: One-Handed", "wm1h", 0.015f); // % bonus melee damage with one-handed weapons
```

- [ ] **Step 4: Build**

```bash
cd "E:/BLT/source_codes/RebellionOfThePeasants/source_perks"
dotnet build PeasantRebellionPerks.csproj -c Release
```

Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add source_perks/PeasantRebellionPerks.cs
git commit -m "Catalog: add Cleave, Ignore Armor, Cut Through, Shrug Off, AOE, One-Handed Mastery branches"
```

---

### Task 9: Four hybrid capstones

**Files:**
- Modify: `E:\BLT\source_codes\RebellionOfThePeasants\source_perks\PeasantRebellionPerks.cs`

- [ ] **Step 1: Add the 4 capstone entries**

Add directly to `PerkCatalog.Default()`, after the 6 new `Branch(...)` calls from Task 8, before
the `return list;` line:

```csharp
            list.Add(new PerkDef
            {
                Key = "hybrid_duelist", DisplayName = "Duelist", Branch = "Duelist (Capstone)",
                MaxRank = 1, BonusPerRank = 0.08f, MinLevel = 15,
                Requirements = new List<PerkRequirement>
                {
                    new PerkRequirement { PerkKey = "dmg_2", MinRank = 1 },
                    new PerkRequirement { PerkKey = "evade_2", MinRank = 1 },
                },
            });
            list.Add(new PerkDef
            {
                Key = "hybrid_juggernaut", DisplayName = "Juggernaut", Branch = "Juggernaut (Capstone)",
                MaxRank = 1, BonusPerRank = 0.08f, MinLevel = 15,
                Requirements = new List<PerkRequirement>
                {
                    new PerkRequirement { PerkKey = "hp_2", MinRank = 1 },
                    new PerkRequirement { PerkKey = "ignorearmor_2", MinRank = 1 },
                },
            });
            list.Add(new PerkDef
            {
                Key = "hybrid_reaper", DisplayName = "Reaper", Branch = "Reaper (Capstone)",
                MaxRank = 1, BonusPerRank = 0.10f, MinLevel = 15,
                Requirements = new List<PerkRequirement>
                {
                    new PerkRequirement { PerkKey = "cleave_2", MinRank = 1 },
                    new PerkRequirement { PerkKey = "berserk_2", MinRank = 1 },
                },
            });
            list.Add(new PerkDef
            {
                Key = "hybrid_marksman", DisplayName = "Marksman", Branch = "Marksman (Capstone)",
                MaxRank = 1, BonusPerRank = 0.08f, MinLevel = 15,
                Requirements = new List<PerkRequirement>
                {
                    new PerkRequirement { PerkKey = "acc_2", MinRank = 1 },
                    new PerkRequirement { PerkKey = "aoe_2", MinRank = 1 },
                },
            });
```

The `PerkKey` values referenced (`dmg_2`, `evade_2`, `hp_2`, `ignorearmor_2`, `cleave_2`,
`berserk_2`, `acc_2`, `aoe_2`) must exactly match the `key` strings the `Branch(...)` local
function generates (`$"{keyPrefix}_{r}"`) - confirm the real `keyPrefix` values used for
`Accuracy` (`"acc"`), `Damage` (`"dmg"`), `Evade` (`"evade"`), `HP` (`"hp"`), `Berserk`
(`"berserk"`) by reading the existing `Branch(...)` call list (Task 8 Step 1's read) rather than
assuming - this plan's `cleave`/`ignorearmor`/`aoe` prefixes are the ones this same plan just
defined in Task 8 Step 3, so those three are guaranteed correct; the other five must be checked
against the real file.

Each capstone gets its own single-entry `Branch` name (e.g. `"Duelist (Capstone)"`) rather than
reusing one of its two source branches, so the radial constellation renderer (Task 10) gives it
its own spoke with both cross-branch lines converging into it, instead of it visually belonging
to only one of the two branches it actually requires.

- [ ] **Step 2: Reflection-verify `PerkService.CanBuy` handles a perk with `Branch` set but no
  matching entry elsewhere in the same branch**

`GetBonus(hero, branch)` (used by combat patches to sum a hero's total bonus in a branch) already
loops `cfg.Perks.Where(p => p.Branch == branch)` - a capstone with a unique one-entry branch name
works fine there (sums to just itself). No code change needed, this step is confirming that by
re-reading `PerkService.GetBonus` once more (already read in Task 1 Step 1) - if anything looks
wrong, stop and report rather than guessing a fix not covered by this plan.

- [ ] **Step 3: Build**

```bash
cd "E:/BLT/source_codes/RebellionOfThePeasants/source_perks"
dotnet build PeasantRebellionPerks.csproj -c Release
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add source_perks/PeasantRebellionPerks.cs
git commit -m "Add 4 hybrid capstone perks (Duelist, Juggernaut, Reaper, Marksman) using cross-branch Requirements"
```

---

### Task 10: Wire real `Requirements` into the documentation generator caller

**Files:**
- Modify: `E:\BLT\source_codes\RebellionOfThePeasants\source_perks\PeasantRebellionPerks.cs`

The renderer (`DocumentationGenerator.PerksSection` in the `BannerlordTwitch` repo) already
accepts a `RequiredPerkKeys` list per perk and draws one line per entry, colored by that
requirement's source branch (shipped 2026-08-12, see that repo's own commit history). Today's
caller in this file still wraps the old single `RequiredPerkKey` in a 1-item list - update it to
map the real `Requirements` list instead, so hybrid capstones actually show two converging lines.

- [ ] **Step 1: Read `PerkGlobalConfig.GenerateDocumentation` in full**

```bash
grep -n "GenerateDocumentation" -A 20 "E:/BLT/source_codes/RebellionOfThePeasants/source_perks/PeasantRebellionPerks.cs"
```

- [ ] **Step 2: Replace the single-key wrap with a real mapping**

Find:

```csharp
                            RequiredPerkKeys: (IEnumerable<string>)(string.IsNullOrEmpty(p.RequiredPerkKey) ? Array.Empty<string>() : new[] { p.RequiredPerkKey }),
```

Replace with:

```csharp
                            RequiredPerkKeys: (IEnumerable<string>)(p.Requirements ?? new List<PerkRequirement>()).Select(r => r.PerkKey).Where(k => !string.IsNullOrEmpty(k)),
```

- [ ] **Step 3: Update `BuildRequirementText` (the legend table's "Requires" column) to list every requirement**

```bash
grep -n "private static string BuildRequirementText" -A 8 "E:/BLT/source_codes/RebellionOfThePeasants/source_perks/PeasantRebellionPerks.cs"
```

Find:

```csharp
        private static string BuildRequirementText(PerkDef p)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(p.RequiredPerkKey)) parts.Add($"{p.RequiredPerkKey} rank {p.RequiredRank}+");
            if (p.MinLevel > 0) parts.Add($"level {p.MinLevel}+");
            if (!string.IsNullOrEmpty(p.RequiredAchievementKey)) parts.Add($"achievement '{p.RequiredAchievementKey}'");
            return string.Join(", ", parts);
        }
```

Replace with:

```csharp
        private static string BuildRequirementText(PerkDef p)
        {
            var parts = new List<string>();
            foreach (var req in p.Requirements ?? new List<PerkRequirement>())
            {
                if (string.IsNullOrEmpty(req.PerkKey)) continue;
                parts.Add($"{req.PerkKey} rank {req.MinRank}+");
            }
            if (p.MinLevel > 0) parts.Add($"level {p.MinLevel}+");
            if (!string.IsNullOrEmpty(p.RequiredAchievementKey)) parts.Add($"achievement '{p.RequiredAchievementKey}'");
            return string.Join(", ", parts);
        }
```

- [ ] **Step 4: Build**

```bash
cd "E:/BLT/source_codes/RebellionOfThePeasants/source_perks"
dotnet build PeasantRebellionPerks.csproj -c Release
```

Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add source_perks/PeasantRebellionPerks.cs
git commit -m "Wire real multi-Requirements into the Neocities doc page (hybrid capstones show both converging lines)"
```

---

### Task 11: Build, deploy, verify

**Files:** none (build/deploy/manual verification only)

- [ ] **Step 1: Full build**

```bash
cd "E:/BLT/source_codes/RebellionOfThePeasants/source_perks"
dotnet build PeasantRebellionPerks.csproj -c Release
```

Expected: 0 errors.

- [ ] **Step 2: Deploy** (ask the user to close the game first if this fails with a file-lock error -
  `MSB3027`/`MSB3021` mentioning `Bannerlord.BLSE.LauncherEx`, same as this project's established
  pattern)

```bash
cp "bin/Release/net48/PeasantRebellionPerks.dll" "C:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord/Modules/BLTAdoptAHero/bin/Win64_Shipping_Client/PeasantRebellionPerks.dll"
cp "bin/Release/net48/PeasantRebellionPerks.dll" "E:/BLT/source_codes/RebellionOfThePeasants/bin_perks/Win64_Shipping_Client/PeasantRebellionPerks.dll"
```

- [ ] **Step 3: Reflection-verify the new types load**

```bash
python3 -c "
import clr
clr.AddReference(r'C:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord\Modules\BLTAdoptAHero\bin\Win64_Shipping_Client\PeasantRebellionPerks.dll')
import PeasantRebellionPerks
print([t.Name for t in PeasantRebellionPerks.__dict__.values()])
" 2>&1 || echo "pythonnet not available - use a .NET reflection scratch instead, same pattern as prior plans in this project"
```

(If `pythonnet`/`clr` isn't installed, fall back to a tiny scratch C# console app or PowerShell
`[Reflection.Assembly]::LoadFrom(...)` - either way, confirm `PerkRequirement`, and that
`PerkDef.Requirements` and the 6 new branch keys (`cleave_1`, `ignorearmor_1`, `cutthrough_1`,
`shrugoff_1`, `aoe_1`, `wm1h_1`) exist by loading `PerkGlobalConfig`'s default catalog and
printing every `Key`.)

- [ ] **Step 4: Manual verification checklist (report results to the user, do not skip)**

In-game: start/load a campaign, run `!perk` for an adopted hero - confirm no crash and the branch
list includes the 6 new ones plus the 4 capstones. Lower `KillsPerPoint` to 1 in `BLTConfigure`
for testing, get a few kills, run `!perk buy cleave` then `!perk buy ignore armor` etc. - confirm
each purchase succeeds and the reply names the right perk. Try `!perk buy duelist` before buying
rank 2 in Damage or Evade - confirm it's rejected with a message naming the missing requirement;
buy rank 2 in both, then retry - confirm it now succeeds. In a real battle: confirm a hero with
Cleave invested visibly damages a second nearby enemy on a melee hit, and a hero with Ignore
Armor invested deals visibly more damage to a heavily-armored target than an otherwise-identical
hero without it. Generate the Neocities doc page and open it - confirm all 4 capstones render
with two differently-colored lines each, converging from their two source branches.

- [ ] **Step 5: Commit and tag**

```bash
cd "E:/BLT/source_codes/RebellionOfThePeasants"
git add -A
git commit -m "Package perk system expansion: 6 new branches + 4 hybrid capstones"
git tag perks-v2
```

---

## Self-review notes

- **Spec coverage:** §1 data model → Task 1. §2 six branches → Tasks 3 (Ignore Armor/Cut
  Through), 4 (Shrug Off), 5 (Cleave), 6 (AOE), 7 (One-Handed Mastery), catalog entries → Task 8.
  §3 four hybrid capstones → Task 9. Spec's "out of scope, folded into implementation" doc-line
  item → Task 10 (the renderer side of that was actually already shipped ahead of this plan,
  during the visual-polish work that preceded it - Task 10 here is only the remaining caller-side
  wiring, `RequiredPerkKeys` real mapping instead of a single-item wrap).
- **Open research spikes**, both explicitly flagged rather than guessed: Task 2 (exact
  `ComputeBlowDamage` parameter name for `AttackCollisionData`) gates Tasks 3, 5, 6 which all
  depend on its answer; Task 4 Step 1 (where `BlowFlags.ShrugOff` is actually decided) gates Task
  4's own implementation shape. Both are written so the implementer confirms a real name/site
  before writing code that depends on it, not after.
- **Type consistency check:** `PerkService.GetBonus(hero, branchName)` (unchanged from before
  this plan) is what every new branch (Tasks 3, 4, 5, 6, 7) reads - verified all six new branch
  name strings passed to it (`"Ignore Armor"`, `"Cut Through"`, `"Shrug Off"`, `"Cleave"`,
  `"AOE"`, `"Weapon Mastery: One-Handed"`) exactly match the `branchName` argument given to
  `Branch(...)` for that same branch in Task 8 Step 3 - no typo drift between the two.
- **`PerkRequirement.PerkKey` values in Task 9** reference keys generated by Task 8's `Branch(...)`
  calls and by this plan's own new branches - cross-checked: `cleave_2` (Task 8's `cleave` prefix,
  rank 2), `berserk_2`/`dmg_2`/`evade_2`/`hp_2`/`acc_2` (pre-existing branches, prefixes to be
  confirmed against the real file per Task 9 Step 1's instruction, not assumed), `ignorearmor_2`/
  `aoe_2` (this plan's own Task 8 prefixes) - consistent.
