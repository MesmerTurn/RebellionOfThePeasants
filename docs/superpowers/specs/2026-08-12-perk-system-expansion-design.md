# Perk System Expansion — Design

## Goal

Expand the standalone perk system (`PeasantRebellionPerks.dll`, `source_perks/PeasantRebellionPerks.cs`)
shipped in Peasant Rebellion with:

1. A real cross-branch prerequisite model (a perk can require ranks in *multiple* different
   branches at once, not just the previous rank in its own branch).
2. Six new combat-mechanic branches: Cleave, Ignore Armor, Cut Through, Shrug Off, ranged AOE,
   One-Handed Weapon Mastery.
3. Four new hybrid capstone perks that use the new cross-branch model to combine two existing
   branches into one powerful, harder-to-reach finisher.

Point-earning rates (`KillsPerPoint`/`BattlesPerPoint`/`PointsPerLevel`) are **out of scope** —
they're already a tunable `PerkGlobalConfig` field editable live in BLTConfigure, not something
this expansion needs to touch. Once the new branches exist, the user can retune the economy from
the config UI without further code changes.

## 1. Data model: single `RequiredPerkKey` → multi `Requirements`

Today (`PeasantRebellionPerks.cs`):

```csharp
public string RequiredPerkKey { get; set; } = "";
public int RequiredRank { get; set; } = 1;
```

A perk can only ever require one prior perk, always read as "in the same branch, previous rank."
This can't express "requires rank 2 in Damage AND rank 2 in Evade."

New shape (same pattern already proven in BannerlordTTV's own perk-tree redesign, see
`BannerlordTTV/docs/superpowers/plans/2026-08-07-perk-tree-redesign.md` — not copied verbatim,
this project's own catalog and hooks differ, but the requirement-list shape is the right fit
here too):

```csharp
public class PerkRequirement
{
    public string PerkKey { get; set; }
    public int MinRank { get; set; } = 1;
}

public class PerkDef
{
    // ... existing fields unchanged ...
    public List<PerkRequirement> Requirements { get; set; } = new List<PerkRequirement>();
    // RequiredPerkKey / RequiredRank removed - every existing branch entry becomes a
    // single-entry Requirements list instead.
}
```

`PerkService.CanBuy` currently checks one `RequiredPerkKey`/`RequiredRank` pair; it changes to
loop over `Requirements` and fail on the first unmet one (same fail-closed behavior, just
multi-entry). `PerkCatalog.Default()` and the four new hybrid capstones both populate
`Requirements` with 1 entry (normal branches) or 2 entries (hybrid capstones).

## 2. Six new branches

Each is a standalone branch (own root + ranks), following the exact shape of the existing 12
branches (3-4 ranks, `BonusPerRank`, root has empty `Requirements`). Mechanic and implementation
site for each:

### Cleave
Melee weapon hit (sword/axe/mace, any handedness) also strikes other enemies in a narrow arc in
front of the hero — not a radius, not an explosion, just "also hits whoever else is standing in
front of you when you swing." No existing hook does this; new code finds nearby enemy agents
within a small radius AND within a forward-facing angle from the hero's attack, then applies a
percentage of the same hit's damage to each. This is new logic, not a copy of an existing pattern
— flagged as the one branch needing careful implementation-time testing.

### Ignore Armor
Percentage of damage bypasses the target's armor (penetration). Implementation site:
`BLTAdoptAHero.cs:290`'s `CalculateDamage` override — this is where the base game's armor
mitigation already happens; the perk blends between the armor-mitigated damage the base model
returns and the raw pre-armor damage, weighted by the penetration percentage.

### Cut Through
Percentage chance a blocked-by-shield hit still deals partial damage through the block.
Implementation site: `collisionData.AttackBlockedWithShield`, the exact flag `AddDamagePower.cs:241`
already reads for its own (different) shield-shatter effect — same hook, new consumer.

### Shrug Off
Percentage chance the hero doesn't stagger/get knocked down when hit. This is not new engine
work — `HitBehavior.cs` already exposes `ShrugOffChancePercent` and applies it via the native
`BlowFlags.ShrugOff` flag for an existing BLT power. The perk branch just needs to add its
percentage into that same existing value instead of introducing a new mechanism.

### Ranged AOE
Bow, crossbow, or thrown-weapon (javelin/throwing axe/throwing knife) hits get a percentage
chance to also damage enemies in a radius around the hit target — an "explosive shot," full
radius (not an arc like Cleave, and melee weapons never trigger it). Same "find nearby agents"
technique as Cleave, but a full circle instead of a forward arc, and gated to ranged
`WeaponClass` values only.

### One-Handed Weapon Mastery
Damage/speed bonus while wielding a one-handed weapon (sword/mace/axe). Implementation site:
`BLTPerkAgentStatModel`, exactly parallel to the existing `wm2h`/`wmpole`/`wmthrow` branches —
add a `WeaponClass.OneHandedSword`/`OneHandedMace`/`OneHandedAxe` check alongside the existing
two-handed/polearm/thrown checks in the same method.

## 3. Four hybrid capstones (cross-branch requirements)

Each requires rank 2 in two specific branches (old or new) — the first real use of the new
multi-entry `Requirements` list, and the answer to "every perk should require unlocking others":

| Key | Requires | Effect |
|---|---|---|
| `hybrid_duelist` | `dmg` r2 + `evade` r2 | Bonus damage AND evade simultaneously — an offense/defense hybrid rank |
| `hybrid_juggernaut` | `hp` r2 + `ignorearmor` r2 | Tankiness that also punches through armor |
| `hybrid_reaper` | `cleave` r2 + `berserk` r2 | Cleave's per-swing bonus damage is increased further while the hero is below 30% HP |
| `hybrid_marksman` | `accuracy` r2 + `aoe` r2 | Explosive ranged shots also gain increased accuracy |

Each is `MaxRank = 1`, a meaningfully larger `BonusPerRank` than a normal rank (these are meant
to feel like a payoff, not just another rank), and — per the existing capstone convention already
used for the 5 BLT-unique branches — `MinLevel` set higher than a normal rank (suggest 15+, tuned
during implementation once real playtesting numbers are available; not blocking this design).

## Out of scope

- Point-earning economy tuning (kills/battles/level per point) — already configurable, untouched.
- Achievement-gated capstones (the existing 5 BLT-unique branches' rank-4 capstones) — unrelated
  to this expansion, not touched.
- The Neocities documentation page's constellation rendering — already handles an arbitrary
  branch list and arbitrary `RequiredPerkKey` (single) today; it needs a small follow-up update to
  draw lines for *every* entry in a multi-requirement list (not just index 0) so the two-branch
  capstones visually show both incoming lines, not just one. Small, mechanical, folded into the
  implementation plan rather than treated as a separate design concern.
