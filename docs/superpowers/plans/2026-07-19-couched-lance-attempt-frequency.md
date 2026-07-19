# Plan: Increasing Couched-Lance *Attempt Frequency* for BLT Hero Agents

## 0. What changed since the last plan

The prior doc (`2026-07-19-couched-lance-blt-scoped.md`) and the shipped `CouchedLanceRewardPatch` (`MakeBltGreatAgain.cs:1875`) both **react** to a couch that naturally happens (bonus damage on a blow where `attacker.IsDoingPassiveAttack` is already true). The user's real ask is the opposite direction: make BLT heroes **enter the real native couch pose far more often**. This plan investigates whether the *frequency of the pose itself* can be raised, and if so by what lever. The reward patch is orthogonal and stays as-is.

## 1. Verdict up front (honest)

**The couched-lance pose cannot be forced or directly triggered from C#. It can only be *approximated* — we maximize the physical preconditions the native engine requires, then let the engine decide.** There is no "start couching" setter, no driven-property toggle, and no AI decision field that maps to "commit to couch." The single realistic lever we own is **scripting the hero's mount into a sustained straight-line max-speed run at an enemy** using APIs this codebase already uses. That raises the odds the native trigger fires; it does not guarantee it.

## 2. Findings (decompiled evidence)

### 2.1 The pose is 100% native, read-only, no setter
- `Agent.IsDoingPassiveAttack` — `Agent.cs:702`, `=> MBAPI.IMBAgent.GetIsDoingPassiveAttack(GetPtr())`. Getter only. The backing interface `IMBAgent.cs:606` declares only `GetIsDoingPassiveAttack(...)` — **there is no `SetIsDoingPassiveAttack`** anywhere in the 1015-file decompile (grep confirmed: the only hits are the getter, the interface decl, and read-sites in `Mission.cs`, `AttackInformation.cs`, `HighlightsController.cs`, `MissionCombatMechanicsHelper.cs`).
- A **second** native-only getter exists and is the more telling one: `Agent.IsPassiveUsageConditionsAreMet` — `Agent.cs:704`, `=> MBAPI.IMBAgent.GetIsPassiveUsageConditionsAreMet(...)` (interface `IMBAgent.cs:609`). This is the engine's own "are the conditions (speed + weapon + no conflicting input) currently satisfied?" query. It too is **read-only** — we can *observe* whether the engine considers the hero eligible, but we cannot flip it. This is useful for diagnostics/telemetry (see Phase 0) but is not a control lever.
- The couch is a "passive usage" of a weapon flagged `PassiveUsage` (`Agent.cs:296`, `WeaponFlags.PassiveUsage = 32`) — the pose is the weapon's passive mode auto-engaging, decided native-side from mount velocity + weapon + input state. None of that decision is exposed.

**Conclusion for research task #1/#2:** entry is **physics/state-gated inside the native engine**, not a C#-visible AI "decision with randomness." The threshold speed constant is not in managed code — it lives behind `GetIsPassiveUsageConditionsAreMet`. We cannot read a numeric gallop constant; we can only read the boolean verdict.

### 2.2 No AI class chooses to couch
`HumanAIComponent.cs` has **zero** references to couch/passive/brace/weapon-usage-pose selection (grep returned no matches). `BehaviorCharge.cs` (full read) only issues formation-level `MovementOrder.MovementOrderCharge` / `MovementOrderChargeToTarget` (lines 19, 34) and computes an AI weight — it never touches individual weapon pose. So the "commit to a straight gallop" behavior the user intuits is **formation movement orders + native locomotion**, and the couch is a *consequence* of speed, not an AI-selected action. There is no per-agent "hold the charge / don't veer" boolean to set.

### 2.3 The one settable AI-charge property is the wrong knob
`AiChargeHorsebackTargetDistFactor` is settable (`AgentDrivenProperties.cs:900-908`, get/set on `DrivenProperty.AiChargeHorsebackTargetDistFactor`), driven by `1.5f * (3f - num)` where `num` is AI/skill level (`AgentStatCalculateModel.cs:216`). But this governs **ranged-cavalry standoff/engagement distance**, not lance-couch commitment. Adjacent `Ai*` properties (`AiRangedHorsebackMissileRange`, `AiMinimumDistanceToContinueFactor`, `AiChargeHorsebackTargetDistFactor`) are all about *when to close/how near to approach*, none about lance pose. Tuning it might marginally affect how directly mounted AI closes, but there is no evidence it raises couch frequency, and the risk/reward is poor. **Not recommended as a primary lever** (documented so the next engineer doesn't chase it).

### 2.4 BLT heroes are AI-controlled and already scripted by this mod (research task #4)
This is the key enabler. In battles the mod already drives hero agents via scripted movement, proving they are AI-controlled agents we can command:
- `FormationBehavior.ResetAgentToNormalAI` (`MakeBltGreatAgain.cs:604-621`) calls `agent.Formation.SetMovementOrder(MovementOrder.MovementOrderCharge)` and `agent.SetScriptedPosition(...)`.
- `DuelMoveAndFight` (`:2521-2542`) already implements exactly the pattern we need: if a target is far and no enemy is near, `attacker.SetScriptedPosition(ref pos, false, ...)` toward the target and `attacker.SetTargetAgent(target)`; when close it hands back to normal AI via `SetAutomaticTargetSelection(true)` + `DisableScriptedMovement()`.
- `FollowCombat.EngageOrFollow` (`:542-557`) shows the engage-vs-move arbitration idiom (`HasEnemyNear(agent, EngageRange)`).

So the hero is a scriptable AI agent, and **the mechanism to point it at an enemy and make it run already exists in-file.**

### 2.5 We already own every API needed to force a sustained max-speed straight run
All confirmed present and used in this codebase:
- `agent.SetScriptedPosition(ref WorldPosition, bool addHumanLikeDelay, AIScriptedFrameFlags)` — `Agent.cs:2362`; used at `:2538, :2555, :4819, :4891`, etc.
- `AIScriptedFrameFlags.NeverSlowDown = 8` — `Agent.cs:169`; already used at `:4819/:4891`. **This is the critical flag** — it keeps the mount from decelerating on approach, sustaining the gallop speed the couch needs.
- `agent.SetMaximumSpeedLimit(float, bool)` — used throughout (`:3688, :4368, :4382, :4939, :5593`); calling with a high/`-1f` (uncapped) limit ensures we are not accidentally throttling the mount.
- `agent.SetTargetAgent(...)`, `agent.SetAutomaticTargetSelection(bool)`, `agent.DisableScriptedMovement()` — `:2530-2539`.
- `agent.MovementVelocity` (`Agent.cs:682`, `.Length` = speed) and `agent.WieldedWeapon.CurrentUsageItem` (used at `:3304`) for gating.
- `WeaponFlags.WideGrip` couch-lance marker — `AgentStatCalculateModel.cs:83`.
- Per-hero tick bus: `PowerHandler.Handlers.OnSlowTick += dt => { var a = hero.GetAgent(); ... }` — the exact idiom at `:3361, :3462, :4250, :4347`.

### 2.6 Why this is "approximate, not force"
Even with a perfect straight max-speed run and a `WideGrip` lance, the native engine still decides via `GetIsPassiveUsageConditionsAreMet`: it wants real velocity, a valid forward-facing target line, the rider not mid-swing/mid-block/mid-reload, and the weapon in its passive-capable usage. We can satisfy all the *managed-visible* preconditions; the final gate is native and unowned. Expect "couches much more often," **not** "couches every time."

## 3. Recommended mechanism

**A self-driven "Lancer Charge" behavior, scoped to BLT hero agents, that periodically steers a mounted, lance-equipped hero into a `NeverSlowDown` scripted run at the nearest reachable enemy whenever the hero is not already in melee.** This is the `DuelMoveAndFight` pattern (§2.4) specialized for couch conditions, wired through the existing per-hero `OnSlowTick` handler bus.

Two viable homes; pick per product intent:
- **(H1) Per-hero power** — a `Composed*`-style power (`IHeroPowerPassive.OnHeroJoinedBattle` → `OnActivation(hero, handlers, ...)`, the shape at `:3778, :3639, :4235`) that subscribes `handlers.OnSlowTick` and drives the hero's own agent. Cleanest fit if this should be an assignable/purchasable viewer power. **Recommended.**
- **(H2) Global toggle in `FormationBehavior`** — extend the existing `OnMissionTick`/`activeHeroes` loop (`:623+`) to apply the charge steering to all active BLT heroes. Fits if it should be a battle-wide rule rather than a per-hero power.

Reject as primary: native property tuning (§2.3) — wrong knob, unproven. Keep it noted only as an optional experimental Phase.

## 4. The per-tick decision (models `DuelMoveAndFight` + couch preconditions)

On each `OnSlowTick` (or the existing 0.5s `UpdateInterval`), for `var a = hero.GetAgent()`:
1. Early-out: `a == null || !a.IsActive()` → skip. Config `Enabled` gate first.
2. **BLT scope** is implicit (we start from `hero.GetAgent()`), mirroring how every power here is per-hero.
3. `a.HasMount` — else skip (couch is mounted-only for this feature).
4. Lance check: `a.WieldedWeapon.CurrentUsageItem?.WeaponFlags.HasAnyFlag(WeaponFlags.WideGrip) == true` (idiom at `:3304`, marker at `AgentStatCalculateModel.cs:83`). If not wielding a couch-capable lance, optionally attempt a wield-swap to a `WideGrip` slot if one is equipped (secondary; keep Phase-gated).
5. **Engagement arbitration** (reuse `FollowCombat.HasEnemyNear(a, EngageRange)` / `DuelMoveAndFight` logic): if an enemy is within melee/engage range, **do nothing** — hand back to normal AI (`SetAutomaticTargetSelection(true)`; `DisableScriptedMovement()`), because couching into a melee scrum is pointless and forcing a run would break normal fighting. The couch wants an *approach*, not a brawl.
6. Otherwise pick the nearest reachable enemy agent (same nearest-enemy selection already used by the aura/knockback code, e.g. the `Mission.Current.Agents` scans near `:4819/:2562`), and:
   - `a.SetMaximumSpeedLimit(-1f, false)` (uncapped) to guarantee full gallop.
   - `var wp = target.GetWorldPosition(); a.SetScriptedPosition(ref wp, false, Agent.AIScriptedFrameFlags.NeverSlowDown);` — the `NeverSlowDown` flag (`Agent.cs:169`) sustains speed through the approach, which is precisely the condition the native couch checks.
   - `a.SetTargetAgent(target)` so facing/aim aligns with the run (couch needs the lance pointed at the line of travel).
7. **Re-evaluate each tick**: when the target dies, goes out of line, or an enemy enters melee range, drop back to step 5's hand-back so the hero fights normally. Never leave a stale scripted position (the `:594` comment warns an un-cleared scripted position freezes the agent).

Optional diagnostic: log `a.IsPassiveUsageConditionsAreMet` and `a.MovementVelocity.Length` during a scripted run to empirically confirm we are actually crossing the native threshold (this is the honest way to tune, since the constant is unreadable).

## 5. Phased implementation

Each phase builds `source/MakeBltGreatAgain.cs` in Release (`dotnet build ... -c Release -p:DefineConstants=BLT_1315`, zero errors) and deploys `MakeBltGreatAgain.dll` to `BLTAdoptAHero\bin\Win64_Shipping_Client`, then verify in a real battle.

**Phase 0 — Instrumentation / prove the ceiling (no behavior change).**
Add a temporary debug log in an `OnSlowTick` for a mounted lance hero printing `IsPassiveUsageConditionsAreMet`, `IsDoingPassiveAttack`, `MovementVelocity.Length`, and whether an enemy is in engage range. *Verify:* observe in-game how often the engine already reports conditions-met vs. actually-couching in normal play. This establishes the honest baseline and confirms the native gate behavior before we build steering. (Remove or gate behind a debug flag before ship.)

**Phase 1 — Self-driven Lancer Charge, minimal (H1 per-hero power or H2 global).**
Implement §4 steps 1-7 with a new config (`Enabled`, `EngageRange`, tick cadence). Mounted + `WideGrip` only. No wield-swapping. Reuse `DuelMoveAndFight`/`FollowCombat` helpers rather than re-implementing nearest-enemy/engage logic. *Verify:* a BLT hero on a lance now repeatedly lines up and gallops straight at enemies between melee contacts; couched hits (watch for the vanilla `ui_delivered_couched_lance_damage` message, `Mission.cs:5275`, and the existing `CouchedLanceRewardPatch` firing) occur noticeably more often than baseline. Confirm the hero still fights normally once in melee (no freezing, no ignoring adjacent enemies).

**Phase 2 — Robustness & cleanup.**
Handle target death/out-of-line mid-run, ensure scripted position is always cleared on deactivation / mission over (pattern at `:594-601`, `handlers.OnMissionOver`), and make sure this coexists with `FormationBehavior` (don't fight it for control of the same agent — if the hero is under an active Formation follow/duel command, that takes precedence). *Verify:* no stuck/frozen heroes after battle end, after the target dies, or when toggling Formation commands; no lingering state next battle.

**Phase 3 — Optional wield assist.**
If the hero has a `WideGrip` lance equipped but not currently wielded, attempt a swap to it before/at the start of a charge run (guard against mid-swing interruptions). *Verify:* heroes carrying a lance in a secondary slot actually draw it and couch; confirm no weapon-flicker/AI thrash.

**Phase 4 — Optional experimental native tuning (low priority, may be dropped).**
Behind its own config flag, apply an `AgentModifierConfig` `PropertyModifierDef` experiment on `DrivenProperty.AiChargeHorsebackTargetDistFactor` (mechanism at `:3663-3668` via `BLTAgentModifierBehavior.Current?.Add`) to see if closer commitment marginally helps. *Verify:* A/B couch frequency with the flag on/off. **If no measurable effect (likely), remove it** — do not ship dead tuning.

## 6. Guardrail mapping (same regression classes as sibling plans)
1. **No new `Harmony`/`PatchAll`** — this feature is pure scripted movement on the existing per-hero `OnSlowTick` bus; it adds **no** Harmony patches at all (unlike the reward patch). Nothing to fan out.
2. **No new contour helper** — if any visual is added, use `SafeSetContourColor` only.
3. **No lazy mid-combat `AddMissionBehavior`** — reuse the existing `FormationBehavior`/`PowerHandler` infrastructure; any per-hero state is a plain field/dict on the single-threaded loop, like `activeHeroes` (`:562`).
4. **Per-tick cost** — gate cheap-first (`Enabled`, `GetAgent()==null`, `HasMount`, lance flag) before the nearest-enemy scan; run on the slow tick / 0.5s cadence (`:567`), never per-frame per-blow.

## 7. Open questions for implementation
- Empirically, does a `NeverSlowDown` scripted run reliably push `MovementVelocity.Length` past the native couch threshold? (Phase 0 answers this — it is the make-or-break measurement.)
- Best `EngageRange` so the hero couches on the approach but still disengages to fight normally (reuse/borrow `FollowCombat.EngageRange`).
- Interaction precedence with `FormationBehavior` follow/duel orders when both want to steer the same hero.
- Whether Phase 3 wield-swapping causes AI attack thrash (may not be worth it).

## 8. Honest bottom line
Frequency **can be raised**, but only by **driving the mount ourselves into the physical setup the native engine rewards** — a sustained straight `NeverSlowDown` gallop at a target with a `WideGrip` lance and no melee in range. There is **no** API to literally force the pose (`IsDoingPassiveAttack`/`IsPassiveUsageConditionsAreMet` are read-only; no AI field maps to "couch now"). Sell this to the user as "couches much more often," not "couches on command."

### Critical Files for Implementation
- `E:\BLT\source_codes\RebellionOfThePeasants\source\MakeBltGreatAgain.cs` — new Lancer-Charge power/behavior; reuse `DuelMoveAndFight` (`:2521`), `FollowCombat.EngageOrFollow`/`HasEnemyNear` (`:542`), `FormationBehavior` (`:560-621`), the `OnSlowTick`+`hero.GetAgent()` idiom (`:3361/:4250`), and `AgentModifierConfig`/`PropertyModifierDef` (`:3663`) for the optional Phase 4.
- `C:\Users\krzysiek\AppData\Local\Temp\claude\E--BLT\11ae1525-b531-445f-ba1a-99dd2e71215e\scratchpad\mb_decompiled\TaleWorlds.MountAndBlade\Agent.cs` — `IsDoingPassiveAttack` :702, `IsPassiveUsageConditionsAreMet` :704, `AIScriptedFrameFlags`/`NeverSlowDown` :163-169, `SetScriptedPosition` :2362, `MovementVelocity` :682, `WeaponFlags.PassiveUsage` :296.
- `C:\Users\krzysiek\AppData\Local\Temp\claude\E--BLT\11ae1525-b531-445f-ba1a-99dd2e71215e\scratchpad\mb_decompiled\TaleWorlds.MountAndBlade\AgentDrivenProperties.cs` — `AiChargeHorsebackTargetDistFactor` get/set :900-908 (optional Phase 4 only).
- `C:\Users\krzysiek\AppData\Local\Temp\claude\E--BLT\11ae1525-b531-445f-ba1a-99dd2e71215e\scratchpad\mb_decompiled\TaleWorlds.MountAndBlade\AgentStatCalculateModel.cs` — `WeaponFlags.WideGrip` lance marker :83; `AiChargeHorsebackTargetDistFactor` derivation :216.
- `C:\Users\krzysiek\AppData\Local\Temp\claude\E--BLT\11ae1525-b531-445f-ba1a-99dd2e71215e\scratchpad\mb_decompiled\TaleWorlds.MountAndBlade\IMBAgent.cs` — proof of no setter: only `GetIsDoingPassiveAttack` :606 / `GetIsPassiveUsageConditionsAreMet` :609.
