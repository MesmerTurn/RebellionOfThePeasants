// ══════════════════════════════════════════════════════════════════════════════
// Conquest of Doravaro — scalony mod BLT
// Zawiera: BLTFormation, BLTGuard,
//          BLTUpgrade, BLTDuel, BLTClanGold, BLTGrail, BLTAuras
// ══════════════════════════════════════════════════════════════════════════════

// ── Usings ──
using BLTAdoptAHero.Actions.Upgrades;
using BLTAdoptAHero.Achievements;
using BLTAdoptAHero.Powers;
using BLTAdoptAHero;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Rewards;
using BannerlordTwitch.UI;
using BannerlordTwitch.Util;
using BannerlordTwitch;
using HarmonyLib;
using JetBrains.Annotations;
using System.Collections.Generic;
using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;
using Xceed.Wpf.Toolkit.PropertyGrid.Editors;
using WpfColor = System.Windows.Media.Color;
using static MakeBltGreatAgain.ContourHelpers;

namespace MakeBltGreatAgain
{

#if BLT_1315
    // Realm of Thrones (ROT.dll, module "ROT-Core") has multiple spots that throw uncaught
    // NullReferenceExceptions during normal play - not our code, not fixable by editing our own
    // patches. Each entry here wraps one of ROT's own methods in a Harmony Finalizer that
    // swallows whatever it throws, turning a full game crash into a logged no-op. Purely
    // reflection-based (ROT.dll isn't a compile-time reference), and a no-op itself if ROT
    // isn't installed or a specific target method isn't found (e.g. renamed in a ROT update).
    //
    // Known targets:
    // - ROT.HarmonyPatches.Core.WarSailsPatches.OnAgentRemovedPrefix2: static cctor throws NRE
    //   the first time this runs (first battle death). Chain: Mission.OnAgentRemoved ->
    //   BattleObserverMissionLogic.OnAgentRemoved -> WarSailsPatches.OnAgentRemovedPrefix2 ->
    //   TypeInitializationException, uncaught, silent (no crash log).
    // - ROT.CampaignBehaviors.ROTTradeBoundBehavior.TryToAssignTradeBoundForVillage: NRE inside
    //   the native map distance model while assigning a village's trade bound, thrown from
    //   OnNewGameCreated - blocks starting a new campaign entirely (crash happens before
    //   character creation). Patched per-village rather than the whole UpdateTradeBounds loop,
    //   so one bad village doesn't skip trade-bound assignment for every other village.
    public static class ROTCompatGuard
    {
        private static bool _applied;

        private static readonly (string TypeName, string MethodName)[] Targets =
        {
            ("ROT.HarmonyPatches.Core.WarSailsPatches", "OnAgentRemovedPrefix2"),
            ("ROT.CampaignBehaviors.ROTTradeBoundBehavior", "TryToAssignTradeBoundForVillage"),
        };

        public static void TryApply(Harmony harmony)
        {
            if (_applied || harmony == null) return;
            _applied = true;
            try
            {
                var rotAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "ROT");
                if (rotAssembly == null) return;

                var finalizer = typeof(ROTCompatGuard).GetMethod(nameof(SwallowException),
                    BindingFlags.Static | BindingFlags.NonPublic);

                foreach (var (typeName, methodName) in Targets)
                {
                    try
                    {
                        var type = rotAssembly.GetType(typeName);
                        var method = type?.GetMethod(methodName,
                            BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (method == null)
                        {
                            Log.Info($"[MBGA] ROTCompatGuard: target not found, skipped ({typeName}.{methodName})");
                            continue;
                        }
                        harmony.Patch(method, finalizer: new HarmonyMethod(finalizer));
                        Log.Info($"[MBGA] ROTCompatGuard: patched {typeName}.{methodName}");
                    }
                    catch (Exception ex) { Log.Exception($"[MBGA] ROTCompatGuard: failed to patch {typeName}.{methodName}", ex); }
                }
            }
            catch (Exception ex) { Log.Exception("[MBGA] ROTCompatGuard.TryApply failed", ex); }
        }

        private static Exception SwallowException(Exception __exception)
        {
            if (__exception != null)
                Log.Exception("[MBGA] ROTCompatGuard suppressed a crash from ROT", __exception);
            return null;
        }
    }

    // Some settlement (almost certainly from RoT's custom map content) has a null/incomplete
    // field that native TaleWorlds.CampaignSystem.GameComponents.DefaultMapDistanceModel doesn't
    // expect. Confirmed crashing from at least two of its methods so far, via completely
    // unrelated callers each time: GetDistance (from RoT's own ROTTradeBoundBehavior, and
    // separately from Bannerlord.ButterLib's GeopoliticsBehavior) and GetNeighborsOfFortification
    // (from native Clan.ConsiderAndUpdateHomeSettlement during OnNewGameCreated). Patching one
    // method at a time as new crash reports surface doesn't scale - this class has 18 public
    // instance methods that all touch the same settlement/navigation data, any of which could
    // hit the same bad field. Instead, reflectively patches EVERY public instance method on the
    // type at once with one generic Harmony Finalizer that swallows the exception and fills in a
    // type-appropriate safe default (0/false/empty list/etc.) based on the method's own return
    // type, so callers keep running instead of the whole game crashing - regardless of which of
    // the 18 methods turns out to be the next one hit.
    public static class MapDistanceModelGuard
    {
        private static bool _applied;

        public static void TryApply(Harmony harmony)
        {
            if (_applied || harmony == null) return;
            _applied = true;
            try
            {
                var finalizer = typeof(MapDistanceModelGuard).GetMethod(nameof(Finalizer), BindingFlags.Static | BindingFlags.NonPublic);
                // Only the distance-computing methods (return float) - that's the actual crash surface
                // (ROTTradeBoundBehavior->GetDistance). Patching ALL public methods generically also
                // hit navigation-cache registration methods whose signatures (by-ref / interface out
                // params like INavigationCache) Harmony's finalizer emitter chokes on, throwing a loud
                // (caught) error at startup. Filtering to float-returning, non-by-ref methods keeps the
                // guard where it matters and drops the noise.
                var methods = typeof(DefaultMapDistanceModel).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => !m.IsSpecialName && !m.IsGenericMethodDefinition
                                && m.ReturnType == typeof(float)
                                && m.GetParameters().All(p => !p.ParameterType.IsByRef && !p.ParameterType.IsPointer));
                int patched = 0;
                foreach (var method in methods)
                {
                    try { harmony.Patch(method, finalizer: new HarmonyMethod(finalizer)); patched++; }
                    catch (Exception ex) { Log.Exception($"[MBGA] MapDistanceModelGuard: failed to patch {method.Name}", ex); }
                }
                Log.Info($"[MBGA] MapDistanceModelGuard: patched {patched} DefaultMapDistanceModel method(s)");
            }
            catch (Exception ex) { Log.Exception("[MBGA] MapDistanceModelGuard.TryApply failed", ex); }
        }

        private static Exception Finalizer(Exception __exception, MethodBase __originalMethod, ref object __result)
        {
            if (__exception == null) return null;
            var name = (__originalMethod as MethodInfo)?.Name ?? "DefaultMapDistanceModel method";
            Log.Exception($"[MBGA] MapDistanceModelGuard suppressed a crash in DefaultMapDistanceModel.{name}", __exception);

            var returnType = (__originalMethod as MethodInfo)?.ReturnType;
            if (returnType == null || returnType == typeof(void)) return null;
            if (returnType == typeof(float)) { __result = 9999f; return null; }
            if (returnType == typeof(bool)) { __result = false; return null; }
            try { __result = Activator.CreateInstance(returnType); }
            catch (Exception ex) { Log.Exception($"[MBGA] MapDistanceModelGuard: couldn't build a fallback {returnType} for {name}", ex); }
            return null;
        }
    }

    // Same "some settlement/culture from RoT's content has a null field the native code doesn't
    // expect" root cause as MapDistanceModelGuard, but hitting a DIFFERENT, unrelated native
    // system each time - after DefaultMapDistanceModel was covered, the very next crash report
    // showed the exact same NullReferenceException-during-OnNewGameCreated pattern coming from
    // TaleWorlds.CampaignSystem.CampaignBehaviors.BackstoryCampaignBehavior.OnNewGameCreated
    // instead (backstory/culture data, nothing to do with map distances). Rather than write a new
    // single-purpose guard class for every native OnNewGameCreated listener that turns out to
    // choke on RoT's data, this is a small extensible list: add a (Type, MethodName) entry here
    // and it gets the same Harmony Finalizer treatment as everything else in this file - swallow
    // the exception, fill in a type-appropriate safe default return value, log it, keep going.
    public static class NativeCrashGuard
    {
        private static bool _applied;

        // (DeclaringTypeFullName, MethodName) resolved via Type.GetType() INSIDE TryApply's own
        // try block, deliberately not typeof(...) on a static field - a static field initializer
        // runs before TryApply's try/catch even starts, so any resolution failure there would be
        // an uncaught TypeInitializationException invisible to our own logging, silently aborting
        // whatever OnGameStart code runs after this call (exactly the kind of failure this class
        // exists to prevent in OTHER code).
        // DELIBERATELY EMPTY. This used to hold skip-prefix targets on native hero-creation methods
        // (CreateHero, GetBirthAndDeathDay, CalculateNameScore, HeroInitializationArgs..ctor, etc.).
        // That was a mistake: skip-prefix UNCONDITIONALLY suppresses the method on EVERY call, even
        // on perfectly healthy data - so CreateHero (returns Hero) skip-returned null during NORMAL
        // vanilla world creation, and every downstream consumer of that null then crashed. Each such
        // crash got "fixed" with yet another skip, minting more nulls: a self-inflicted whack-a-mole.
        // Proven by the user: the older Nexus build WITHOUT these skips starts a vanilla+BLT campaign
        // cleanly. Everything moved to FinalizerTargets below (run normally, swallow ONLY on throw).
        private static readonly (string TypeName, string MethodName)[] Targets =
        {
        };

        // Top-level campaign-creation event handlers / loop passes. A Finalizer here lets the method
        // RUN normally and swallows an exception ONLY if one is actually thrown - so on healthy data
        // it is a complete no-op (identical to not patching at all), and on genuinely broken data
        // (RoT templates, corrupted save/native files) it turns a hard crash into a logged, partial
        // completion instead. One guard per OnNewGameCreated branch, never touching a leaf method, so
        // there are no null returns to relocate the crash. This is the non-destructive replacement
        // for the old skip-prefix Targets list.
        private static readonly (string TypeName, string MethodName)[] FinalizerTargets =
        {
            // Backstory assignment pass.
            ("TaleWorlds.CampaignSystem.CampaignBehaviors.BackstoryCampaignBehavior", "OnNewGameCreated"),
            // Settlement companions (AdjustEquipment / CreateCompanionAndAddToSettlement live inside).
            ("TaleWorlds.CampaignSystem.CampaignBehaviors.CompanionsCampaignBehavior", "OnNewGameCreated"),
            // Settlement Notables (elders, merchants, guildmasters).
            ("TaleWorlds.CampaignSystem.CampaignBehaviors.NotablesCampaignBehavior", "SpawnNotablesAtGameStart"),
            // Initial NPC child generation across the world at game start.
            ("TaleWorlds.CampaignSystem.CampaignBehaviors.InitialChildGenerationCampaignBehavior", "OnNewGameCreatedPartialFollowUp"),
            // RoT-only unique-wanderer spawner (type absent on vanilla -> silently skipped there).
            ("ROT.CampaignBehaviors.ROTSpawnUniqueWanderers", "SpawnHero"),
            // RoT-only minor-faction hero-from-template patch (type absent on vanilla).
            ("ROT.HarmonyPatches.CustomCompanions.HeroSpawnCampaignBehaviorPatches", "CreateMinorFactionHeroFromTemplate"),
        };

        public static void TryApply(Harmony harmony)
        {
            if (_applied || harmony == null) return;
            _applied = true;
            Log.Info($"[MBGA] NativeCrashGuard.TryApply: starting, {Targets.Length} target(s)");
            try
            {
                // Three separate attempts to patch BackstoryCampaignBehavior.OnNewGameCreated with a
                // Harmony Finalizer (which works fine on ~20 other native/third-party methods
                // elsewhere in this file) never actually registered - confirmed via crash reports'
                // own "Harmony Patches" listing showing every other guard's patch except this one,
                // across three different reflection strategies to locate the method. Rather than
                // guess a fourth time, switched to a Prefix that unconditionally skips the original
                // method (returns false) - Harmony's simplest, most basic patch type, much smaller
                // surface area for whatever is special-casing this specific method. Heavier-handed
                // than the Finalizer approach elsewhere (no hero gets a backstory assigned at all,
                // instead of only skipping the ones that would have crashed), but guaranteed to
                // prevent the crash rather than silently failing to patch a third time.
                var skipPrefix = typeof(NativeCrashGuard).GetMethod(nameof(SkipPrefix), BindingFlags.Static | BindingFlags.NonPublic);
                foreach (var (typeName, methodName) in Targets)
                {
                    try
                    {
                        var type = AppDomain.CurrentDomain.GetAssemblies()
                            .Select(a => { try { return a.GetType(typeName); } catch { return null; } })
                            .FirstOrDefault(t => t != null);
                        if (type == null)
                        {
                            Log.Info($"[MBGA] NativeCrashGuard: type not found, skipped ({typeName})");
                            continue;
                        }
                        // methodName == ".ctor" targets a constructor (e.g. a nested DTO type
                        // whose constructor itself throws) - MethodBase covers both MethodInfo
                        // and ConstructorInfo, so the same Harmony.Patch/skip-prefix path works
                        // for either once resolved.
                        MethodBase method;
                        if (methodName == ".ctor")
                            method = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault();
                        else
                            method = type.GetMethod(methodName,
                                BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (method == null)
                        {
                            Log.Info($"[MBGA] NativeCrashGuard: method not found, skipped ({type.FullName}.{methodName})");
                            continue;
                        }
                        harmony.Patch(method, prefix: new HarmonyMethod(skipPrefix));
                        Log.Info($"[MBGA] NativeCrashGuard: patched {type.FullName}.{methodName} (skip-prefix)");
                    }
                    catch (Exception ex) { Log.Exception($"[MBGA] NativeCrashGuard: failed to patch {typeName}.{methodName}", ex); }
                }

                // Finalizer-swallow targets: run the method, eat whatever it throws (see comment on
                // FinalizerTargets for why this beats leaf skip-prefix for loop-style void methods).
                var swallowFinalizer = typeof(NativeCrashGuard).GetMethod(nameof(SwallowFinalizer), BindingFlags.Static | BindingFlags.NonPublic);
                foreach (var (typeName, methodName) in FinalizerTargets)
                {
                    try
                    {
                        var type = AppDomain.CurrentDomain.GetAssemblies()
                            .Select(a => { try { return a.GetType(typeName); } catch { return null; } })
                            .FirstOrDefault(t => t != null);
                        if (type == null)
                        {
                            Log.Info($"[MBGA] NativeCrashGuard: (finalizer) type not found, skipped ({typeName})");
                            continue;
                        }
                        var method = type.GetMethod(methodName,
                            BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (method == null)
                        {
                            Log.Info($"[MBGA] NativeCrashGuard: (finalizer) method not found, skipped ({type.FullName}.{methodName})");
                            continue;
                        }
                        harmony.Patch(method, finalizer: new HarmonyMethod(swallowFinalizer));
                        Log.Info($"[MBGA] NativeCrashGuard: patched {type.FullName}.{methodName} (finalizer-swallow)");
                    }
                    catch (Exception ex) { Log.Exception($"[MBGA] NativeCrashGuard: failed to finalizer-patch {typeName}.{methodName}", ex); }
                }
            }
            catch (Exception ex) { Log.Exception("[MBGA] NativeCrashGuard.TryApply failed", ex); }
        }

        private static bool SkipPrefix(MethodBase __originalMethod)
        {
            Log.Info($"[MBGA] NativeCrashGuard: skipped {__originalMethod?.DeclaringType?.FullName}.{__originalMethod?.Name} entirely (known to crash on this data)");
            return false;
        }

        // Returning null from a Finalizer tells Harmony "the exception is handled, swallow it".
        // The original method already ran as far as it could before throwing, so partial work
        // (notables/companions created before the bad one) is kept; only the crash is discarded.
        private static Exception SwallowFinalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception != null)
                Log.Exception($"[MBGA] NativeCrashGuard: swallowed a crash from {__originalMethod?.DeclaringType?.FullName}.{__originalMethod?.Name} (broken character/culture data)", __exception);
            return null;
        }
    }
#endif

    // A torn-down agent's C# wrapper can still be non-null while its underlying native object is
    // already destroyed (very common during tournament match transitions, which teardown agents
    // fast) - calling SetContourColor on that dead native object throws AccessViolationException,
    // a NATIVE crash that try/catch cannot actually catch (confirmed via a real crash report from
    // AdrenalineMissionBehavior.Deactivate). agent.IsActive() is the required guard before ANY
    // AgentVisuals access, not just a null-conditional. Centralized here since the unsafe pattern
    // was repeated ~100 times across every aura/strike power in this file.
    public static class ContourHelpers
    {
        public static void SafeSetContourColor(Agent agent, uint? color, bool alwaysVisible)
        {
            if (agent == null || !agent.IsActive()) return;
            // MUST call the engine's AgentVisuals.SetContourColor here - calling SafeSetContourColor
            // (this method) again is infinite self-recursion -> StackOverflowException, which .NET
            // canNOT catch (the try/catch is useless against it) -> hard freeze + crash to desktop
            // with no crash report. Triggered whenever an aura power (Heal/Damage/Fear/etc.) tints an
            // agent's contour in battle. This exact bug was fixed earlier and regressed when the
            // wanderer feature was restored from an older commit that predated the fix.
            try { agent.AgentVisuals?.SetContourColor(color, alwaysVisible); } catch { }
        }
    }

    // Wspólny, assembly-wide strażnik: MBGA scala 5 modułów (MBSubModuleBase) w jednym DLL,
    // każdy z własną instancją Harmony i własnym wywołaniem PatchAll(). Bezparametrowe PatchAll()
    // skanuje CAŁĄ WYWOŁUJĄCĄ ASEMBLĘ (tu zawsze MakeBltGreatAgain.dll, niezależnie która klasa
    // je wywołuje) i patchuje WSZYSTKIE klasy [HarmonyPatch] w niej — nie tylko "swoje". Bez tego
    // strażnika 5 modułów = 5× PatchAll = każdy patch aplikowany 5 razy (np. DamageTrackingPatch na
    // Mission.RegisterBlow wykonywałby się 5× na jedno realne trafienie, mnożąc statystyki dmg).
    // Ten guard gwarantuje, że tylko PIERWSZY moduł, który się załaduje,
    // faktycznie wywoła PatchAll() — reszta pomija je jako zbędne (i tak patchowałyby to samo).
    internal static class MbgaPatchGuard
    {
        private static bool _applied;
        public static bool ShouldApply()
        {
            if (_applied) return false;
            _applied = true;
            return true;
        }
    }

    // ======================================================================
    // BLTFormation
    // ======================================================================

public class BLTFormationModule : MBSubModuleBase
    {
        private static Harmony harmony;

        public BLTFormationModule()
        {
            ActionManager.RegisterAll(typeof(BLTFormationModule).Assembly);
        }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            if (harmony != null) return;
            try
            {
                harmony = new Harmony("mod.bannerlord.bltformation");
                if (MbgaPatchGuard.ShouldApply()) harmony.PatchAll();
                Log.Info("[BLTFormation] Loaded.");
            }
            catch (Exception ex) { Log.Exception("[BLTFormation] Load failed", ex); }
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            if (mission.CombatType != Mission.MissionCombatType.Combat) return;
            mission.AddMissionBehavior(new FormationBehavior());
        }
    }

    // ── !follow — włącz podążanie za streamerem ───────────────────────────────
    [DisplayName("FormationStrimer")]
    [LocDescription("Summons your BLT hero into a formation next to the streamer during battle.")]
    public class FormationStreamerCommand : ICommandHandler
    {
        public Type HandlerConfigType => typeof(FormationSettings);

        public void Execute(ReplyContext context, object config)
        {
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (hero == null) { ActionManager.SendReply(context, "You do not have an adopted hero."); return; }
            if (Mission.Current == null || Mission.Current.CurrentState != Mission.State.Continuing)
            { ActionManager.SendReply(context, "Formation command can only be used during an active mission."); return; }
            if (!Mission.Current.IsDeploymentFinished)
            { ActionManager.SendReply(context, "Can't follow during deployment - wait for the battle to actually begin."); return; }
            var behavior = Mission.Current.GetMissionBehavior<FormationBehavior>();
            if (behavior == null) { ActionManager.SendReply(context, "Formation behavior not ready."); return; }
            if (!SummonAccess.IsHeroSummoned(hero)) { ActionManager.SendReply(context, "Your hero must be summoned in this battle."); return; }

            behavior.Activate(hero);
            ActionManager.SendReply(context, $"✓ {hero.FirstName} is now following the streamer!");
        }

        [DisplayName("Formation Settings")]
        public class FormationSettings
        {
            [DisplayName("Follow Distance (meters)")]
            [Description("Distance from the streamer — the hero stops and fights when closer than this value.")]
            public float FollowDistance { get; set; } = 4f;
        }
    }

    // ── !followhero — podążaj za innym BLT hero podczas bitwy ────────────────
    [DisplayName("FormationFollowHero")]
    [LocDescription("Makes your summoned hero follow and fight alongside another summoned BLT hero during battle. Usage: !followhero @username")]
    public class FormationFollowHeroCommand : ICommandHandler
    {
        public Type HandlerConfigType => typeof(FollowHeroSettings);

        public void Execute(ReplyContext context, object config)
        {
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (hero == null) { ActionManager.SendReply(context, "You do not have an adopted hero."); return; }
            if (Mission.Current == null || Mission.Current.CurrentState != Mission.State.Continuing)
            { ActionManager.SendReply(context, "Command can only be used during an active battle."); return; }
            if (!Mission.Current.IsDeploymentFinished)
            { ActionManager.SendReply(context, "Can't follow during deployment - wait for the battle to actually begin."); return; }
            var behavior = Mission.Current.GetMissionBehavior<FormationBehavior>();
            if (behavior == null) { ActionManager.SendReply(context, "Formation behavior not ready."); return; }
            if (!SummonAccess.IsHeroSummoned(hero)) { ActionManager.SendReply(context, "Your hero must be summoned in this battle."); return; }

            // Nazwa targetu z args (usuń @ jeśli jest)
            string targetName = context.Args != null && context.Args.Length > 0
                ? string.Join("", context.Args).TrimStart('@')
                : null;

            if (string.IsNullOrWhiteSpace(targetName))
            { ActionManager.SendReply(context, "Usage: !followhero @username"); return; }

            var targetHero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(targetName);
            if (targetHero == null)
            { ActionManager.SendReply(context, $"BLT hero '{targetName}' not found."); return; }

            if (targetHero == hero)
            { ActionManager.SendReply(context, "You cannot follow yourself."); return; }

            if (!SummonAccess.IsHeroSummoned(targetHero))
            { ActionManager.SendReply(context, $"{targetHero.FirstName} is not summoned in this battle."); return; }

            // Can't follow an enemy - same failure mode as !duel during deployment: running
            // solo across the field into a hostile formation gets you killed on arrival.
            var ownAgent = SummonAccess.GetHeroAgent(hero);
            var targetAgentForTeamCheck = SummonAccess.GetHeroAgent(targetHero);
            if (ownAgent?.Team != null && targetAgentForTeamCheck?.Team != null && ownAgent.Team != targetAgentForTeamCheck.Team)
            { ActionManager.SendReply(context, $"{targetHero.FirstName} is on the enemy side - you can't follow them."); return; }

            var s = config as FollowHeroSettings ?? new FollowHeroSettings();
            behavior.ActivateFollowHero(hero, targetHero, s.FollowDistance);
            ActionManager.SendReply(context, $"✓ {hero.FirstName} is now following {targetHero.FirstName}!");
        }

        [DisplayName("Follow Hero Settings")]
        public class FollowHeroSettings
        {
            [DisplayName("Follow Distance (meters)")]
            [Description("The hero stops and fights when closer than this value to the target.")]
            public float FollowDistance { get; set; } = 4f;
        }
    }

    // ── !follow off — wyłącz podążanie ───────────────────────────────────────
    [DisplayName("FormationStrimerOff")]
    [LocDescription("Stops your hero from following the streamer's formation or another hero.")]
    public class FormationOffCommand : ICommandHandler
    {
        public class EmptyConfig { }
        public Type HandlerConfigType => typeof(EmptyConfig);

        public void Execute(ReplyContext context, object config)
        {
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (hero == null) { ActionManager.SendReply(context, "You do not have an adopted hero."); return; }
            var behavior = Mission.Current?.GetMissionBehavior<FormationBehavior>();
            if (behavior == null) { ActionManager.SendReply(context, "Not in a mission."); return; }

            behavior.Deactivate(hero);
            ActionManager.SendReply(context, $"✓ {hero.FirstName} returned to normal AI.");
        }
    }

    // ── Mission behavior ──────────────────────────────────────────────────────
    // ── Wspólna logika "podążania z walką" ────────────────────────────────────
    // Followerzy / retinue mają aktywnie bić wrogów po drodze zamiast omijać ich
    // i biec tylko do lidera. Jeśli w pobliżu jest wróg → puść normalne AI (atakuje),
    // jeśli czysto → dogoń lidera przez scripted position.
    internal static class FollowCombat
    {
        // Promień w którym agent porzuca marsz i aktywnie atakuje wroga po drodze.
        public const float EngageRange = 9f;

        public static bool HasEnemyNear(Agent agent, float range)
        {
            if (agent?.Team == null || Mission.Current?.Agents == null) return false;
            float r2 = range * range;
            foreach (var other in Mission.Current.Agents)
            {
                if (other == null || !other.IsActive() || other.IsMount) continue;
                if (!other.IsEnemyOf(agent)) continue;
                if ((other.Position - agent.Position).LengthSquared <= r2) return true;
            }
            return false;
        }

        // Wróg blisko → walcz (auto-target, zdejmij scripted ruch); inaczej dogoń lidera.
        public static void EngageOrFollow(Agent agent, ref WorldPosition leaderPos, float distToLeader, float followDist)
        {
            if (agent == null || !agent.IsActive()) return;
            try
            {
                if (HasEnemyNear(agent, EngageRange))
                {
                    agent.SetAutomaticTargetSelection(true);
                    agent.DisableScriptedMovement();
                    return;
                }
                if (distToLeader > followDist)
                    agent.SetScriptedPosition(ref leaderPos, false, Agent.AIScriptedFrameFlags.None);
            }
            catch { }
        }
    }

    public class FormationBehavior : MissionBehavior
    {
        private readonly Dictionary<Hero, float> activeHeroes = new Dictionary<Hero, float>();
        private readonly Dictionary<Hero, float> lastUpdateTime = new Dictionary<Hero, float>();
        // Mapa: follower → target hero (gdy hero śledzi innego BLT hero zamiast strimera)
        private readonly Dictionary<Hero, (Hero target, float dist)> heroFollowTargets
            = new Dictionary<Hero, (Hero, float)>();
        private const float UpdateInterval = 0.5f;
        private const float FollowDist    = 4f;
        private const float RetinueDist   = 3f;

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        public void Activate(Hero hero)
        {
            if (hero == null) return;
            heroFollowTargets.Remove(hero); // wyczyść poprzedni follow hero jeśli był
            activeHeroes[hero] = Mission.Current?.CurrentTime ?? 0f;
        }

        public void ActivateFollowHero(Hero hero, Hero target, float followDist = 4f)
        {
            if (hero == null || target == null) return;
            heroFollowTargets[hero] = (target, followDist);
            activeHeroes[hero] = Mission.Current?.CurrentTime ?? 0f;
        }

        public void Deactivate(Hero hero)
        {
            if (hero == null) return;
            activeHeroes.Remove(hero);
            lastUpdateTime.Remove(hero);
            heroFollowTargets.Remove(hero);

            // Wyczyść scripted position — bez tego agent stoi w miejscu po dezaktywacji
            var heroAgent = SummonAccess.GetHeroAgent(hero);
            if (heroAgent != null && heroAgent.IsActive())
            {
                ResetAgentToNormalAI(heroAgent);
                foreach (var ra in SummonAccess.GetRetinueAgents(hero))
                    ResetAgentToNormalAI(ra);
            }
        }

        private static void ResetAgentToNormalAI(Agent agent)
        {
            try
            {
                // Jeśli agent ma formation → wróć do rozkazu formacji (charge/advance)
                if (agent.Formation != null)
                {
                    agent.Formation.SetMovementOrder(
                        MovementOrder.MovementOrderCharge);
                    return;
                }
                // Fallback: ustaw scripted position na aktualne miejsce ale bez żadnych flag
                // żeby agent mógł swobodnie walczyć (nie blokuje ataku)
                var cur = agent.GetWorldPosition();
                agent.SetScriptedPosition(ref cur, false, Agent.AIScriptedFrameFlags.None);
            }
            catch { }
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            if (Mission.Current == null) return;

            var streamerAgent = Mission.Current.MainAgent;
            var now = Mission.Current.CurrentTime;
            var toRemove = new List<Hero>();

            foreach (var kvp in activeHeroes)
            {
                var hero = kvp.Key;
                if (lastUpdateTime.TryGetValue(hero, out var lastTime) && now - lastTime < UpdateInterval)
                    continue;
                lastUpdateTime[hero] = now;

                var heroAgent = SummonAccess.GetHeroAgent(hero);
                if (heroAgent == null || !heroAgent.IsActive()) { toRemove.Add(hero); continue; }

                // Ustal cel: inny BLT hero lub streamer
                Agent targetAgent;
                float followDist;
                if (heroFollowTargets.TryGetValue(hero, out var followInfo))
                {
                    targetAgent = SummonAccess.GetHeroAgent(followInfo.target);
                    followDist  = followInfo.dist;
                    // Jeśli target zginął — usuń follow i wróć do normalnego AI
                    if (targetAgent == null || !targetAgent.IsActive())
                    {
                        heroFollowTargets.Remove(hero);
                        toRemove.Add(hero);
                        continue;
                    }
                }
                else
                {
                    // Brak streamera → pomiń (nie wymagaj streamera dla follow-hero mode)
                    if (streamerAgent == null || !streamerAgent.IsActive()) { toRemove.Add(hero); continue; }
                    targetAgent = streamerAgent;
                    followDist  = FollowDist;
                }

                var targetPos = targetAgent.GetWorldPosition();
                float heroDist = (heroAgent.Position - targetAgent.Position).Length;

                // Follow porusza TYLKO blthero (za mainhero/innym hero), bije wrogów po drodze.
                // Retinue podąża za blthero wyłącznie gdy aktywny jest GUARD (osobny behavior) —
                // bez guard retinue NIE leci z blthero do celu follow.
                FollowCombat.EngageOrFollow(heroAgent, ref targetPos, heroDist, followDist);
            }

            foreach (var h in toRemove)
            {
                activeHeroes.Remove(h);
                lastUpdateTime.Remove(h);
                heroFollowTargets.Remove(h);
                var ha = SummonAccess.GetHeroAgent(h);
                if (ha != null && ha.IsActive()) ResetAgentToNormalAI(ha);
            }
        }

        public override void OnAgentDeleted(Agent affectedAgent)
        {
            if (affectedAgent.IsMainAgent) { activeHeroes.Clear(); lastUpdateTime.Clear(); heroFollowTargets.Clear(); return; }
            // Jeśli zginął follower
            var dead = activeHeroes.Keys.Where(h => SummonAccess.GetHeroAgent(h) == affectedAgent).ToList();
            foreach (var h in dead) { activeHeroes.Remove(h); lastUpdateTime.Remove(h); heroFollowTargets.Remove(h); }
            // Jeśli zginął target — usuń wszystkich którzy go śledzili
            var orphaned = heroFollowTargets
                .Where(kv => SummonAccess.GetHeroAgent(kv.Value.target) == affectedAgent)
                .Select(kv => kv.Key).ToList();
            foreach (var h in orphaned) heroFollowTargets.Remove(h);
        }
    }

    // ── Shared summon access helpers ──────────────────────────────────────────
    internal static class SummonAccess
    {
        private static readonly Type SummonBehaviorType =
            typeof(BLTAdoptAHeroCampaignBehavior).Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == "BLTSummonBehavior");

        public static bool IsHeroSummoned(Hero hero)
        {
            var agent = GetHeroAgent(hero);
            return agent != null && agent.IsActive();
        }

        public static Agent GetHeroAgent(Hero hero)
        {
            var state = GetSummonState(hero);
            // CurrentAgent jest Property (nie Field) w HeroSummonState
            return GetProperty<Agent>(state, "CurrentAgent")
                ?? Mission.Current?.Agents?.FirstOrDefault(a => BLTAdoptAHero.AgentExtensions.GetHero(a) == hero);
        }

        public static IEnumerable<Agent> GetRetinueAgents(Hero hero)
        {
            var state = GetSummonState(hero);
            if (state == null) yield break;

            // Retinue = List<RetinueState>, każdy element ma POLE Agent (nie retinueAgent)
            var retinue = GetProperty<System.Collections.IEnumerable>(state, "Retinue");
            if (retinue != null)
                foreach (var entry in retinue)
                {
                    var a = GetField<Agent>(entry, "Agent");
                    if (a != null && a.IsActive()) yield return a;
                }

            // Retinue2 = List<RetinueState>, pole Agent (tak samo)
            var retinue2 = GetProperty<System.Collections.IEnumerable>(state, "Retinue2");
            if (retinue2 != null)
                foreach (var entry in retinue2)
                {
                    var a = GetField<Agent>(entry, "Agent");
                    if (a != null && a.IsActive()) yield return a;
                }
        }

        private static object GetSummonState(Hero hero)
        {
            if (SummonBehaviorType == null || Mission.Current == null) return null;
            var getMB = typeof(Mission).GetMethods()
                .FirstOrDefault(m => m.Name == "GetMissionBehavior" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            var summonBehavior = getMB?.MakeGenericMethod(SummonBehaviorType).Invoke(Mission.Current, null);
            return SummonBehaviorType.GetMethod("GetHeroSummonState")?.Invoke(summonBehavior, new object[] { hero });
        }

        private static T GetField<T>(object instance, string name)
        {
            if (instance == null) return default;
            var f = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return f == null ? default : (T)f.GetValue(instance);
        }

        private static T GetProperty<T>(object instance, string name)
        {
            if (instance == null) return default;
            var p = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null) return default;
            try { return (T)p.GetValue(instance); } catch { return default; }
        }
    }


    // ======================================================================
    // BLTGuard
    // ======================================================================

public class BLTGuardModule : MBSubModuleBase
    {
        private static Harmony harmony;

        public BLTGuardModule()
        {
            ActionManager.RegisterAll(typeof(BLTGuardModule).Assembly);
        }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            if (harmony != null) return;
            try
            {
                harmony = new Harmony("mod.bannerlord.bltguard");
                if (MbgaPatchGuard.ShouldApply()) harmony.PatchAll();
                Log.Info("BLTGuard loaded.");
            }
            catch (Exception ex) { Log.Exception("BLTGuard patch failed", ex); }
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            if (mission.CombatType != Mission.MissionCombatType.Combat) return;
            mission.AddMissionBehavior(new GuardMissionBehavior());
            mission.AddMissionBehavior(new RetreatMissionBehavior());
        }
    }

    // ── !retreat — biegnij od najbliższego wroga, unikaj walki, ale nadal
    // można cię dogonić i zabić po drodze; !retreat off wraca do normalnego AI ──
    [DisplayName("Retreat")]
    [LocDescription("Makes your summoned hero run away from the nearest enemy, avoiding combat (can still be caught and killed while fleeing). Use !retreat off to return to normal AI.")]
    public class RetreatCommand : ICommandHandler
    {
        public class RetreatConfig { }
        public Type HandlerConfigType => typeof(RetreatConfig);

        public void Execute(ReplyContext context, object config)
        {
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (hero == null) { ActionManager.SendReply(context, "You do not have an adopted hero."); return; }
            if (Mission.Current == null || Mission.Current.CurrentState != Mission.State.Continuing)
            { ActionManager.SendReply(context, "Retreat can only be used during an active battle."); return; }
            var behavior = Mission.Current.GetMissionBehavior<RetreatMissionBehavior>();
            if (behavior == null) { ActionManager.SendReply(context, "Retreat is not ready in this mission."); return; }

            string arg = (context.Args ?? "").Trim();
            if (arg.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                behavior.DeactivateRetreat(hero);
                ActionManager.SendReply(context, $"✓ {hero.FirstName} stopped retreating.");
                return;
            }

            if (!GuardSummonAccess.IsHeroSummoned(hero))
            { ActionManager.SendReply(context, "Your hero must be summoned in this battle."); return; }

            behavior.ActivateRetreat(hero);
            ActionManager.SendReply(context, $"🏃 {hero.FirstName} is retreating from battle!");
        }
    }

    public class RetreatMissionBehavior : MissionBehavior
    {
        private readonly HashSet<Hero> activeRetreats = new HashSet<Hero>();
        private float _lastTickTime = 0f;
        private const float TickInterval = 0.5f;
        private const float FleeDistance = 30f;

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        public void ActivateRetreat(Hero hero)
        {
            if (hero == null) return;
            activeRetreats.Add(hero);
        }

        public void DeactivateRetreat(Hero hero)
        {
            if (hero == null) return;
            activeRetreats.Remove(hero);
            var agent = GuardSummonAccess.GetHeroAgent(hero);
            if (agent == null || !agent.IsActive()) return;
            try
            {
                agent.SetAutomaticTargetSelection(true);
                if (agent.Formation != null)
                {
                    agent.Formation.SetMovementOrder(MovementOrder.MovementOrderCharge);
                }
                else
                {
                    var cur = agent.GetWorldPosition();
                    agent.SetScriptedPosition(ref cur, false, Agent.AIScriptedFrameFlags.None);
                }
            }
            catch { }
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            if (activeRetreats.Count == 0 || Mission.Current == null) return;

            float now = Mission.Current.CurrentTime;
            if (now - _lastTickTime < TickInterval) return;
            _lastTickTime = now;

            var toRemove = new List<Hero>();
            foreach (var hero in activeRetreats)
            {
                var agent = GuardSummonAccess.GetHeroAgent(hero);
                if (agent == null || !agent.IsActive()) { toRemove.Add(hero); continue; }

                try
                {
                    // Find the nearest enemy to flee away from
                    Agent nearestEnemy = null;
                    float nearestDistSq = float.MaxValue;
                    foreach (var other in Mission.Current.Agents)
                    {
                        if (other == null || !other.IsActive() || other.IsMount || !other.IsEnemyOf(agent)) continue;
                        float dSq = (other.Position - agent.Position).LengthSquared;
                        if (dSq < nearestDistSq) { nearestDistSq = dSq; nearestEnemy = other; }
                    }

                    if (nearestEnemy == null) continue; // nothing to flee from right now, keep the flag active

                    agent.SetAutomaticTargetSelection(false); // don't fight back while fleeing

                    var away = agent.Position.AsVec2 - nearestEnemy.Position.AsVec2;
                    if (away.LengthSquared < 0.01f) away = new Vec2(1f, 0f);
                    away.Normalize();

                    var wp = agent.GetWorldPosition();
                    wp.SetVec2(agent.Position.AsVec2 + away * FleeDistance);
                    agent.SetScriptedPosition(ref wp, false, Agent.AIScriptedFrameFlags.None);
                }
                catch { }
            }

            foreach (var h in toRemove) activeRetreats.Remove(h);
        }

        public override void OnAgentDeleted(Agent affectedAgent)
        {
            var heroOfAgent = activeRetreats.FirstOrDefault(h => GuardSummonAccess.GetHeroAgent(h) == affectedAgent);
            if (heroOfAgent != null) activeRetreats.Remove(heroOfAgent);
        }
    }

    [DisplayName("Guard")]
    [LocDescription("Makes your summoned hero guard and stay close to another summoned hero during battle, helping fend off attackers. Use !guard off to stop.")]
    public class GuardCommand : ICommandHandler
    {
        public class GuardConfig { }
        public Type HandlerConfigType => typeof(GuardConfig);

        public void Execute(ReplyContext context, object config)
        {
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (hero == null) { ActionManager.SendReply(context, "You do not have an adopted hero."); return; }
            if (Mission.Current == null || Mission.Current.CurrentState != Mission.State.Continuing)
            { ActionManager.SendReply(context, "Guard can only be used during an active battle."); return; }
            var behavior = Mission.Current.GetMissionBehavior<GuardMissionBehavior>();
            if (behavior == null) { ActionManager.SendReply(context, "Guard is not ready in this mission."); return; }

            string arg = (context.Args ?? "").Trim();
            if (arg.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                behavior.DeactivateGuard(hero);
                ActionManager.SendReply(context, $"✓ {hero.FirstName}'s retinue stopped guarding them.");
                return;
            }

            if (!Mission.Current.IsDeploymentFinished)
            { ActionManager.SendReply(context, "Can't activate guard during deployment - wait for the battle to actually begin."); return; }

            if (!GuardSummonAccess.IsHeroSummoned(hero))
            { ActionManager.SendReply(context, "Your hero must be summoned in this battle."); return; }

            behavior.ActivateGuard(hero);
            ActionManager.SendReply(context, $"✓ {hero.FirstName}'s retinue is now guarding them!");
        }
    }

    public class GuardMissionBehavior : MissionBehavior
    {
        private readonly HashSet<Hero> activeGuards = new HashSet<Hero>();
        private float _lastTickTime = 0f;
        private const float TickInterval = 0.5f;
        private const float GuardRadius  = 3f;

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        public void ActivateGuard(Hero hero)
        {
            if (hero == null) return;
            activeGuards.Add(hero);
            var retinue = GuardSummonAccess.GetRetinueAgents(hero);
            Log.Info($"[BLTGuard v0.6] ActivateGuard: {hero.FirstName}, retinueCount={retinue.Count}");
        }

        public void DeactivateGuard(Hero hero)
        {
            if (hero == null) return;
            activeGuards.Remove(hero);
            Log.Info($"[BLTGuard v0.6] DeactivateGuard: {hero.FirstName}");
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            if (Mission.Current == null) return;

            var now = Mission.Current.CurrentTime;
            if (now - _lastTickTime < TickInterval) return;
            _lastTickTime = now;

            var toRemove = new List<Hero>();

            foreach (var hero in activeGuards)
            {
                var heroAgent = GuardSummonAccess.GetHeroAgent(hero);
                if (heroAgent == null || !heroAgent.IsActive()) { toRemove.Add(hero); continue; }

                var retinue = GuardSummonAccess.GetRetinueAgents(hero).ToList();
                var heroPos = heroAgent.GetWorldPosition();

                foreach (var agent in retinue)
                {
                    try
                    {
                        float dist = (agent.Position - heroAgent.Position).Length;
                        // Guard retinue: bije wrogów po drodze, dogania hero gdy czysto
                        FollowCombat.EngageOrFollow(agent, ref heroPos, dist, GuardRadius);
                    }
                    catch { }
                }
            }

            foreach (var h in toRemove) activeGuards.Remove(h);
        }

        public override void OnAgentDeleted(Agent affectedAgent)
        {
            var heroOfAgent = activeGuards.FirstOrDefault(h => GuardSummonAccess.GetHeroAgent(h) == affectedAgent);
            if (heroOfAgent != null) activeGuards.Remove(heroOfAgent);
        }
    }

    // ── Reflection helpers ────────────────────────────────────────────────────
    internal static class GuardSummonAccess
    {
        private static readonly Type SummonBehaviorType =
            typeof(BLTAdoptAHeroCampaignBehavior).Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == "BLTSummonBehavior");

        public static bool IsHeroSummoned(Hero hero)
        {
            var agent = GetHeroAgent(hero);
            return agent != null && agent.IsActive();
        }

        public static Agent GetHeroAgent(Hero hero)
        {
            var state = GetSummonState(hero);
            // CurrentAgent jest Property (nie Field) w HeroSummonState
            return GetProperty<Agent>(state, "CurrentAgent")
                ?? Mission.Current?.Agents?.FirstOrDefault(a => BLTAdoptAHero.AgentExtensions.GetHero(a) == hero);
        }

        public static List<Agent> GetRetinueAgents(Hero hero)
        {
            var result = new List<Agent>();
            var state = GetSummonState(hero);
            if (state == null) return result;

            // Retinue = List<RetinueState>, każdy element ma POLE Agent (nie retinueAgent)
            var retinue = GetProperty<System.Collections.IEnumerable>(state, "Retinue");
            if (retinue != null)
                foreach (var entry in retinue)
                {
                    var a = GetField<Agent>(entry, "Agent");
                    if (a != null && a.IsActive()) result.Add(a);
                }

            // Retinue2 = List<RetinueState>, pole Agent (tak samo)
            var retinue2 = GetProperty<System.Collections.IEnumerable>(state, "Retinue2");
            if (retinue2 != null)
                foreach (var entry in retinue2)
                {
                    var a = GetField<Agent>(entry, "Agent");
                    if (a != null && a.IsActive()) result.Add(a);
                }

            return result;
        }

        private static object GetSummonState(Hero hero)
        {
            if (SummonBehaviorType == null || Mission.Current == null) return null;
            var getMB = typeof(Mission).GetMethods()
                .FirstOrDefault(m => m.Name == "GetMissionBehavior" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            var summonBehavior = getMB?.MakeGenericMethod(SummonBehaviorType).Invoke(Mission.Current, null);
            return SummonBehaviorType.GetMethod("GetHeroSummonState")?.Invoke(summonBehavior, new object[] { hero });
        }

        private static T GetField<T>(object instance, string name)
        {
            if (instance == null) return default;
            var f = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return f == null ? default : (T)f.GetValue(instance);
        }

        private static T GetProperty<T>(object instance, string name)
        {
            if (instance == null) return default;
            var p = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null) return default;
            try { return (T)p.GetValue(instance); } catch { return default; }
        }
    }


    [HarmonyPatch]
    internal static class DamageTrackingPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Mission), "RegisterBlow")]
        private static void RegisterBlowPrefix(Agent attacker, Agent victim, WeakGameEntity realHitEntity, ref Blow b,
            ref AttackCollisionData collisionData, in MissionWeapon attackerWeapon)
        {
            try
            {
                var atkHero = attacker?.GetAdoptedHero();
                if (atkHero != null) BLTDamageTracker.Add(atkHero, b.InflictedDamage);
            }
            catch (Exception ex)
            {
                Log.Exception("DamageTrackingPatch failed", ex);
            }
        }
    }

    // Samodzielny system "Normalize Armor" dla turniejów, działający jako Postfix na natywnej metodzie
    // TournamentFightMissionController.AddRandomClothes - NIE zależy od żadnych fork-owych metod
    // (ApplyNormalizedArmor/BuildClassLoadout to metody dodane WYŁĄCZNIE w naszym forku, nie istnieją
    // w oryginalnym DLL Randomchair22 - stąd Postfix na natywnej metodzie zamiast Prefix na nich).
    // Uruchamia się zawsze PO oryginale (i po ewentualnym własnym NormalizeArmor forka, jeśli DLL jest
    // forkiem), więc jego wynik jest ostateczny - nadpisuje slot pancerza preferując kulturę bohatera.
    public class MBGATournamentLoadoutConfig
    {
        private const string ID = "MBGA - Tournament Loadout";
        internal static void Register() => ActionManager.RegisterGlobalConfigType(ID, typeof(MBGATournamentLoadoutConfig));
        internal static MBGATournamentLoadoutConfig Get() => ActionManager.GetGlobalConfig<MBGATournamentLoadoutConfig>(ID);

        [DisplayName("Normalize Armor"), Description("Give tournament participants matching armor at a fixed tier, preferring each hero's own culture. Standalone MBGA re-implementation of the BLT tournament armor normalization feature."), UsedImplicitly]
        public bool NormalizeArmor { get; set; } = false;

        [DisplayName("Normalize Armor Tier"), Description("Armor tier (1-6) used when Normalize Armor is enabled."), UsedImplicitly]
        public int NormalizeArmorTier { get; set; } = 4;
    }

    // DISABLED in the 1.3.15 Warsails line: tournament armor normalization was a TOR-specific feature
    // and the fork's own BLTAdoptAHero already provides it (ApplyNormalizedArmor/BuildClassLoadout).
    // Class-level [HarmonyPatch] removed so PatchAll skips it; the code stays dormant. (Belongs to the
    // separate hidden TOR package, not here.)
    internal static class TournamentCulturePatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SandBox.Tournaments.MissionLogics.TournamentFightMissionController), "AddRandomClothes")]
        private static void AddRandomClothesPostfix(CultureObject culture,
            TaleWorlds.CampaignSystem.TournamentGames.TournamentParticipant participant)
        {
            try
            {
                var cfg = MBGATournamentLoadoutConfig.Get();
                if (cfg == null || !cfg.NormalizeArmor || participant == null) return;

                var heroCulture = participant.Character?.HeroObject?.Culture ?? culture;
                int tier = Math.Max(0, Math.Min(5, cfg.NormalizeArmorTier - 1));
                foreach (var (slot, itemType) in SkillGroup.ArmorIndexType)
                {
                    var item = SelectRandomItemNearestTier(
                                   CampaignHelpers.AllItems.Where(i => i.Culture == heroCulture && i.ItemType == itemType), tier)
                               ?? SelectRandomItemNearestTier(
                                   CampaignHelpers.AllItems.Where(i => i.ItemType == itemType), tier);
                    if (item != null)
                        participant.MatchEquipment[slot] = new EquipmentElement(item);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("TournamentCulturePatch.AddRandomClothesPostfix failed", ex);
            }
        }

        // EquipHero i GlobalCommonConfig (BLTAdoptAHero) sa klasami internal, wiec ich
        // SelectRandomItemNearestTier / RestrictedItemIds nie sa dostepne z MBGA - lokalna
        // reimplementacja tej samej logiki wyboru tieru (bez filtra restricted-item, ktory
        // wymagalby dostepu do internal configu forka), zeby dzialac niezaleznie od DLL-a BLT.
        private static ItemObject SelectRandomItemNearestTier(IEnumerable<ItemObject> items, int tier)
        {
            var allowed = items.ToList();
            var atOrBelow = allowed.Where(i => (int)i.Tier <= tier).ToList();
            if (atOrBelow.Count > 0)
                return atOrBelow.GroupBy(item => (int)item.Tier).OrderByDescending(g => g.Key).FirstOrDefault()?.SelectRandom();
            return allowed.GroupBy(item => (int)item.Tier).OrderBy(g => g.Key).FirstOrDefault()?.SelectRandom();
        }
    }

    // Samodzielny refund za "!retinue clear" - fork zwraca poziom z KillRetinueAtIndex (int) i liczy
    // zwrot z wlasnych kosztow tierow, ale oryginalny (niezmodyfikowany) DLL Randomchair22 ma
    // KillRetinueAtIndex zwracajace void - nie da sie odczytac poziomu ani po fakcie. Zamiast tego
    // czytamy CharacterObject.Tier jednostki PRZED usunieciem (Prefix) jako przyblizenie "poziomu"
    // retinue, i liczymy zwrot z wlasnej (konfigurowalnej) tabeli kosztow tierow.
    public class MBGARetinueRefundConfig
    {
        private const string ID = "MBGA - Retinue Refund";
        internal static void Register() => ActionManager.RegisterGlobalConfigType(ID, typeof(MBGARetinueRefundConfig));
        internal static MBGARetinueRefundConfig Get() => ActionManager.GetGlobalConfig<MBGARetinueRefundConfig>(ID);

        [DisplayName("Enabled"), Description("Refund a fraction of gold spent when a retinue member is cleared with !retinue clear. Standalone re-implementation for an UNMODIFIED BLTAdoptAHero.dll (vanilla Randomchair22 build), since the exact gold spent isn't readable from outside - refund is estimated from the troop's own tier. Off by default: leave disabled if you're running the MesmerTurn fork, which already refunds this natively - enabling both would double the refund."), UsedImplicitly]
        public bool Enabled { get; set; } = false;

        [DisplayName("Refund Fraction"), Description("Fraction (0-1) of the estimated tier cost refunded on clear. 0.333 = a third, matching the original fork behavior."), UsedImplicitly]
        public float RefundFraction { get; set; } = 0.333f;

        [DisplayName("Cost Tier 1"), Description("Estimated gold cost for a Tier 1 retinue member (used only to compute the refund)."), UsedImplicitly]
        public int CostTier1 { get; set; } = 25000;
        [DisplayName("Cost Tier 2"), Description("Estimated gold cost for a Tier 2 retinue member."), UsedImplicitly]
        public int CostTier2 { get; set; } = 50000;
        [DisplayName("Cost Tier 3"), Description("Estimated gold cost for a Tier 3 retinue member."), UsedImplicitly]
        public int CostTier3 { get; set; } = 100000;
        [DisplayName("Cost Tier 4"), Description("Estimated gold cost for a Tier 4 retinue member."), UsedImplicitly]
        public int CostTier4 { get; set; } = 175000;
        [DisplayName("Cost Tier 5"), Description("Estimated gold cost for a Tier 5 retinue member."), UsedImplicitly]
        public int CostTier5 { get; set; } = 275000;
        [DisplayName("Cost Tier 6+"), Description("Estimated gold cost for a Tier 6 (or higher) retinue member."), UsedImplicitly]
        public int CostTier6 { get; set; } = 400000;

        internal int TotalCostUpToTier(int tier)
        {
            int[] costs = { CostTier1, CostTier2, CostTier3, CostTier4, CostTier5, CostTier6 };
            int total = 0;
            for (int i = 0; i < tier && i < costs.Length; i++) total += costs[i];
            if (tier > costs.Length) total += (tier - costs.Length) * CostTier6;
            return total;
        }
    }

    [HarmonyPatch]
    internal static class RetinueClearRefundPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLTAdoptAHeroCampaignBehavior), "KillRetinueAtIndex")]
        private static void KillRetinueAtIndexPrefix(Hero retinueOwnerHero, int index)
        {
            try
            {
                var cfg = MBGARetinueRefundConfig.Get();
                if (cfg == null || !cfg.Enabled || retinueOwnerHero == null) return;

                var troop = BLTAdoptAHeroCampaignBehavior.Current?.GetRetinue(retinueOwnerHero)?.ElementAtOrDefault(index);
                if (troop == null) return;

                int tier = Math.Max(0, troop.Tier);
                int refund = (int)(cfg.TotalCostUpToTier(tier) * cfg.RefundFraction);
                if (refund > 0)
                    BLTAdoptAHeroCampaignBehavior.Current.ChangeHeroGold(retinueOwnerHero, refund);
            }
            catch (Exception ex)
            {
                Log.Exception("RetinueClearRefundPatch.KillRetinueAtIndexPrefix failed", ex);
            }
        }
    }

    // BLTClanDiplomacyBehavior.IsLanded w oryginalnym DLL sprawdza tylko wlasne Fiefs klanu - klan
    // ktory stracil wszystkie ziemie, ale ma zwasala z ziemia, jest blednie liczony jako "bezziemny"
    // (np. zle liczy limit sojuszy MaxClanAlliances i zle wykrywa utrate ziemi w CheckFiefLoss, bo obie
    // metody wewnetrznie wolaja IsLanded). Fork poprawia to uwzgledniajac zwasali - port jako pelna
    // podmiana metody (Prefix + skip original), bo IsLanded jest public static wiec mozna ja wolac
    // bezposrednio, a naprawa propaguje sie automatycznie do wszystkich wewnetrznych wywolan w tej klasie.
    [HarmonyPatch]
    internal static class DiplomacyVassalFixPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLTClanDiplomacyBehavior), "IsLanded")]
        private static bool IsLandedPrefix(Clan c, ref bool __result)
        {
            try
            {
                if (c == null) { __result = false; return false; }
                if (c.Fiefs != null && c.Fiefs.Count > 0) { __result = true; return false; }

                var vassals = VassalBehavior.Current?.GetVassalClans(c);
                if (vassals != null)
                {
                    foreach (var v in vassals)
                    {
                        if (v?.Fiefs != null && v.Fiefs.Count > 0) { __result = true; return false; }
                    }
                }
                __result = false;
                return false;
            }
            catch (Exception ex)
            {
                Log.Exception("DiplomacyVassalFixPatch.IsLandedPrefix failed", ex);
                return true; // fall back to original on error
            }
        }
    }

    // Samodzielny system Prestige - fork ma cala te funkcje (!prestige, BLTAdoptAHeroCampaignBehavior
    // .GetPrestigeLevel/DoPrestige/itd, 3 patche staty) jako wlasny dodatek, ktorego NIE MA w ogole w
    // oryginalnym DLL Randomchair22 - metody te po prostu nie istnieja tam, wiec nie mozna ich wolac ani
    // patchowac. Wlasna, niezalezna reimplementacja: wlasne przechowywanie poziomu prestizu (JSON, jak
    // WandererRecord), wlasne liczenie killi, wlasna komenda "!mbgaprestige", wlasne bonusy staty (HP/
    // obrazenia/pancerz) naklejone na te same natywne metody co fork patchuje. Nie resetuje sprzetu do T1
    // przy prestizu (EquipHero.UpgradeEquipment jest internal w BLTAdoptAHero - niedostepne z zewnatrz).
    public class MBGAPrestigeRecord
    {
        public int Level { get; set; } = 0;
        public int KillsSincePrestige { get; set; } = 0;
    }

    public class BLTMBGAPrestigeBehavior : CampaignBehaviorBase
    {
        public static BLTMBGAPrestigeBehavior Current => Campaign.Current?.GetCampaignBehavior<BLTMBGAPrestigeBehavior>();

        private Dictionary<string, MBGAPrestigeRecord> records = new Dictionary<string, MBGAPrestigeRecord>();

        public override void RegisterEvents() { }

        public override void SyncData(IDataStore dataStore)
        {
            string json = dataStore.IsSaving
                ? Newtonsoft.Json.JsonConvert.SerializeObject(records ?? new Dictionary<string, MBGAPrestigeRecord>())
                : null;
            dataStore.SyncData("MBGAPrestigeJson", ref json);
            if (!dataStore.IsSaving)
            {
                records = string.IsNullOrEmpty(json)
                    ? new Dictionary<string, MBGAPrestigeRecord>()
                    : (Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, MBGAPrestigeRecord>>(json)
                       ?? new Dictionary<string, MBGAPrestigeRecord>());
            }
            if (records == null) records = new Dictionary<string, MBGAPrestigeRecord>();
        }

        public MBGAPrestigeRecord GetOrCreate(Hero hero)
        {
            if (hero == null) return new MBGAPrestigeRecord();
            if (!records.TryGetValue(hero.StringId, out var rec))
            {
                rec = new MBGAPrestigeRecord();
                records[hero.StringId] = rec;
            }
            return rec;
        }

        public static int GetLevel(Hero hero)
            => hero != null && Current != null && Current.records.TryGetValue(hero.StringId, out var r) ? r.Level : 0;

        public void AddKill(Hero hero)
        {
            if (hero == null) return;
            GetOrCreate(hero).KillsSincePrestige++;
        }
    }

    public class MBGAPrestigeConfig
    {
        private const string ID = "MBGA - Prestige";
        internal static void Register() => ActionManager.RegisterGlobalConfigType(ID, typeof(MBGAPrestigeConfig));
        internal static MBGAPrestigeConfig Get() => ActionManager.GetGlobalConfig<MBGAPrestigeConfig>(ID);

        [DisplayName("Enabled"), Description("Standalone re-implementation of the fork's Prestige system for an UNMODIFIED BLTAdoptAHero.dll. Off by default - leave disabled if running the MesmerTurn fork, which already has its own !prestige command."), UsedImplicitly]
        public bool Enabled { get; set; } = false;

        [DisplayName("Min Equipment Tier Required"), Description("Hero must be at least this equipment tier (1-8) to prestige."), UsedImplicitly]
        public int MinTierRequired { get; set; } = 7;

        [DisplayName("Required Kills"), Description("Kills needed since the last prestige (or since hire) before prestiging again. 0 = no kill requirement."), UsedImplicitly]
        public int RequireKills { get; set; } = 200;

        [DisplayName("Max Prestige Level"), Description("Maximum prestige level obtainable."), UsedImplicitly]
        public int MaxPrestigeLevel { get; set; } = 5;

        [DisplayName("Gold Per Kill Bonus % Per Level"), Description("Extra gold-per-kill percent bonus, per prestige level (informational only in this standalone version - not wired into a gold-per-kill reward)."), UsedImplicitly]
        public int GoldPerKillBonusPercentPerLevel { get; set; } = 15;

        [DisplayName("Damage Bonus % Per Level"), Description("Extra outgoing damage percent, per prestige level, cumulative."), UsedImplicitly]
        public int DamageBonusPercentPerLevel { get; set; } = 5;

        [DisplayName("Armor Bonus Per Level"), Description("Extra flat armor effectiveness, per prestige level, cumulative."), UsedImplicitly]
        public int ArmorBonusPerLevel { get; set; } = 2;

        [DisplayName("HP Bonus Per Level"), Description("Extra flat max HP, per prestige level, cumulative."), UsedImplicitly]
        public int HPBonusPerLevel { get; set; } = 20;

        [DisplayName("Chat Title Symbol"), Description("Symbol repeated per prestige level and shown before the hero's name (e.g. 3 stars at P3)."), UsedImplicitly]
        public string ChatTitleSymbol { get; set; } = "★"; // ★

        internal string GetChatTitle(int level) => level <= 0 ? "" : string.Concat(Enumerable.Repeat(ChatTitleSymbol, level));
    }

    [LocDescription("Resets your hero to a higher Prestige level once you meet the equipment tier and kill requirements, granting permanent HP/damage/armor bonuses. Standalone MBGA version - disabled by default, see \"MBGA - Prestige\" in Global Configs.")]
    public class MBGAPrestigeCommand : ICommandHandler
    {
        public class Settings { }
        public Type HandlerConfigType => typeof(Settings);

        public void Execute(ReplyContext context, object config)
        {
            var cfg = MBGAPrestigeConfig.Get();
            if (cfg == null || !cfg.Enabled) { ActionManager.SendReply(context, "MBGA Prestige is disabled."); return; }

            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (hero == null) { ActionManager.SendReply(context, "You don't have an adopted hero."); return; }

            var behavior = BLTMBGAPrestigeBehavior.Current;
            if (behavior == null) { ActionManager.SendReply(context, "Prestige system not ready."); return; }

            var rec = behavior.GetOrCreate(hero);
            string title = cfg.GetChatTitle(rec.Level);
            string titlePrefix = string.IsNullOrEmpty(title) ? "" : $"{title} ";

            if (rec.Level >= cfg.MaxPrestigeLevel)
            {
                ActionManager.SendReply(context, $"{titlePrefix}P{rec.Level} MAX prestige reached.");
                return;
            }

            int currentTier = (BLTAdoptAHeroCampaignBehavior.Current?.GetEquipmentTier(hero) ?? -1) + 1; // 1-based
            bool tierOk = currentTier >= cfg.MinTierRequired;
            bool killsOk = cfg.RequireKills <= 0 || rec.KillsSincePrestige >= cfg.RequireKills;
            int nextLevel = rec.Level + 1;

            if (!tierOk)
            {
                ActionManager.SendReply(context, $"{titlePrefix}P{rec.Level} -> P{nextLevel} | Need T{cfg.MinTierRequired} (current T{currentTier}).");
                return;
            }
            if (!killsOk)
            {
                ActionManager.SendReply(context, $"{titlePrefix}P{rec.Level} -> P{nextLevel} | Need {cfg.RequireKills - rec.KillsSincePrestige} more kills ({rec.KillsSincePrestige}/{cfg.RequireKills}).");
                return;
            }
            if (Mission.Current != null)
            {
                ActionManager.SendReply(context, "Cannot prestige during an active battle!");
                return;
            }

            rec.Level = nextLevel;
            rec.KillsSincePrestige = 0;
            string newTitle = cfg.GetChatTitle(rec.Level);
            ActionManager.SendReply(context, $"{newTitle} {context.UserName} prestiged to P{rec.Level}! +{cfg.DamageBonusPercentPerLevel * rec.Level}% damage, +{cfg.ArmorBonusPerLevel * rec.Level} armor, +{cfg.HPBonusPerLevel * rec.Level} HP.");
        }
    }

    internal class MBGAPrestigeKillTrackerBehavior : MissionBehavior
    {
        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            try
            {
                var cfg = MBGAPrestigeConfig.Get();
                if (cfg == null || !cfg.Enabled) return;
                if (agentState != AgentState.Killed && agentState != AgentState.Unconscious) return;
                if (affectorAgent == null || affectedAgent == null || affectorAgent == affectedAgent) return;
                if (affectorAgent.Team != null && affectedAgent.Team != null && affectorAgent.Team.IsFriendOf(affectedAgent.Team)) return;

                var killerHero = affectorAgent.GetAdoptedHero();
                if (killerHero == null) return;

                BLTMBGAPrestigeBehavior.Current?.AddKill(killerHero);
            }
            catch (Exception ex)
            {
                Log.Exception("MBGAPrestigeKillTrackerBehavior.OnAgentRemoved failed", ex);
            }
        }
    }

    // DISABLED in the 1.3.15 Warsails line: Tier 7/8 is handled by the fork's own !equip command
    // (Elite/Legendary tiers with costs). This standalone MBGA Tier 7-8 system (costs + stat
    // multipliers) duplicated the equip side, so it's removed here. Class-level [HarmonyPatch] gone
    // -> PatchAll skips it; the stat-multiplier patches no longer apply. Code stays dormant.
    internal static class MBGAPrestigeStatPatches
    {
        // Patchowanie Agent.BaseHealthLimit/GetBaseArmorEffectivenessForBodyPart (nawet z pustym
        // cialem, gdy Enabled=false) psuje renderowanie podgladu w character creation - agent
        // podgladowy tam nie jest w pelni "prawdziwym" agentem. Prepare() to wbudowany hook Harmony:
        // zwrocenie false oznacza calkowite pominiecie patchowania tej klasy, wiec przy domyslnym
        // Enabled=false (99% instalacji) te gettery w ogole nie sa dotykane. Jesli funkcja jest
        // wlaczona, wymaga to restartu gry zeby patch sie zaaplikowal (Prepare() liczy sie raz,
        // przy OnSubModuleLoad) - akceptowalny kompromis wobec psucia character creation domyslnie.
        private static bool Prepare() => MBGAPrestigeConfig.Get()?.Enabled == true;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Agent), "BaseHealthLimit", MethodType.Getter)]
        private static void HealthPostfix(Agent __instance, ref float __result)
        {
            try
            {
                var cfg = MBGAPrestigeConfig.Get();
                if (cfg == null || !cfg.Enabled) return;
                if (__instance == null || !__instance.IsActive()) return;
                var hero = (__instance.Character as CharacterObject)?.HeroObject;
                if (hero == null) return;
                int level = BLTMBGAPrestigeBehavior.GetLevel(hero);
                if (level > 0) __result += cfg.HPBonusPerLevel * level;
            }
            catch (Exception ex) { Log.Exception("MBGAPrestigeStatPatches.HealthPostfix failed", ex); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Mission), "RegisterBlow")]
        private static void DamagePrefix(Agent attacker, ref Blow b)
        {
            try
            {
                var cfg = MBGAPrestigeConfig.Get();
                if (cfg == null || !cfg.Enabled || attacker == null || !attacker.IsActive()) return;
                var hero = attacker.GetAdoptedHero();
                if (hero == null) return;
                int level = BLTMBGAPrestigeBehavior.GetLevel(hero);
                if (level <= 0) return;
                float mult = 1f + (cfg.DamageBonusPercentPerLevel * level) / 100f;
                b.BaseMagnitude *= mult;
                b.InflictedDamage = (int)(b.InflictedDamage * mult);
            }
            catch (Exception ex) { Log.Exception("MBGAPrestigeStatPatches.DamagePrefix failed", ex); }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Agent), "GetBaseArmorEffectivenessForBodyPart")]
        private static void ArmorPostfix(Agent __instance, ref float __result)
        {
            try
            {
                var cfg = MBGAPrestigeConfig.Get();
                if (cfg == null || !cfg.Enabled) return;
                if (__instance == null || !__instance.IsActive()) return;
                var hero = (__instance.Character as CharacterObject)?.HeroObject;
                if (hero == null) return;
                int level = BLTMBGAPrestigeBehavior.GetLevel(hero);
                if (level > 0) __result += cfg.ArmorBonusPerLevel * level;
            }
            catch (Exception ex) { Log.Exception("MBGAPrestigeStatPatches.ArmorPostfix failed", ex); }
        }
    }

    // Mission-global, agent -> outgoing-damage-bonus-percent lookup. Populated/cleared by
    // ComposedAuraPower's Buff/Debuff block (see docs/superpowers/plans/
    // 2026-07-17-composed-aura-buff-debuff.md). Deliberately a plain static Dictionary, not a
    // MissionBehavior - the game loop is single-threaded so no locking is needed, and this avoids
    // ever needing to register a new MissionBehavior from inside a combat callback (the "Collection
    // was modified" regression class from earlier this session).
    internal static class ComposedAuraDamageTracker
    {
        private static readonly Dictionary<Agent, float> bonuses = new Dictionary<Agent, float>();
        public static bool IsEmpty => bonuses.Count == 0;
        public static void Set(Agent a, float pct) { if (a != null) bonuses[a] = pct; }
        public static void Remove(Agent a) { if (a != null) bonuses.Remove(a); }
        public static bool TryGet(Agent a, out float pct) => bonuses.TryGetValue(a, out pct);
        public static void Clear() => bonuses.Clear();
    }

    // Real aura damage buff/debuff for ComposedAuraPower, reaching ANY agent (regular troops
    // included, not just other adopted heroes) - unlike ComposedSelfBuffPower, which is limited to
    // the owning hero's own agent because PowerHandler.Handlers.OnDoDamage/OnTakeDamage only fire
    // for that one hero (see PowerHandler.CallHandlersForAgentPair in the fork). Modeled directly on
    // MBGAPrestigeStatPatches.DamagePrefix above - same (Agent attacker, ref Blow b) signature
    // (Harmony binds by parameter name, so these must stay named exactly "attacker"/"b"), same
    // try/catch-and-log shape. No Prepare() gate: unlike Prestige (a global on/off config read at
    // PatchAll time), this feature is assigned per-class and only "active" mid-mission - the
    // ComposedAuraDamageTracker.IsEmpty early-out is the equivalent "costs nothing when unused"
    // guarantee. This patch is discovered by the SAME already-guarded PatchAll() call
    // (MbgaPatchGuard.ShouldApply()) as every other [HarmonyPatch] in this file - no new Harmony
    // instance, no new PatchAll() call, so the multi-module fan-out regression class doesn't apply.
    [HarmonyPatch]
    internal static class ComposedAuraDamagePatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Mission), "RegisterBlow")]
        private static void DamagePrefix(Agent attacker, ref Blow b)
        {
            try
            {
                if (ComposedAuraDamageTracker.IsEmpty) return;
                if (attacker == null || !attacker.IsActive()) return;
                if (!ComposedAuraDamageTracker.TryGet(attacker, out float pct)) return;
                float mult = 1f + pct / 100f;
                if (mult < 0f) mult = 0f;
                b.BaseMagnitude *= mult;
                b.InflictedDamage = (int)(b.InflictedDamage * mult);
            }
            catch (Exception ex) { Log.Exception("ComposedAuraDamagePatch.DamagePrefix failed", ex); }
        }
    }


    // Samodzielna progresja Tier 7/8 - fork blokuje/odblokowuje to przez bramke wewnatrz duzej,
    // internal metody EquipHero.ExecuteInternal (nierozlaczna bez przepisania calosci equipu). Klucz:
    // UpgradeEquipment(targetTier: 6 lub 7) juz DZIALA bezpiecznie samo z siebie - poniewaz natywne
    // itemy istnieja tylko do Tier 6 (indeks 5), logika "najblizszy tier <= target" naturalnie laduje
    // na najwyzszym dostepnym (Tier 6) gdy poprosimy o wiecej - bez zadnych zmian w doborze sprzetu.
    // Wystarczy wiec: (1) wlasna komenda ktora POMIJA bramke forka wolajac UpgradeEquipment bezposrednio
    // przez refleksje (dokladnie ten sam wzorzec co RebornCommand juz uzywa), (2) wlasne
    // GetEquipmentTier/SetEquipmentTier (public, identyczne w forku i orginale) do sledzenia "wirtualnego"
    // tieru 7/8 ponad naturalny limit gry, (3) bonusy staty na tych samych natywnych hookach co Prestige.
    public class MBGATier78Config
    {
        private const string ID = "MBGA - Tier 7-8 Progression";
        internal static void Register() => ActionManager.RegisterGlobalConfigType(ID, typeof(MBGATier78Config));
        internal static MBGATier78Config Get() => ActionManager.GetGlobalConfig<MBGATier78Config>(ID);

        [DisplayName("Enabled"), Description("Standalone re-implementation of the fork's Tier 7 (Elite) / Tier 8 (Legendary) progression for an UNMODIFIED BLTAdoptAHero.dll. Off by default - leave disabled if running the MesmerTurn fork, which already has this."), UsedImplicitly]
        public bool Enabled { get; set; } = false;

        [DisplayName("Cost Tier 7 (Elite)"), Description("Gold cost to advance from Tier 6 (native max) to Tier 7."), UsedImplicitly]
        public int CostTier7 { get; set; } = 600000;

        [DisplayName("Cost Tier 8 (Legendary)"), Description("Gold cost to advance from Tier 7 to Tier 8."), UsedImplicitly]
        public int CostTier8 { get; set; } = 900000;

        [DisplayName("Tier 7 Damage Multiplier"), Description("Outgoing damage multiplier at Tier 7+ (e.g. 1.15 = +15%)."), UsedImplicitly]
        public float Tier7DamageMultiplier { get; set; } = 1.15f;

        [DisplayName("Tier 7 Armor Multiplier"), Description("Armor effectiveness multiplier at Tier 7+."), UsedImplicitly]
        public float Tier7ArmorMultiplier { get; set; } = 1.15f;

        [DisplayName("Tier 8 Health Multiplier"), Description("Max HP multiplier at Tier 8 (e.g. 2.0 = double HP)."), UsedImplicitly]
        public float Tier8HealthMultiplier { get; set; } = 2f;
    }

    [LocDescription("Advances a hero already at max equipment tier to Tier 7 (Elite) or Tier 8 (Legendary) for gold, granting HP/damage/armor multipliers. Standalone MBGA version - disabled by default, see \"MBGA - Tier 7-8 Progression\" in Global Configs.")]
    public class MBGATierUpCommand : ICommandHandler
    {
        public class Settings { }
        public Type HandlerConfigType => typeof(Settings);

        public void Execute(ReplyContext context, object config)
        {
            var cfg = MBGATier78Config.Get();
            if (cfg == null || !cfg.Enabled) { ActionManager.SendReply(context, "MBGA Tier 7/8 progression is disabled."); return; }

            var beh = BLTAdoptAHeroCampaignBehavior.Current;
            var hero = beh?.GetAdoptedHero(context.UserName);
            if (hero == null) { ActionManager.SendReply(context, "You don't have an adopted hero."); return; }
            if (Mission.Current != null) { ActionManager.SendReply(context, "Cannot upgrade equipment while a mission is active!"); return; }

            int currentTier = beh.GetEquipmentTier(hero); // 0-based; 5 = native Tier 6 max
            if (currentTier < 5) { ActionManager.SendReply(context, "Reach Tier 6 first with the normal upgrade command."); return; }
            if (currentTier >= 7) { ActionManager.SendReply(context, "Already at Tier 8 (Legendary), the maximum."); return; }

            int targetTier = currentTier + 1; // 6 = Tier 7, 7 = Tier 8
            int cost = targetTier == 6 ? cfg.CostTier7 : cfg.CostTier8;
            int availableGold = beh.GetHeroGold(hero);
            if (availableGold < cost) { ActionManager.SendReply(context, $"Not enough gold (need {cost}, have {availableGold})."); return; }

            try
            {
                var classDef = beh.GetClass(hero);
                var equipHeroType = typeof(BLTAdoptAHeroCampaignBehavior).Assembly.GetTypes()
                    .FirstOrDefault(t => t.Name == "EquipHero");
                var upgradeMethod = equipHeroType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "UpgradeEquipment");
                if (upgradeMethod != null)
                {
                    var parameters = upgradeMethod.GetParameters();
                    var args = new object[parameters.Length];
                    args[0] = hero;
                    args[1] = targetTier;
                    args[2] = classDef;
                    args[3] = false; // replaceSameTier
                    for (int i = 4; i < parameters.Length; i++) args[i] = Type.Missing;
                    upgradeMethod.Invoke(null, args);
                }

                beh.SetEquipmentTier(hero, targetTier);
                beh.ChangeHeroGold(hero, -cost, isSpending: true);

                string label = targetTier == 6 ? "Tier 7 (Elite)" : "Tier 8 (Legendary)";
                ActionManager.SendReply(context, $"{context.UserName} advanced to {label}!");
            }
            catch (Exception ex)
            {
                Log.Exception("MBGATierUpCommand.Execute failed", ex);
                ActionManager.SendReply(context, "Something went wrong upgrading to the next tier.");
            }
        }
    }

    [HarmonyPatch]
    internal static class MBGATier78StatPatches
    {
        // Patchowanie Agent.BaseHealthLimit/GetBaseArmorEffectivenessForBodyPart (nawet z pustym
        // cialem, gdy Enabled=false) psuje renderowanie podgladu w character creation - patrz
        // komentarz przy MBGAPrestigeStatPatches.Prepare(). Przy domyslnym Enabled=false te
        // gettery w ogole nie sa patchowane.
        private static bool Prepare() => MBGATier78Config.Get()?.Enabled == true;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Agent), "BaseHealthLimit", MethodType.Getter)]
        private static void HealthPostfix(Agent __instance, ref float __result)
        {
            try
            {
                var cfg = MBGATier78Config.Get();
                if (cfg == null || !cfg.Enabled) return;
                if (__instance == null || !__instance.IsActive()) return;
                var hero = (__instance.Character as CharacterObject)?.HeroObject;
                if (hero == null) return;
                if ((BLTAdoptAHeroCampaignBehavior.Current?.GetEquipmentTier(hero) ?? -1) >= 7)
                    __result *= cfg.Tier8HealthMultiplier;
            }
            catch (Exception ex) { Log.Exception("MBGATier78StatPatches.HealthPostfix failed", ex); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Mission), "RegisterBlow")]
        private static void DamagePrefix(Agent attacker, ref Blow b)
        {
            try
            {
                var cfg = MBGATier78Config.Get();
                if (cfg == null || !cfg.Enabled || attacker == null || !attacker.IsActive()) return;
                var hero = attacker.GetAdoptedHero();
                if (hero == null) return;
                if ((BLTAdoptAHeroCampaignBehavior.Current?.GetEquipmentTier(hero) ?? -1) >= 6)
                {
                    float mult = cfg.Tier7DamageMultiplier;
                    b.BaseMagnitude *= mult;
                    b.InflictedDamage = (int)(b.InflictedDamage * mult);
                }
            }
            catch (Exception ex) { Log.Exception("MBGATier78StatPatches.DamagePrefix failed", ex); }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Agent), "GetBaseArmorEffectivenessForBodyPart")]
        private static void ArmorPostfix(Agent __instance, ref float __result)
        {
            try
            {
                var cfg = MBGATier78Config.Get();
                if (cfg == null || !cfg.Enabled) return;
                if (__instance == null || !__instance.IsActive()) return;
                var hero = (__instance.Character as CharacterObject)?.HeroObject;
                if (hero == null) return;
                if ((BLTAdoptAHeroCampaignBehavior.Current?.GetEquipmentTier(hero) ?? -1) >= 6)
                    __result *= cfg.Tier7ArmorMultiplier;
            }
            catch (Exception ex) { Log.Exception("MBGATier78StatPatches.ArmorPostfix failed", ex); }
        }
    }

    // Samodzielny odpowiednik forkowego UseDragon/UseChariot (RoT) - fork dodaje te dwa bool-e
    // BEZPOSREDNIO do HeroClassDef (BLTAdoptAHero), wiec nie istnieja w oryginalnym DLL i nie da
    // sie ich stamtad odczytac na czystej instalacji. HeroClassDef samo w sobie (ID/Name) jest
    // czescia oryginalnego BLT, wiec zamiast rozszerzac ten typ, MBGA trzyma WLASNA, rownolegla
    // liste "ta klasa jezdzi na X" kluczowana po nazwie klasy, i podmienia wierzchowca po kazdej
    // zmianie tieru sprzetu (Postfix na SetEquipmentTier - publiczna, identyczna w forku i
    // oryginale, wolana przez !upgrade, Prestige i TierUp jednakowo). Itemy dragon_black/chariot1
    // itd. to zwykle stringowe ID przedmiotow z modulu RoT - odczytywane przez natywny
    // MBObjectManager, wiec nie ma tu zadnej zaleznosci od BLTAdoptAHero w ogole.
    public class MBGAClassMountDef
    {
        [DisplayName("Class Name"), Description("Exact display name of the hero class this override applies to (as set in the class's own config)."), UsedImplicitly]
        public string ClassName { get; set; } = "";

        [DisplayName("Mount"), Description("Dragon or Chariot - requires the RoT (Rise of Templin) content module to be installed for the item IDs to resolve."), ItemsSource(typeof(MountTypeItemsSource)), UsedImplicitly]
        public string Mount { get; set; } = "Dragon";
    }

    public class MountTypeItemsSource : IItemsSource
    {
        public ItemCollection GetValues()
        {
            var c = new ItemCollection { "Dragon", "Chariot" };
            return c;
        }
    }

    public class MBGADragonChariotConfig
    {
        private const string ID = "MBGA - Dragon-Chariot Mounts (RoT)";
        internal static void Register() => ActionManager.RegisterGlobalConfigType(ID, typeof(MBGADragonChariotConfig));
        internal static MBGADragonChariotConfig Get() => ActionManager.GetGlobalConfig<MBGADragonChariotConfig>(ID);

        [DisplayName("Enabled"), Description("Lets specific hero classes ride a Dragon or Chariot (RoT module content) instead of a normal horse. Standalone MBGA re-implementation of the fork's UseDragon/UseChariot class flags. Off by default - leave disabled if running the MesmerTurn fork, which already has this."), UsedImplicitly]
        public bool Enabled { get; set; } = false;

        [DisplayName("Class Overrides"), Description("Which hero classes (by exact name) ride a Dragon or Chariot."), UsedImplicitly]
        public List<MBGAClassMountDef> Overrides { get; set; } = new List<MBGAClassMountDef>();
    }

    // DISABLED in the 1.3.15 Warsails line: the fork (BLTAdoptAHero 5.4.x) already provides
    // Dragon/Chariot mounts per-class natively (HeroClassDef "Use Dragon/Chariot" checkboxes), so this
    // standalone MBGA override duplicates it. Class-level [HarmonyPatch] removed -> PatchAll skips it;
    // code stays dormant.
    internal static class MBGADragonChariotPatch
    {
        private static readonly string[] DragonGroundIds = { "dragon_black", "dragon_brown", "dragon_gold" };
        private static readonly string[] DragonFlyingIds = { "dragon_black2", "dragon_brown2", "dragon_gold2" };
        private static readonly string[] ChariotBasicIds = { "chariot1", "chariot2", "chariot3" };
        private static readonly string[] ChariotAdvancedIds = { "chariot4", "chariot5", "chariot6" };

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BLTAdoptAHeroCampaignBehavior), "SetEquipmentTier")]
        private static void Postfix(BLTAdoptAHeroCampaignBehavior __instance, Hero hero, int tier)
        {
            try
            {
                var cfg = MBGADragonChariotConfig.Get();
                if (cfg == null || !cfg.Enabled || hero == null || cfg.Overrides.Count == 0) return;

                var classDef = __instance.GetClass(hero);
                string className = classDef?.Name?.ToString();
                if (string.IsNullOrEmpty(className)) return;

                var over = cfg.Overrides.FirstOrDefault(o => string.Equals(o.ClassName, className, StringComparison.OrdinalIgnoreCase));
                if (over == null) return;

                int idx = Math.Max(0, Math.Min(2, tier));
                string itemId = over.Mount == "Chariot"
                    ? (tier < 3 ? ChariotBasicIds[idx] : ChariotAdvancedIds[idx])
                    : (tier < 3 ? DragonGroundIds[idx] : DragonFlyingIds[idx]);

                var item = TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObject<ItemObject>(itemId);
                if (item == null)
                {
                    Log.Info($"[MBGADragonChariot] Item '{itemId}' not found - is the RoT content module installed?");
                    return;
                }

                hero.BattleEquipment[EquipmentIndex.Horse] = new EquipmentElement(item);
                hero.BattleEquipment[EquipmentIndex.HorseHarness] = EquipmentElement.Invalid;
            }
            catch (Exception ex) { Log.Exception("MBGADragonChariotPatch.Postfix failed", ex); }
        }
    }

    // Patch aplikowany RĘCZNIE w BLTAurasModule.OnSubModuleLoad (NIE przez [HarmonyPatch]/PatchAll —
    // 9 modułów MBGA = 9× PatchAll = duplikaty patchy). Mnoży obrażenia szarży konnej gdy adrenalina aktywna.
    internal static class AdrenalineChargePatch
    {
        // Mirror Mission.RegisterBlow(Agent attacker, Agent victim, WeakGameEntity realHitEntity,
        //   ref Blow b, ref AttackCollisionData collisionData, in MissionWeapon attackerWeapon)
        public static void Prefix(Agent attacker, Agent victim, WeakGameEntity realHitEntity, ref Blow b,
            ref AttackCollisionData collisionData, in MissionWeapon attackerWeapon)
        {
            try
            {
                var cfg = AdrenalineGlobalConfig.Get();
                if (cfg == null || !cfg.Enabled) return;
                if (cfg.MountedChargeDamageMultiplier <= 1f) return;
                if (attacker == null || !attacker.IsActive()) return;
                if (attacker.GetAdoptedHero() == null) return;
                if (!attacker.HasMount) return;                 // mounted only
                if (!AdrenalineMissionBehavior.IsAdrenalineActive(attacker)) return;
                if (!IsChargeBlow(b)) return;

                float mult = cfg.MountedChargeDamageMultiplier;
                b.InflictedDamage = Math.Max(0, (int)Math.Round(b.InflictedDamage * mult));
                b.BaseMagnitude *= mult;
                collisionData.InflictedDamage = Math.Max(0, (int)Math.Round(collisionData.InflictedDamage * mult));
                collisionData.BaseMagnitude *= mult;
            }
            catch (Exception ex)
            {
                Log.Exception("Adrenaline charge damage patch failed", ex);
            }
        }

        // Charge / mount-collision hit: AttackType == Collision (mount running into a target).
        private static bool IsChargeBlow(Blow b)
        {
            return b.AttackType == AgentAttackType.Collision;
        }
    }

    // ======================================================================
    // BLTUpgrade
    // ======================================================================

public class BLTUpgradeModule : MBSubModuleBase
    {
        private static Harmony harmony;

        public BLTUpgradeModule()
        {
            ActionManager.RegisterAll(typeof(BLTUpgradeModule).Assembly);
        }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            if (harmony != null) return;

            try
            {
                harmony = new Harmony("mod.bannerlord.bltupgrade");
                if (MbgaPatchGuard.ShouldApply()) harmony.PatchAll();
                Log.Info("[BLTUpgrade] Loaded. Commands: !autoupgrade_clan / !autoupgrade_fief / !autoupgrade_kingdom");
            }
            catch (Exception ex)
            {
                Log.Exception("[BLTUpgrade] Load failed", ex);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // CLAN UPGRADE
    // ════════════════════════════════════════════════════════════════════════════

    [DisplayName("UpgradeClanCommand")]
    [LocDescription("Automatically buys every affordable clan-level upgrade your hero's clan is missing.")]
    public class UpgradeClanCommand : ICommandHandler
    {
        public Type HandlerConfigType => typeof(UpgradeSettings);

        public void Execute(ReplyContext context, object config)
        {
            var settings = config as UpgradeSettings ?? new UpgradeSettings();
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);

            if (hero == null) { ActionManager.SendReply(context, "You do not have an adopted hero."); return; }
            if (hero.Clan == null) { ActionManager.SendReply(context, "Your hero has no clan."); return; }

            var (count, spent) = UpgradeAllHandler.ExecuteClan(hero, out var debugMsg, settings.DryRun);
            if (count > 0)
                ActionManager.SendReply(context, $"✓ {count} clan upgrade(s) applied, spent {spent} gold.");
            else
                ActionManager.SendReply(context, $"No upgrades: {debugMsg}");
        }

        public class UpgradeSettings
        {
            [DisplayName("Dry Run (test mode)")]
            public bool DryRun { get; set; } = false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // FIEF UPGRADE
    // ════════════════════════════════════════════════════════════════════════════

    [DisplayName("UpgradeFiefCommand")]
    [LocDescription("Automatically buys every affordable fief/settlement-level upgrade available to your hero's clan.")]
    public class UpgradeFiefCommand : ICommandHandler
    {
        public Type HandlerConfigType => typeof(UpgradeSettings);

        public void Execute(ReplyContext context, object config)
        {
            var settings = config as UpgradeSettings ?? new UpgradeSettings();
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);

            if (hero == null) { ActionManager.SendReply(context, "You do not have an adopted hero."); return; }
            if (hero.Clan == null) { ActionManager.SendReply(context, "Your hero has no clan."); return; }

            var (count, spent) = UpgradeAllHandler.ExecuteFief(hero, settings.DryRun);
            if (count > 0)
                ActionManager.SendReply(context, $"✓ {count} fief upgrade(s) applied, spent {spent} gold.");
            else
                ActionManager.SendReply(context, "No fief upgrades available or not enough gold.");
        }

        public class UpgradeSettings
        {
            [DisplayName("Dry Run (test mode)")]
            public bool DryRun { get; set; } = false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // KINGDOM UPGRADE
    // ════════════════════════════════════════════════════════════════════════════

    [DisplayName("UpgradeKingdomCommand")]
    [LocDescription("Automatically buys every affordable kingdom-level upgrade available to your hero's clan (requires the clan to be in a kingdom).")]
    public class UpgradeKingdomCommand : ICommandHandler
    {
        public Type HandlerConfigType => typeof(UpgradeSettings);

        public void Execute(ReplyContext context, object config)
        {
            var settings = config as UpgradeSettings ?? new UpgradeSettings();
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);

            if (hero == null) { ActionManager.SendReply(context, "You do not have an adopted hero."); return; }
            if (hero.Clan?.Kingdom == null) { ActionManager.SendReply(context, "Your hero's clan is not in a kingdom."); return; }

            var (count, spent) = UpgradeAllHandler.ExecuteKingdom(hero, settings.DryRun);
            if (count > 0)
                ActionManager.SendReply(context, $"✓ {count} kingdom upgrade(s) applied, spent {spent} gold.");
            else
                ActionManager.SendReply(context, "No kingdom upgrades available or not enough gold.");
        }

        public class UpgradeSettings
        {
            [DisplayName("Dry Run (test mode)")]
            public bool DryRun { get; set; } = false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // CORE UPGRADE LOGIC
    // ════════════════════════════════════════════════════════════════════════════

    public static class UpgradeAllHandler
    {
        // Zwraca (liczba ulepszeń, wydane złoto BLT)
        public static (int count, int spent) ExecuteClan(Hero hero, out string debugInfo, bool dryRun = false)
        {
            debugInfo = "";
            try
            {
                if (hero?.Clan == null) { debugInfo = "hero/clan null"; return (0, 0); }

                var config = GetGlobalConfig();
                var upgradeBehavior = UpgradeBehavior.Current;
                if (config == null) { debugInfo = "config null"; Log.Error("[BLTUpgrade] Missing config"); return (0, 0); }
                if (upgradeBehavior == null) { debugInfo = "behavior null"; Log.Error("[BLTUpgrade] Missing behavior"); return (0, 0); }

                var clanUpgrades = GetPropertyValue<IEnumerable>(config, "ClanUpgrades");
                if (clanUpgrades == null) { debugInfo = "ClanUpgrades null"; return (0, 0); }

                var allUpgrades = clanUpgrades.Cast<object>().ToList();
                int totalGold = BLTAdoptAHeroCampaignBehavior.Current?.GetHeroGold(hero) ?? 0;
                var owned = new HashSet<string>(upgradeBehavior.GetClanUpgrades(hero.Clan));

                int upgraded = 0;
                int spent = 0;
                int skippedOwned = 0;
                int skippedGold = 0;
                int skippedReqs = 0;

                var available = allUpgrades
                    .Where(u => {
                        var id = GetPropertyValue<string>(u, "ID");
                        if (upgradeBehavior.HasClanUpgrade(hero.Clan, id)) { skippedOwned++; return false; }
                        return true;
                    })
                    .OrderBy(u => GetPropertyValue<int>(u, "GoldCost"))
                    .ToList();

                foreach (var upgrade in available)
                {
                    var id = GetPropertyValue<string>(upgrade, "ID");
                    var cost = GetPropertyValue<int>(upgrade, "GoldCost");

                    if (totalGold < cost) { skippedGold++; continue; }

                    var required = GetPropertyValue<List<string>>(upgrade, "RequiredUpgradeIDs");
                    if (required?.Count > 0 && !required.All(r => owned.Contains(r)))
                    { skippedReqs++; continue; }

                    if (!dryRun)
                    {
                        if (upgradeBehavior.AddClanUpgrade(hero.Clan, id))
                        {
                            BLTAdoptAHeroCampaignBehavior.Current?.ChangeHeroGold(hero, -cost, true);
                            owned.Add(id);
                            totalGold -= cost;
                            spent += cost;
                            upgraded++;
                        }
                    }
                    else
                    {
                        totalGold -= cost;
                        spent += cost;
                        upgraded++;
                    }
                }

                debugInfo = $"total={allUpgrades.Count} owned={skippedOwned} avail={available.Count} gold={totalGold} skippedGold={skippedGold} skippedReqs={skippedReqs}";
                Log.Info($"[BLTUpgrade] CLAN: {upgraded} added, {spent}g spent | {debugInfo}");
                return (upgraded, spent);
            }
            catch (Exception ex)
            {
                debugInfo = $"exception: {ex.Message}";
                Log.Exception("[BLTUpgrade] clan error", ex);
                return (0, 0);
            }
        }

        public static (int count, int spent) ExecuteFief(Hero hero, bool dryRun = false)
        {
            try
            {
                if (hero?.Clan == null) return (0, 0);

                var config = GetGlobalConfig();
                var upgradeBehavior = UpgradeBehavior.Current;
                if (config == null || upgradeBehavior == null) return (0, 0);

                var fiefUpgrades = GetPropertyValue<IEnumerable>(config, "FiefUpgrades");
                if (fiefUpgrades == null) return (0, 0);

                var fiefs = hero.Clan.Settlements.Where(s => s.IsTown || s.IsCastle).ToList();
                if (fiefs.Count == 0) return (0, 0);

                int totalGold = BLTAdoptAHeroCampaignBehavior.Current?.GetHeroGold(hero) ?? 0;
                int upgraded = 0;
                int spent = 0;

                var upgrades = fiefUpgrades.Cast<object>()
                    .OrderBy(u => GetPropertyValue<int>(u, "GoldCost"))
                    .ToList();

                foreach (var fief in fiefs)
                {
                    if (totalGold <= 0) break;

                    var owned = new HashSet<string>(upgradeBehavior.GetFiefUpgrades(fief));

                    foreach (var upgrade in upgrades)
                    {
                        var id = GetPropertyValue<string>(upgrade, "ID");
                        var cost = GetPropertyValue<int>(upgrade, "GoldCost");

                        if (owned.Contains(id)) continue;
                        if (totalGold < cost) continue;

                        if (GetPropertyValue<bool>(upgrade, "CoastalOnly") && !GetPropertyValue<bool>(fief, "IsCoastal"))
                            continue;

                        var required = GetPropertyValue<List<string>>(upgrade, "RequiredUpgradeIDs");
                        if (required?.Count > 0 && !required.All(r => owned.Contains(r)))
                            continue;

                        if (!dryRun)
                        {
                            if (upgradeBehavior.AddFiefUpgrade(fief, id))
                            {
                                BLTAdoptAHeroCampaignBehavior.Current?.ChangeHeroGold(hero, -cost, true);
                                owned.Add(id);
                                totalGold -= cost;
                                spent += cost;
                                upgraded++;
                            }
                        }
                        else
                        {
                            totalGold -= cost;
                            spent += cost;
                            upgraded++;
                        }
                    }
                }

                Log.Info($"[BLTUpgrade] FIEF: {upgraded} added, {spent}g spent");
                return (upgraded, spent);
            }
            catch (Exception ex)
            {
                Log.Exception("[BLTUpgrade] fief error", ex);
                return (0, 0);
            }
        }

        public static (int count, int spent) ExecuteKingdom(Hero hero, bool dryRun = false)
        {
            try
            {
                var kingdom = hero?.Clan?.Kingdom;
                if (kingdom == null) return (0, 0);

                var config = GetGlobalConfig();
                var upgradeBehavior = UpgradeBehavior.Current;
                if (config == null || upgradeBehavior == null) return (0, 0);

                var kingdomUpgrades = GetPropertyValue<IEnumerable>(config, "KingdomUpgrades");
                if (kingdomUpgrades == null) return (0, 0);

                int totalGold = BLTAdoptAHeroCampaignBehavior.Current?.GetHeroGold(hero) ?? 0;
                int upgraded = 0;
                int spent = 0;

                var owned = new HashSet<string>(upgradeBehavior.GetKingdomUpgrades(kingdom));

                var available = kingdomUpgrades.Cast<object>()
                    .Where(u => !owned.Contains(GetPropertyValue<string>(u, "ID")))
                    .OrderBy(u => GetPropertyValue<int>(u, "GoldCost"))
                    .ToList();

                foreach (var upgrade in available)
                {
                    var id = GetPropertyValue<string>(upgrade, "ID");
                    var cost = GetPropertyValue<int>(upgrade, "GoldCost");

                    if (totalGold < cost) break;

                    var required = GetPropertyValue<List<string>>(upgrade, "RequiredUpgradeIDs");
                    if (required?.Count > 0 && !required.All(r => owned.Contains(r)))
                        continue;

                    if (!dryRun)
                    {
                        if (upgradeBehavior.AddKingdomUpgrade(kingdom, id))
                        {
                            BLTAdoptAHeroCampaignBehavior.Current?.ChangeHeroGold(hero, -cost, true);
                            owned.Add(id);
                            totalGold -= cost;
                            spent += cost;
                            upgraded++;
                        }
                    }
                    else
                    {
                        totalGold -= cost;
                        spent += cost;
                        upgraded++;
                    }
                }

                Log.Info($"[BLTUpgrade] KINGDOM: {upgraded} added, {spent}g spent");
                return (upgraded, spent);
            }
            catch (Exception ex)
            {
                Log.Exception("[BLTUpgrade] kingdom error", ex);
                return (0, 0);
            }
        }

        private static object GetGlobalConfig()
        {
            try
            {
                // GlobalCommonConfig is internal — Type.GetType fails for internal types from external assemblies.
                // Use assembly scanning instead.
                var type = typeof(UpgradeBehavior).Assembly.GetTypes()
                    .FirstOrDefault(t => t.Name == "GlobalCommonConfig");
                if (type == null) return null;
                var method = type.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                return method?.Invoke(null, null);
            }
            catch { return null; }
        }

        private static T GetPropertyValue<T>(object obj, string propertyName)
        {
            try
            {
                if (obj == null) return default;
                var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop == null) return default;
                var val = prop.GetValue(obj);
                return val is T t ? t : default;
            }
            catch { return default; }
        }
    }


    // ======================================================================
    // BLTDuel
    // ======================================================================

public class BLTDuelModule : MBSubModuleBase
    {
        public BLTDuelModule()
        {
            ActionManager.RegisterAll(typeof(BLTDuelModule).Assembly);
        }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            Log.Info("[BLTDuel] Loaded.");
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            if (mission.CombatType != Mission.MissionCombatType.Combat) return;
            mission.AddMissionBehavior(new DuelMissionBehavior());
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // COMMAND HANDLER — widz wpisuje !duel @nazwawidza
    // ════════════════════════════════════════════════════════════════════════════

    [DisplayName("Duel")]
    [LocDescription("Challenges another viewer's summoned BLT hero to a 1v1 duel during battle. Usage: !duel @username")]
    public class DuelCommand : ICommandHandler
    {
        public class DuelSettings { }
        public Type HandlerConfigType => typeof(DuelSettings);

        public void Execute(ReplyContext context, object config)
        {
            // Check that we're in a battle
            if (Mission.Current == null || Mission.Current.CurrentState != Mission.State.Continuing)
            {
                ActionManager.SendReply(context, "Duel can only be challenged during an active battle.");
                return;
            }

            // Block during the pre-battle deployment phase: agents are still lined up in
            // formation on their own side of the field, so ordering one to run to an enemy
            // target means crossing the whole no-man's-land alone into a full enemy formation
            // that hasn't engaged yet - it gets swarmed and killed instantly on arrival.
            if (!Mission.Current.IsDeploymentFinished)
            {
                ActionManager.SendReply(context, "Duel can't be started during deployment - wait for the battle to actually begin.");
                return;
            }

            var behavior = Mission.Current.GetMissionBehavior<DuelMissionBehavior>();
            if (behavior == null)
            {
                ActionManager.SendReply(context, "Duel system is not active.");
                return;
            }

            // Get the challenger's hero
            var challengerHero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (challengerHero == null)
            {
                ActionManager.SendReply(context, "You don't have an adopted hero.");
                return;
            }

            // Read the target from the command argument (e.g. "!duel Mark" or "!duel @Mark")
            string targetName = context.Args?.Trim().TrimStart('@');
            if (string.IsNullOrEmpty(targetName))
            {
                ActionManager.SendReply(context, "Usage: !duel @viewername");
                return;
            }

            var targetHero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(targetName);
            if (targetHero == null)
            {
                ActionManager.SendReply(context, $"BLT hero '{targetName}' not found.");
                return;
            }

            if (targetHero == challengerHero)
            {
                ActionManager.SendReply(context, "You can't challenge yourself.");
                return;
            }

            // Check that both are in this battle
            var challengerAgent = GetActiveAgent(challengerHero);
            var targetAgent     = GetActiveAgent(targetHero);

            if (challengerAgent == null)
            {
                ActionManager.SendReply(context, "Your hero is not summoned in this battle.");
                return;
            }
            if (targetAgent == null)
            {
                ActionManager.SendReply(context, $"{targetHero.FirstName} is not present in this battle.");
                return;
            }

            // Check that they're on opposite sides
            if (challengerAgent.Team == targetAgent.Team)
            {
                ActionManager.SendReply(context,
                    $"{challengerHero.FirstName} and {targetHero.FirstName} are on the same side — duel impossible!");
                return;
            }

            // The challenger can't attack two targets at once. The target CAN be attacked
            // by multiple bltheroes at the same time (gang up on one enemy).
            if (behavior.HasActiveDuel(challengerHero))
            {
                ActionManager.SendReply(context, "You're already in a duel — finish it first.");
                return;
            }

            // Register the duel and issue orders
            behavior.StartDuel(challengerHero, targetHero, challengerAgent, targetAgent);

            var msg = $"⚔ DUEL! {challengerHero.FirstName} challenged {targetHero.FirstName}! Let the fight begin!";
            ActionManager.SendReply(context, msg);
            Log.ShowInformation(msg, challengerHero.CharacterObject);
            BroadcastToChat(msg);
        }

        private static Agent GetActiveAgent(Hero hero)
            => Mission.Current?.Agents?.FirstOrDefault(a => BLTAdoptAHero.AgentExtensions.GetHero(a) == hero && a.IsActive());

        internal static void BroadcastToChat(string message)
        {
            try
            {
                var serviceType = Type.GetType("BannerlordTwitch.Twitch.TwitchService, BannerlordTwitch");
                if (serviceType == null) return;
                var currentProp = serviceType.GetProperty("Current",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var service = currentProp?.GetValue(null);
                if (service == null) return;
                var botField = serviceType.GetField("bot", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? serviceType.GetField("Bot", BindingFlags.Instance | BindingFlags.NonPublic);
                var bot = botField?.GetValue(service);
                if (bot == null) return;
                var sendChat = bot.GetType().GetMethod("SendChat",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                sendChat?.Invoke(bot, new object[] { new[] { message } });
            }
            catch { }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // MISSION BEHAVIOR — zarządza aktywnymi duelami w trakcie bitwy
    // ════════════════════════════════════════════════════════════════════════════

    public class DuelMissionBehavior : MissionBehavior
    {
        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        // Mapa: atakujący blthero → jego cel (wróg). Wielu atakujących MOŻE mieć ten sam cel
        // (kilku blthero rzuca się na jednego wroga z przeciwnej strony).
        private readonly Dictionary<Hero, Hero> activeDuels = new Dictionary<Hero, Hero>();
        private float nextRetargetTime = 0f;
        private const float RetargetInterval = 1.5f;

        // hero jest "zajęty" duelem tylko jako ATAKUJĄCY (nie może atakować dwóch naraz);
        // bycie celem nie blokuje — można być atakowanym przez wielu.
        public bool HasActiveDuel(Hero hero) => activeDuels.ContainsKey(hero);

        public void StartDuel(Hero attacker, Hero target, Agent attackerAgent, Agent targetAgent)
        {
            activeDuels[attacker] = target;
            DuelMoveAndFight(attackerAgent, targetAgent);
        }

        public override void OnMissionTick(float dt)
        {
            if (activeDuels.Count == 0) return;
            float now = Mission.Current?.CurrentTime ?? 0f;
            if (now < nextRetargetTime) return;
            nextRetargetTime = now + RetargetInterval;
            RefreshDuels();
        }

        private void RefreshDuels()
        {
            var toRemove = new List<Hero>();
            var announcedDeadTargets = new HashSet<Hero>();

            foreach (var (attacker, target) in activeDuels)
            {
                var attackerAgent = GetActiveAgent(attacker);
                var targetAgent   = GetActiveAgent(target);

                bool attackerDead = attackerAgent == null || !attackerAgent.IsActive();
                bool targetDead   = targetAgent   == null || !targetAgent.IsActive();

                if (attackerDead)
                {
                    toRemove.Add(attacker);
                    string msg = $"⚔ {attacker.FirstName} fell in a duel with {target.FirstName}!";
                    Log.ShowInformation(msg, target.CharacterObject);
                    DuelCommand.BroadcastToChat(msg);
                    continue;
                }

                if (targetDead)
                {
                    // The attacker survived — free, returns to normal AI
                    ClearDuelAI(attackerAgent);
                    toRemove.Add(attacker);
                    if (announcedDeadTargets.Add(target))
                    {
                        string msg = $"🏆 {target.FirstName} was defeated in a duel!";
                        Log.ShowInformation(msg, attacker.CharacterObject);
                        DuelCommand.BroadcastToChat(msg);
                    }
                    continue;
                }

                // Oboje żyją — kontynuuj pościg/walkę
                DuelMoveAndFight(attackerAgent, targetAgent);
            }

            foreach (var h in toRemove)
                activeDuels.Remove(h);
        }

        // Atakujący biegnie do celu duelu, ale bije wrogów którzy go zaczepią po drodze.
        // Gdy cel jest w zasięgu — priorytetowo go atakuje.
        private static void DuelMoveAndFight(Agent attacker, Agent target)
        {
            if (attacker == null || !attacker.IsActive() || target == null || !target.IsActive()) return;
            try
            {
                float dist = (attacker.Position - target.Position).Length;
                if (dist <= FollowCombat.EngageRange || FollowCombat.HasEnemyNear(attacker, FollowCombat.EngageRange))
                {
                    // Walka: normalne AI wybiera cele, ale gdy cel duelu blisko — wymuś go
                    attacker.SetAutomaticTargetSelection(true);
                    attacker.DisableScriptedMovement();
                    if (dist <= FollowCombat.EngageRange)
                        attacker.SetTargetAgent(target);
                    return;
                }
                // Daleko i czysto — biegnij do celu duelu
                var pos = target.GetWorldPosition();
                attacker.SetScriptedPosition(ref pos, false, Agent.AIScriptedFrameFlags.None);
                attacker.SetTargetAgent(target);
            }
            catch { }
        }

        private static void ClearDuelAI(Agent agent)
        {
            if (agent == null || !agent.IsActive()) return;
            try
            {
                // Wróć do normalnego AI — formation lub bieżąca pozycja
                if (agent.Formation != null)
                    agent.Formation.SetMovementOrder(MovementOrder.MovementOrderCharge);
                else
                {
                    var cur = agent.GetWorldPosition();
                    agent.SetScriptedPosition(ref cur, false, Agent.AIScriptedFrameFlags.None);
                }
            }
            catch { }
        }

        private static Agent GetActiveAgent(Hero hero)
            => Mission.Current?.Agents?.FirstOrDefault(a => BLTAdoptAHero.AgentExtensions.GetHero(a) == hero && a.IsActive());
    }


    // ======================================================================
    // BLTClanGold
    // ======================================================================

public class BLTClanGoldModule : MBSubModuleBase
    {
        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            Log.Info("[BLTClanGold] Loaded.");
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // CAMPAIGN BEHAVIOR — dzieli dochód klanu między BLT członków
    // ════════════════════════════════════════════════════════════════════════════

    public class ClanGoldBehavior : CampaignBehaviorBase
    {
        public static ClanGoldBehavior Current { get; private set; }

        // Zapamiętujemy złoto kampanijne klanu z poprzedniego dnia
        private readonly Dictionary<string, int> lastKnownClanGold = new Dictionary<string, int>();

        public override void RegisterEvents()
        {
            Current = this;
            CampaignEvents.DailyTickClanEvent.AddNonSerializedListener(this, OnDailyTickClan);
        }

        public override void SyncData(IDataStore dataStore)
        {
            // Gold delta is recalculated daily — no persistence needed
        }

        private void OnDailyTickClan(Clan clan)
        {
            if (clan == null) return;

            // Zbierz wszystkich BLT bohaterów w klanie (żywych)
            var bltMembers = clan.Heroes
                .Where(h => h != null && h.IsAlive && h.IsAdopted())
                .ToList();

            // Potrzeba co najmniej 2 BLT bohaterów — inaczej nie ma kogo dzielić
            if (bltMembers.Count < 2) return;

            // Lider klanu musi być BLT bohaterem
            if (clan.Leader == null || !clan.Leader.IsAdopted()) return;

            string clanId = clan.StringId;
            int currentGold = clan.Gold;

            if (!lastKnownClanGold.TryGetValue(clanId, out int lastGold))
            {
                lastKnownClanGold[clanId] = currentGold;
                return;
            }

            int delta = currentGold - lastGold;
            lastKnownClanGold[clanId] = currentGold;

            // Interesuje nas tylko dodatni dochód
            if (delta <= 0) return;

            // Podziel równo między wszystkich BLT członków
            int sharePerMember = delta / bltMembers.Count;
            if (sharePerMember <= 0) return;

            var behavior = BLTAdoptAHeroCampaignBehavior.Current;
            if (behavior == null) return;

            foreach (var member in bltMembers)
            {
                behavior.ChangeHeroGold(member, sharePerMember);
            }

            Log.Info($"[BLTClanGold] Clan {clan.Name}: +{delta}g income split {bltMembers.Count} ways " +
                     $"({sharePerMember}g each) → {string.Join(", ", bltMembers.Select(h => h.FirstName?.Raw()))}");
        }
    }


    // ======================================================================
    // BLTGrail
    // ======================================================================

// ════════════════════════════════════════════════════════════════════════════
    // USTAWIENIA JEDNEGO QUESTA (Weapon lub Armor)
    // ════════════════════════════════════════════════════════════════════════════

    [DisplayName("Grail Quest Settings")]
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public class GrailQuestSettings
    {
        [DisplayName("Enabled"),
         Description("Czy ten quest jest aktywny?"),
         PropertyOrder(1)]
        public bool Enabled { get; set; } = true;

        [DisplayName("Quest Name"),
         Description("Quest name shown in chat messages."),
         PropertyOrder(2)]
        public string QuestName { get; set; } = "Grail Quest";

        [DisplayName("Daily Trigger Chance (0-1)"),
         Description("Daily chance for the quest to appear. 0.02 = 2%."),
         Range(0.0, 1.0), Editor(typeof(SliderFloatEditor), typeof(SliderFloatEditor)),
         PropertyOrder(3)]
        public float DailyChance { get; set; } = 0.02f;

        [DisplayName("Battles Required"),
         Description("Number of battles the hero must survive to complete the quest."),
         PropertyOrder(4)]
        public int BattlesRequired { get; set; } = 5;

        [DisplayName("Count Player Battles"),
         Description("When true — quest only counts battles where the streamer (MainHero) participates."),
         PropertyOrder(5)]
        public bool CountPlayerBattles { get; set; } = true;

        [DisplayName("Use Hero Class Item"),
         Description("When true — reward is matched to the hero's class (archer gets bow, warrior gets sword, etc.). Ignores Item Type field."),
         PropertyOrder(6)]
        public bool UseHeroClassItem { get; set; } = true;

        [DisplayName("Item Type"),
         Description("Reward type when UseHeroClassItem = false."),
         PropertyOrder(7)]
        public RewardHelpers.RewardType ItemType { get; set; } = RewardHelpers.RewardType.Weapon;

        [DisplayName("Item Tier (1-6)"),
         Description("Reward item tier. 6 = best available with modifier (like !smithweapon)."),
         Range(1, 6),
         PropertyOrder(8)]
        public int ItemTier { get; set; } = 6;

        [DisplayName("Item Power"),
         Description("Reward item power multiplier — same as the slider in smithweapon."),
         Range(0.1, 5.0), Editor(typeof(SliderFloatEditor), typeof(SliderFloatEditor)),
         PropertyOrder(9)]
        public float ItemPower { get; set; } = 1.6f;

        [DisplayName("Item Name"),
         Description("Name of the reward item. {ITEMNAME} = base item name."),
         PropertyOrder(10)]
        public string ItemName { get; set; } = "Holy Grail {ITEMNAME}";

        [DisplayName("Quest Start Message"),
         Description("Message shown when the quest starts. {hero} = hero name, {battles} = battles required, {quest} = quest name."),
         PropertyOrder(10)]
        public string QuestStartMessage { get; set; } =
            "⚔ {quest}! {hero} must survive {battles} battles to claim a legendary item!";

        [DisplayName("Quest Progress Message"),
         Description("Message shown after each battle. {hero}, {current}, {required}, {quest}."),
         PropertyOrder(11)]
        public string QuestProgressMessage { get; set; } =
            "🛡 [{quest}] {hero} survived a battle! Progress: {current}/{required}";

        [DisplayName("Quest Complete Message"),
         Description("Message shown on quest completion. {hero}, {quest}."),
         PropertyOrder(12)]
        public string QuestCompleteMessage { get; set; } =
            "🏆 {hero} completed {quest} and claimed a legendary item!";

        [DisplayName("Quest Failed Message"),
         Description("Message shown when the hero dies. {hero}, {quest}."),
         PropertyOrder(13)]
        public string QuestFailedMessage { get; set; } =
            "💀 {hero} has fallen! {quest} is lost...";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // GLOBALNY CONFIG — 2 oddzielne questy w BLTConfigure
    // ════════════════════════════════════════════════════════════════════════════

    [DisplayName("MBGA - Grail")]
    public class GrailConfig
    {
        private const string ID = "MBGA - Grail";
        internal static void Register() => ActionManager.RegisterGlobalConfigType(ID, typeof(GrailConfig));
        internal static GrailConfig Get() => ActionManager.GetGlobalConfig<GrailConfig>(ID);

        [DisplayName("Weapon Quest"),
         Description("Quest that rewards a legendary weapon."),
         PropertyOrder(1)]
        public GrailQuestSettings WeaponQuest { get; set; } = new GrailQuestSettings
        {
            QuestName    = "Holy Blade Quest",
            ItemType     = RewardHelpers.RewardType.Weapon,
            ItemName     = "Holy Grail Blade",
            QuestCompleteMessage = "🏆 {hero} has claimed the Holy Grail Blade!",
            QuestFailedMessage   = "💀 {hero} has fallen! The Holy Blade Quest is lost...",
        };

        [DisplayName("Armor Quest"),
         Description("Quest that rewards legendary armor."),
         PropertyOrder(2)]
        public GrailQuestSettings ArmorQuest { get; set; } = new GrailQuestSettings
        {
            QuestName    = "Holy Armor Quest",
            ItemType     = RewardHelpers.RewardType.Armor,
            ItemName     = "Holy Grail Armor",
            QuestCompleteMessage = "🏆 {hero} has claimed the Holy Grail Armor!",
            QuestFailedMessage   = "💀 {hero} has fallen! The Holy Armor Quest is lost...",
        };
    }

    // ════════════════════════════════════════════════════════════════════════════
    // STAN AKTYWNEGO QUESTA
    // ════════════════════════════════════════════════════════════════════════════

    public class GrailActiveQuest
    {
        public Hero             Hero            { get; set; }
        public GrailQuestSettings Settings      { get; set; }
        public int              BattlesSurvived { get; set; }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // MODULE
    // ════════════════════════════════════════════════════════════════════════════

    public class BLTGrailModule : MBSubModuleBase
    {
        public BLTGrailModule() { try { GrailConfig.Register(); } catch (Exception ex) { Log.Exception("[Grail] Register failed", ex); } }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            Log.Info("[BLTGrail] Loaded.");
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarter)
        {
            base.OnGameStart(game, gameStarter);
            if (gameStarter is CampaignGameStarter cs)
                cs.AddBehavior(new GrailCampaignBehavior());
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // CAMPAIGN BEHAVIOR
    // ════════════════════════════════════════════════════════════════════════════

    public class GrailCampaignBehavior : CampaignBehaviorBase
    {
        public static GrailCampaignBehavior Current { get; private set; }

        // Two independent slots — weapon and armor quests can run simultaneously
        private GrailActiveQuest _weaponQuest;
        private GrailActiveQuest _armorQuest;

        public GrailActiveQuest WeaponQuest => _weaponQuest;
        public GrailActiveQuest ArmorQuest  => _armorQuest;

        public override void RegisterEvents()
        {
            Current = this;
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick);
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
            CampaignEvents.HeroKilledEvent.AddNonSerializedListener(this, OnHeroKilled);
        }

        public override void SyncData(IDataStore dataStore) { }

        // ── Daily tick ────────────────────────────────────────────────────────

        private void OnDailyTick()
        {
            var cfg = GrailConfig.Get();
            if (cfg == null) return;

            // Tylko jeden quest na raz — jeśli cokolwiek aktywne, nie startuj kolejnego
            if (_weaponQuest == null && _armorQuest == null)
            {
                // Losuj który typ questa odpali (żeby nie faworyzować broni)
                if (MBRandom.RandomInt(2) == 0)
                {
                    TryTriggerQuest(cfg.WeaponQuest, ref _weaponQuest);
                    if (_weaponQuest == null)
                        TryTriggerQuest(cfg.ArmorQuest, ref _armorQuest);
                }
                else
                {
                    TryTriggerQuest(cfg.ArmorQuest, ref _armorQuest);
                    if (_armorQuest == null)
                        TryTriggerQuest(cfg.WeaponQuest, ref _weaponQuest);
                }
            }

            // Pokaż postęp aktywnego questa w overlaycie
            ShowProgress(_weaponQuest);
            ShowProgress(_armorQuest);
        }

        private static void TryTriggerQuest(GrailQuestSettings settings, ref GrailActiveQuest slot)
        {
            if (!settings.Enabled) return;
            if (slot != null) return;  // już aktywny

            if (MBRandom.RandomFloat > settings.DailyChance) return;

            var candidates = Hero.AllAliveHeroes
                .Where(h => h != null && h.IsAdopted())
                .ToList();
            if (candidates.Count == 0) return;

            var chosen = candidates[MBRandom.RandomInt(candidates.Count)];
            slot = new GrailActiveQuest
            {
                Hero            = chosen,
                Settings        = settings,
                BattlesSurvived = 0,
            };

            var msg = settings.QuestStartMessage
                .Replace("{quest}",   settings.QuestName)
                .Replace("{hero}",    chosen.FirstName?.Raw() ?? chosen.Name?.Raw())
                .Replace("{battles}", settings.BattlesRequired.ToString());

            Notify(msg, chosen);
            Log.Info($"[BLTGrail] Quest '{settings.QuestName}' started for {chosen.Name}");
        }

        private static void ShowProgress(GrailActiveQuest quest)
        {
            if (quest == null) return;
            var msg = $"[{quest.Settings.QuestName}] {quest.Hero?.FirstName?.Raw()}: " +
                      $"{quest.BattlesSurvived}/{quest.Settings.BattlesRequired} battles";
            Log.ShowInformation(msg, quest.Hero?.CharacterObject);
        }

        // ── MapEvent ended ────────────────────────────────────────────────────

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            ProcessQuestBattle(mapEvent, ref _weaponQuest);
            ProcessQuestBattle(mapEvent, ref _armorQuest);
        }

        private void ProcessQuestBattle(MapEvent mapEvent, ref GrailActiveQuest slot)
        {
            if (slot == null) return;
            var hero = slot.Hero;
            if (hero == null || !hero.IsAlive) return;

            // Liczymy TYLKO bitwy w których bierze udział MainHero (streamer)
            if (!mapEvent.IsPlayerMapEvent) return;

            bool participated = mapEvent.InvolvedParties
                .Any(p => p?.LeaderHero == hero
                       || p?.MemberRoster?.Contains(hero.CharacterObject) == true);
            if (!participated) return;

            slot.BattlesSurvived++;
            Log.Info($"[BLTGrail] '{slot.Settings.QuestName}' {hero.Name}: " +
                     $"{slot.BattlesSurvived}/{slot.Settings.BattlesRequired}");

            if (slot.BattlesSurvived >= slot.Settings.BattlesRequired)
            {
                CompleteQuest(ref slot);
            }
            else
            {
                var msg = slot.Settings.QuestProgressMessage
                    .Replace("{quest}",    slot.Settings.QuestName)
                    .Replace("{hero}",     hero.FirstName?.Raw())
                    .Replace("{current}",  slot.BattlesSurvived.ToString())
                    .Replace("{required}", slot.Settings.BattlesRequired.ToString());
                Notify(msg, hero);
            }
        }

        // ── Hero killed ───────────────────────────────────────────────────────

        private void OnHeroKilled(Hero victim, Hero killer,
            TaleWorlds.CampaignSystem.Actions.KillCharacterAction.KillCharacterActionDetail detail,
            bool showNotification)
        {
            FailQuestIfVictim(victim, ref _weaponQuest);
            FailQuestIfVictim(victim, ref _armorQuest);
        }

        private static void FailQuestIfVictim(Hero victim, ref GrailActiveQuest slot)
        {
            if (slot == null || slot.Hero != victim) return;
            var msg = slot.Settings.QuestFailedMessage
                .Replace("{quest}", slot.Settings.QuestName)
                .Replace("{hero}",  victim.FirstName?.Raw());
            Notify(msg, victim);
            Log.Info($"[BLTGrail] Quest '{slot.Settings.QuestName}' failed — {victim.Name} killed.");
            slot = null;
        }

        // ── Nagroda ───────────────────────────────────────────────────────────

        private static void CompleteQuest(ref GrailActiveQuest slot)
        {
            var hero     = slot.Hero;
            var settings = slot.Settings;
            slot = null;  // wyczyść przed GenerateReward (w razie wyjątku)

            try
            {
                var heroClass = BLTAdoptAHeroCampaignBehavior.Current?.GetClass(hero);
                // tier > 5 triggers custom modifier generation (same as !smithweapon passing tier=6)
                int tier = settings.ItemTier >= 6 ? 6 : Math.Max(0, settings.ItemTier - 1);
                var modifier = GetBLTModifiers();

                // GenerateRewardType(Weapon) już używa heroClass wewnętrznie — nie nadpisuj ItemType
                // Armor Quest zostaje Armor, Weapon Quest dobiera broń do klasy bohatera
                var rewardType = settings.ItemType;

                var (item, mod, itemSlot) = RewardHelpers.GenerateRewardType(
                    rewardType, tier, hero, heroClass,
                    allowDuplicates: true,
                    modifierDef: modifier,
                    customItemName: string.IsNullOrWhiteSpace(settings.ItemName) ? null : settings.ItemName,
                    customItemPower: settings.ItemPower);

                // Jeśli nie znaleziono itemka z klasą, spróbuj bez klasy
                if (item == null && heroClass != null)
                    (item, mod, itemSlot) = RewardHelpers.GenerateRewardType(
                        rewardType, tier, hero, null,
                        allowDuplicates: true, modifierDef: modifier,
                        customItemName: string.IsNullOrWhiteSpace(settings.ItemName) ? null : settings.ItemName,
                        customItemPower: settings.ItemPower);

                if (item == null)
                {
                    Log.Error($"[BLTGrail] Could not generate reward for {hero.Name}");
                    return;
                }

                // Najpierw zarejestruj item przez BLT (dodaje do storage)
                RewardHelpers.AssignCustomReward(hero, item, mod, itemSlot);

                // Force-equip: Grail reward goes directly into the slot even if ShouldReplaceItem would refuse
                string reward = item.Name?.ToString() ?? "Holy Grail Item";
                if (itemSlot != EquipmentIndex.None)
                {
                    try
                    {
                        hero.BattleEquipment[itemSlot] = new EquipmentElement(item, mod);
                        hero.CivilianEquipment[itemSlot] = new EquipmentElement(item, mod);
                    }
                    catch { }
                }
                else
                {
                    // Brak konkretnego slotu — szukaj pierwszego pasującego wolnego lub najsłabszego
                    for (var idx = EquipmentIndex.WeaponItemBeginSlot; idx < EquipmentIndex.NumAllWeaponSlots; idx++)
                    {
                        var current = hero.BattleEquipment[idx];
                        if (current.IsEmpty || (!BLTAdoptAHeroCampaignBehavior.Current
                                .GetCustomItems(hero).Any(ci => ci.Item == current.Item)))
                        {
                            try
                            {
                                hero.BattleEquipment[idx] = new EquipmentElement(item, mod);
                                hero.CivilianEquipment[idx] = new EquipmentElement(item, mod);
                                break;
                            }
                            catch { }
                        }
                    }
                }

                var msg = settings.QuestCompleteMessage
                    .Replace("{quest}", settings.QuestName)
                    .Replace("{hero}",  hero.FirstName?.Raw())
                    + $" [{reward}]";

                Notify(msg, hero);
                Log.Info($"[BLTGrail] Quest '{settings.QuestName}' complete! {hero.Name} received: {reward}");
            }
            catch (Exception ex)
            {
                Log.Exception("[BLTGrail] CompleteQuest failed", ex);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // Pobiera CustomRewardModifiers z BLTAdoptAHeroModule.CommonConfig (internal)
        // — ten sam modifier co !smithweapon używa
        private static RandomItemModifierDef GetBLTModifiers()
        {
            try
            {
                var moduleType = typeof(BLTAdoptAHeroCampaignBehavior).Assembly
                    .GetTypes().FirstOrDefault(t => t.Name == "BLTAdoptAHeroModule");
                var prop = moduleType?.GetProperty("CommonConfig",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var commonConfig = prop?.GetValue(null);
                var modProp = commonConfig?.GetType()
                    .GetProperty("CustomRewardModifiers",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return modProp?.GetValue(commonConfig) as RandomItemModifierDef
                    ?? new RandomItemModifierDef();
            }
            catch { return new RandomItemModifierDef(); }
        }

        private static void Notify(string message, Hero hero = null)
        {
            Log.ShowInformation(message, hero?.CharacterObject);
            try
            {
                var serviceType = Type.GetType("BannerlordTwitch.Twitch.TwitchService, BannerlordTwitch");
                var service = serviceType
                    ?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(null);
                var bot = service?.GetType()
                    .GetField("bot", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(service);
                bot?.GetType()
                    .GetMethod("SendChat", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.Invoke(bot, new object[] { new[] { message } });
            }
            catch { }
        }
    }


    // ======================================================================
    // BLTAuras
    // ======================================================================

public class BLTAurasModule : MBSubModuleBase
    {
        private static Harmony harmony;

        public BLTAurasModule()
        {
            ActionManager.RegisterAll(typeof(BLTAurasModule).Assembly);
            // Rejestracja w konstruktorze — niezależna od PatchAll, na pewno wykona się przed ładowaniem ustawień
            try { PowerProgressionGlobalConfig.Register(); } catch (Exception ex) { Log.Exception("[PowerProg] Register failed", ex); }
            // TOR-only configs (Culture Restriction / Equip From Culture) NOT registered in the
            // 1.3.15 Warsails line - they belong to the separate hidden TOR package. Classes stay
            // dormant in the DLL; without Register() they never appear in the config UI.
            try { WandererGlobalConfig.Register(); } catch (Exception ex) { Log.Exception("[Wanderer] Register failed", ex); }
            try { AdrenalineGlobalConfig.Register(); } catch (Exception ex) { Log.Exception("[Adrenaline] Register failed", ex); }
            try { HeroBarGlobalConfig.Register(); } catch (Exception ex) { Log.Exception("[HeroBar] Register failed", ex); }
            // MBGA - Tournament Loadout NOT registered in 1.3.15 Warsails line: TOR-specific and the
            // fork already provides tournament armor normalization. Class dormant in DLL.
            try { MBGARetinueRefundConfig.Register(); } catch (Exception ex) { Log.Exception("[RetinueRefund] Register failed", ex); }
            // MBGA - Prestige NOT registered in 1.3.15 Warsails line: the fork (BLTAdoptAHero 5.4.x)
            // has native prestige (Global Common Config > Prestige System, handler PrestigeHero). The
            // config's !prestige command is repointed to PrestigeHero. MBGA prestige classes dormant.
            try { MBGADiscardRefundConfig.Register(); } catch (Exception ex) { Log.Exception("[DiscardRefund] Register failed", ex); }
            // MBGA - Tier 7-8 Progression NOT registered in 1.3.15 Warsails line: the fork's !equip
            // already provides Tier 7/8 (Elite/Legendary). Duplicate. Class + patches dormant.
            // MBGA - Dragon-Chariot Mounts NOT registered in 1.3.15 Warsails line: the fork already
            // provides Dragon/Chariot per-class (HeroClassDef). Duplicate. Class dormant in DLL.
        }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            if (harmony != null) return;
            try
            {
                harmony = new Harmony("mod.bannerlord.bltauras");
                if (MbgaPatchGuard.ShouldApply()) harmony.PatchAll();

                // Patch progresji aplikowany RĘCZNIE i tylko RAZ (tu, w jednym module) —
                // NIE przez [HarmonyPatch]/PatchAll, bo to aplikowałoby go 9× (9 modułów MBGA = 9 PatchAll).
                var target = AccessTools.Method(typeof(PassivePowerGroup), nameof(PassivePowerGroup.OnHeroJoinedBattle));
                var prefix = typeof(PowerProgressionPatches).GetMethod(
                    nameof(PowerProgressionPatches.OnHeroJoinedBattle_Prefix),
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (target != null && prefix != null)
                    harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                else
                    Log.Info($"[PowerProg] Patch target/prefix not found (target={target != null}, prefix={prefix != null})");

                // TOR-only culture patches (Culture Restriction / Equip From Culture) NOT applied in
                // the 1.3.15 Warsails line - they belong to the separate hidden TOR package.

                // Human children — ręcznie raz (nie [HarmonyPatch]/PatchAll)
                var childTarget = HumanChildPatch.TargetMethod();
                var childPostfix = typeof(HumanChildPatch).GetMethod(
                    nameof(HumanChildPatch.Postfix),
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (childTarget != null && childPostfix != null)
                    harmony.Patch(childTarget, postfix: new HarmonyMethod(childPostfix));
                else
                    Log.Info($"[HumanChild] Patch target/postfix not found (target={childTarget != null}, postfix={childPostfix != null})");

                // AddDamage progression — ręcznie raz (oryginalna moc BLT, skaluje DamageModifierPercent/DamageToAdd per tier)
                var addDmgTarget = AccessTools.Method(typeof(AddDamagePower), "OnDoDamage");
                if (addDmgTarget != null)
                    harmony.Patch(addDmgTarget,
                        prefix: new HarmonyMethod(typeof(AddDamageProgressionPatch).GetMethod(nameof(AddDamageProgressionPatch.Prefix), BindingFlags.Static | BindingFlags.Public)),
                        postfix: new HarmonyMethod(typeof(AddDamageProgressionPatch).GetMethod(nameof(AddDamageProgressionPatch.Postfix), BindingFlags.Static | BindingFlags.Public)));
                else
                    Log.Info("[AddDamageProgression] OnDoDamage target not found");

                // Adrenaline mounted charge damage — ręcznie raz na Mission.RegisterBlow (jak DamageTrackingPatch)
                var chargeTarget = AccessTools.Method(typeof(Mission), "RegisterBlow");
                var chargePrefix = typeof(AdrenalineChargePatch).GetMethod(
                    nameof(AdrenalineChargePatch.Prefix), BindingFlags.Static | BindingFlags.Public);
                if (chargeTarget != null && chargePrefix != null)
                    harmony.Patch(chargeTarget, prefix: new HarmonyMethod(chargePrefix));
                else
                    Log.Info($"[AdrenalineCharge] Patch target/prefix not found (target={chargeTarget != null}, prefix={chargePrefix != null})");

                Log.Info("[BLTAuras] Loaded: PoisonStrike / Berserk / LastStand / Taunt / Auras / Kick / JumpAttack / Teleport / PowerProgression / BannerFix / HumanChild");
            }
            catch (Exception ex)
            {
                Log.Exception("[BLTAuras] Load failed", ex);
            }

            // Wire the BLTAdoptAHero.BLTExternalStats bridge so the fork's new hero bar / overlay
            // (BLTHeroWidgetBehavior) actually reads OUR config + wanderer/adrenaline data. Without
            // this, GetUseNewHeroBarLayout stays null -> fork falls back to the OLD 5.2.4 bar and the
            // overlay toggles do nothing (exactly the "no new bar / no overlay" symptom). Done via
            // reflection, NOT a direct reference: the type exists in the bundled fork DLL but not in
            // the original Randomchair22 DLL, so a compile-time reference would break other builds.
            try
            {
                var ext = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => { try { return a.GetType("BLTAdoptAHero.BLTExternalStats"); } catch { return null; } })
                    .FirstOrDefault(t => t != null);
                if (ext != null)
                {
                    void Set<T>(string field, T value) where T : class
                    { try { ext.GetField(field, BindingFlags.Public | BindingFlags.Static)?.SetValue(null, value); } catch (Exception e) { Log.Exception($"[HeroBar] bridge set {field}", e); } }

                    // Toggles (config lives in MBGA HeroBarGlobalConfig now).
                    Set("GetUseNewHeroBarLayout", (Func<bool>)(() => HeroBarGlobalConfig.Get()?.UseNewHeroBarLayout ?? true));
                    Set("GetShowSideBars",        (Func<bool>)(() => HeroBarGlobalConfig.Get()?.ShowSideBars ?? true));
                    Set("GetShowMissionOverlay",  (Func<bool>)(() => HeroBarGlobalConfig.Get()?.ShowMissionOverlay ?? true));
                    // Data feeds for the bar/overlay.
                    Set("CompanionCount",   (Func<Hero, int>)(h => BLTWandererBehavior.CountForHero(h)));
                    Set("IsWandererHero",   (Func<Hero, bool>)(h => BLTWandererBehavior.IsWandererStringId(h?.StringId)));
                    Set("WandererKillCount",(Func<Hero, int>)(h => BLTWandererKillTracker.Get(h)));
                    Set("AdrenalineFraction",(Func<Hero, float>)(h => AdrenalineMissionBehavior.RemainingFraction(h)));
                    Log.Info("[HeroBar] BLTExternalStats bridge wired (new bar/overlay toggles + wanderer/adrenaline feeds).");
                }
                else
                {
                    Log.Info("[HeroBar] BLTExternalStats type not found - running without the fork bar bridge (standalone?).");
                }
            }
            catch (Exception ex) { Log.Exception("[HeroBar] BLTExternalStats bridge wiring failed", ex); }
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);
            if (gameStarterObject is CampaignGameStarter campaignStarter)
            {
                campaignStarter.AddBehavior(new BLTBannerSanitizerBehavior());
                campaignStarter.AddBehavior(new BLTWandererBehavior());
                // BLTMBGAPrestigeBehavior NOT added - using the fork's native prestige instead.
            }
#if BLT_1315
            ROTCompatGuard.TryApply(harmony);
            MapDistanceModelGuard.TryApply(harmony);
            NativeCrashGuard.TryApply(harmony);
#endif
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            base.OnMissionBehaviorInitialize(mission);
            mission.AddMissionBehavior(new WandererSpawnMissionBehavior());
            mission.AddMissionBehavior(new AdrenalineMissionBehavior());
            // Added eagerly at mission start, NOT lazily on first use. Both NecromancyMissionBehavior
            // and BLTTimedBuffBehavior otherwise get AddMissionBehavior()'d from inside a combat
            // callback (OnGotAKill / buff application), which mutates the mission's behavior list
            // while the engine is iterating it -> "Collection was modified" InvalidOperationException.
            // Both are no-ops when idle, so adding them up front is free. (Their lazy Track()/Get()
            // fallbacks stay as harmless safety nets - Current will already be set here.)
            mission.AddMissionBehavior(new BLTTimedBuffBehavior());
            mission.AddMissionBehavior(new NecromancyMissionBehavior());
            // MBGAPrestigeKillTrackerBehavior NOT added - using the fork's native prestige instead.
        }

    }

    // Skalowanie oryginalnej mocy BLT AddDamagePower przez progresję — patch ręczny (bez [HarmonyPatch], aplikowany raz).
    public static class AddDamageProgressionPatch
    {
        public static void Prefix(AddDamagePower __instance, Agent agent, out float[] __state)
        {
            __state = null;
            try
            {
                var cfg = PowerProgressionGlobalConfig.Get();
                if (cfg == null || !cfg.Enabled) return;
                var hero = (agent?.Character as CharacterObject)?.HeroObject;
                if (hero == null) return;
                __state = new float[] { __instance.DamageModifierPercent, __instance.DamageToAdd };
                __instance.DamageModifierPercent = PowerProgression.ScaleFloat(__instance, hero, "DamageModifierPercent", __instance.DamageModifierPercent);
                __instance.DamageToAdd = PowerProgression.ScaleInt(__instance, hero, "DamageToAdd", __instance.DamageToAdd);
            }
            catch { __state = null; }
        }

        public static void Postfix(AddDamagePower __instance, float[] __state)
        {
            if (__state == null) return;
            try
            {
                __instance.DamageModifierPercent = __state[0];
                __instance.DamageToAdd = (int)__state[1];
            }
            catch { }
        }
    }

    // ============================================================================
    // COMPOSABLE POWER ENGINE -- PHASE 1 PILOT ("Uprising of the Peasants" 5.4.1)
    // See docs/superpowers/plans/2026-07-17-uprising-of-the-peasants-composable-powers.md
    //
    // Two reusable effect helpers extracted from PoisonStrikePower / DamageAuraPower below (kept
    // byte-for-byte untouched - this pilot runs SIDE BY SIDE with the originals, never replaces
    // them). A single new opt-in power, ComposedPowerDef, composes these helpers to prove that one
    // power slot can express what used to require several (e.g. "Damage Over Time" on-hit + a
    // "Damage Aura" pulse + an aura-wide Slow, all from ONE power entry - the "Blizzard" combo).
    //
    // Deliberately NOT a new MissionBehavior/dispatcher: both helpers wire onto the SAME
    // PowerHandler.Handlers bus every other power already uses (handlers.OnDoDamage/OnSlowTick),
    // registered inside OnActivation exactly like every existing power - no new Harmony patches,
    // no new mission-behavior registration, so none of today's three regression classes (contour
    // self-recursion, lazy mid-combat AddMissionBehavior, PatchAll fan-out) apply here by
    // construction. Both helpers call ONLY the shared ContourHelpers.SafeSetContourColor - never
    // define their own copy - keeping the contour-recursion surface at the same call sites as
    // every other power in this file.
    // ============================================================================

    // Wires an on-hit damage-over-time effect (poison/bleed/burn are all this same primitive with
    // different tuning/color) onto a hero's existing power-handler bus. Extracted verbatim from
    // PoisonStrikePower.OnActivation - same Blow-construction, same tracked-agent dictionary, same
    // cleanup-on-deactivate/mission-over pattern, just parameterized instead of hardcoded.
    public class DamageOverTimeEffect
    {
        private readonly int damagePerTick;
        private readonly int durationTicks;
        private readonly PoisonApplyOn applyOn;
        private readonly bool showContour;
        private readonly uint contourColor;
        private readonly float weaponDropChancePercent;
        private readonly float dismountChancePercent;
        private readonly Dictionary<Agent, int> tracked = new Dictionary<Agent, int>();

        public DamageOverTimeEffect(int damagePerTick, int durationTicks, PoisonApplyOn applyOn,
            bool showContour, uint contourColor, float weaponDropChancePercent = 0f, float dismountChancePercent = 0f)
        {
            this.damagePerTick = damagePerTick;
            this.durationTicks = durationTicks;
            this.applyOn = applyOn;
            this.showContour = showContour;
            this.contourColor = contourColor;
            this.weaponDropChancePercent = weaponDropChancePercent;
            this.dismountChancePercent = dismountChancePercent;
        }

        // Caller wires Cleanup() to whatever deactivation/mission-over hook is appropriate on its
        // side - kept as a plain public method instead of taking DeactivationHandler directly,
        // since that type is `protected` on DurationMissionHeroPowerDefBase and not visible to an
        // unrelated standalone helper class like this one.
        public void Wire(Hero hero, PowerHandler.Handlers handlers)
        {
            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (victim == null || !victim.IsActive()) return;
                if (attacker == null || !victim.IsEnemyOf(attacker)) return;
                if (applyOn != PoisonApplyOn.Both)
                {
                    bool isRanged = attacker != null
                        && !attacker.WieldedWeapon.IsEmpty
                        && attacker.WieldedWeapon.CurrentUsageItem?.IsRangedWeapon == true;
                    if (applyOn == PoisonApplyOn.RangedOnly && !isRanged) return;
                    if (applyOn == PoisonApplyOn.MeleeOnly && isRanged) return;
                }
                tracked[victim] = durationTicks;
                if (showContour) try { SafeSetContourColor(victim, contourColor, true); } catch { }
            };

            handlers.OnSlowTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;
                foreach (var key in tracked.Keys.ToList())
                {
                    if (key == null || !key.IsActive()) { tracked.Remove(key); continue; }
                    try
                    {
                        var dir = Vec3.Up;
                        var blow = new Blow(heroAgent.Index)
                        {
                            AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Pierce,
                            BoneIndex = key.Monster.HeadLookDirectionBoneIndex, GlobalPosition = key.Position,
                            BaseMagnitude = damagePerTick, InflictedDamage = damagePerTick,
                            SwingDirection = dir, Direction = dir, DamageCalculated = true,
                            VictimBodyPart = BoneBodyPartType.Chest,
                        };
                        // Dismount reuses the fork's own HitBehavior struct (same mechanism the
                        // native AddDamagePower uses) rather than reinventing dismount handling.
                        if (dismountChancePercent > 0f)
                        {
                            var hitBehavior = new HitBehavior { DismountChance = dismountChancePercent };
                            blow.BlowFlag = hitBehavior.AddFlags(key, blow.BlowFlag);
                        }
                        key.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, key, blow));

                        if (weaponDropChancePercent > 0f && MBRandom.RandomFloat * 100f < weaponDropChancePercent)
                        {
                            for (var wi = EquipmentIndex.WeaponItemBeginSlot; wi < EquipmentIndex.NumAllWeaponSlots; wi++)
                            {
                                if (!key.Equipment[wi].IsEmpty) { key.DropItem(wi); break; }
                            }
                        }
                    }
                    catch { }
                    tracked[key]--;
                    if (tracked[key] <= 0)
                    {
                        if (showContour) try { SafeSetContourColor(key, null, false); } catch { }
                        tracked.Remove(key);
                    }
                }
            };
        }

        public void Cleanup()
        {
            if (showContour)
                foreach (var a in tracked.Keys) try { SafeSetContourColor(a, null, false); } catch { }
            tracked.Clear();
        }
    }

    // Wires a periodic radius sweep around the hero (aura pulse) onto the power-handler bus.
    // Extracted verbatim from DamageAuraPower.OnActivation - same GetNearbyAgents sweep, same
    // lastInRange contour diffing, same tick-interval gating - generalized with an onTickAgent
    // callback so the composing power decides what happens to each agent caught in the sweep
    // (deal damage, apply a slow, heal, etc.) instead of hardcoding "deal damage".
    public class AuraSweepEffect
    {
        private readonly float radius;
        private readonly float tickInterval;
        private readonly int maxAgents;
        private readonly bool targetEnemies;
        private readonly bool includeSelf;
        private readonly bool showContour;
        private readonly uint contourColor;
        private readonly Action<Agent, Agent> onTickAgent; // (heroAgent, targetAgent)
        private readonly Action<Agent> onAgentEnter; // fires once when an agent first enters the sweep
        private readonly Action<Agent> onAgentExit;  // fires once when an agent leaves the sweep (or the aura ends)
        private float lastTick = -999f;
        private readonly HashSet<Agent> lastInRange = new HashSet<Agent>();

        // includeSelf only matters when targetEnemies is false (ally-targeting sweep) - matches
        // the original HealAuraPower's "Heal Self" toggle, which this generalized helper dropped
        // by defaulting to hero-excluded until this parameter was added back.
        // onAgentEnter/onAgentExit are optional (default null) - only ComposedAuraPower's
        // Buff/Debuff block uses them (to hand persistent per-agent state to
        // ComposedAuraDamageTracker / BLTAgentModifierBehavior); every other caller (DoT, Heal,
        // Slow) passes nothing and is unaffected.
        public AuraSweepEffect(float radius, float tickInterval, int maxAgents, bool targetEnemies,
            bool showContour, uint contourColor, Action<Agent, Agent> onTickAgent, bool includeSelf = true,
            Action<Agent> onAgentEnter = null, Action<Agent> onAgentExit = null)
        {
            this.radius = radius;
            this.tickInterval = tickInterval;
            this.maxAgents = maxAgents;
            this.targetEnemies = targetEnemies;
            this.includeSelf = includeSelf;
            this.showContour = showContour;
            this.contourColor = contourColor;
            this.onTickAgent = onTickAgent;
            this.onAgentEnter = onAgentEnter;
            this.onAgentExit = onAgentExit;
        }

        public void Wire(Hero hero, PowerHandler.Handlers handlers)
        {
            var nearbyBuffer = new MBList<Agent>();

            handlers.OnSlowTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive())
                {
                    foreach (var a in lastInRange)
                    {
                        try { SafeSetContourColor(a, null, false); } catch { }
                        if (onAgentExit != null) try { onAgentExit(a); } catch { }
                    }
                    lastInRange.Clear();
                    return;
                }
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastTick < tickInterval) return;
                lastTick = now;
                nearbyBuffer.Clear();
                var nowInRange = new HashSet<Agent>(Mission.Current
                    .GetNearbyAgents(heroAgent.Position.AsVec2, radius, nearbyBuffer)
                    .Where(a => a.IsActive() && !a.IsMount
                        && (targetEnemies ? a.IsEnemyOf(heroAgent) : (!a.IsEnemyOf(heroAgent) && (includeSelf || a != heroAgent))))
                    .OrderBy(a => a.Position.Distance(heroAgent.Position))
                    .Take(Math.Max(1, maxAgents)));

                foreach (var a in lastInRange)
                    if (!nowInRange.Contains(a))
                    {
                        try { SafeSetContourColor(a, null, false); } catch { }
                        if (onAgentExit != null) try { onAgentExit(a); } catch { }
                    }

                if (onAgentEnter != null)
                    foreach (var a in nowInRange)
                        if (!lastInRange.Contains(a))
                            try { onAgentEnter(a); } catch { }

                if (showContour)
                    foreach (var a in nowInRange) try { SafeSetContourColor(a, contourColor, true); } catch { }

                foreach (var target in nowInRange)
                {
                    try { onTickAgent(heroAgent, target); } catch { }
                }

                lastInRange.Clear();
                foreach (var a in nowInRange) lastInRange.Add(a);
            };

        }

        public void Cleanup()
        {
            foreach (var a in lastInRange)
            {
                try { SafeSetContourColor(a, null, false); } catch { }
                if (onAgentExit != null) try { onAgentExit(a); } catch { }
            }
            lastInRange.Clear();
        }
    }

    // Three separate composed-power types (split from an earlier single "Composed Power (Beta)"
    // that crammed DoT + Aura + self-buff into one 22-property power - exactly the "intimidating
    // dropdown" problem this whole effort is meant to solve). Each is now its own, focused
    // power-picker entry built from the helpers above; a class can pick MULTIPLE of these
    // together to reproduce the old all-in-one combos (e.g. "Composed DoT" + "Composed Aura" on
    // the same class = a DoT-and-aura hybrid). See
    // docs/superpowers/plans/2026-07-17-uprising-of-the-peasants-composable-powers.md.

    [DisplayName("Composed DoT (Beta)"),
     Description("PILOT/BETA - on-hit damage-over-time (poison/bleed/burn-style), with optional weapon-drop and dismount chance per tick."),
     UsedImplicitly]
    public class ComposedDoTPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Damage Per Tick"), Description("Damage dealt each tick while affected."), PropertyOrder(1), UsedImplicitly]
        public int DamagePerTick { get; set; } = 10;

        [DisplayName("Duration (ticks)"), Description("How many ticks the effect lasts."), PropertyOrder(2), UsedImplicitly]
        public int DurationTicks { get; set; } = 5;

        [DisplayName("Apply On"), Description("Which kind of attacks trigger this effect (melee, ranged, or both)."), PropertyOrder(3), UsedImplicitly]
        public PoisonApplyOn ApplyOn { get; set; } = PoisonApplyOn.Both;

        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline - visual only."), PropertyOrder(4), UsedImplicitly]
        public bool ShowContour { get; set; } = true;

        // EXPERIMENTAL: visual color picker instead of typing a hex code, using Xceed's
        // PropertyGrid ColorEditor (binds to System.Windows.Media.Color). This is the first use
        // of this editor/type anywhere in the project - every other power's contour color is a
        // plain hex string. Trying it here first, isolated to one field, before touching the
        // other ~35 contour-color fields across the file.
        [DisplayName("Contour Color"), Description("Outline color shown on affected agents - visual only."),
         Editor(typeof(ColorEditor), typeof(ColorEditor)), PropertyOrder(5), UsedImplicitly]
        public WpfColor ContourColor { get; set; } = WpfColor.FromArgb(0xFF, 0x00, 0xCC, 0x00);

        [DisplayName("Weapon Drop Chance (%)"), Description("Chance per tick that an affected agent drops their weapon. 0 = disabled."), PropertyOrder(6), UsedImplicitly]
        public float WeaponDropChancePercent { get; set; } = 0f;

        [DisplayName("Dismount Chance (%)"), Description("Chance per tick that an affected mounted agent is dismounted. 0 = disabled."), PropertyOrder(7), UsedImplicitly]
        public float DismountChancePercent { get; set; } = 0f;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = (uint)((ContourColor.A << 24) | (ContourColor.R << 16) | (ContourColor.G << 8) | ContourColor.B);
            int damagePerTick = PowerProgression.ScaleInt(this, hero, nameof(DamagePerTick), DamagePerTick);
            int durationTicks = PowerProgression.ScaleInt(this, hero, nameof(DurationTicks), DurationTicks);
            var dot = new DamageOverTimeEffect(damagePerTick, durationTicks, ApplyOn, ShowContour, color,
                WeaponDropChancePercent, DismountChancePercent);
            dot.Wire(hero, handlers);
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => dot.Cleanup();
            handlers.OnMissionOver += dot.Cleanup;
        }

        public override LocString Description =>
            $"Composed DoT (Beta): {DamagePerTick} dmg/tick for {DurationTicks} ticks"
            + $"{(WeaponDropChancePercent > 0 ? $", {WeaponDropChancePercent:0}% weapon drop" : "")}"
            + $"{(DismountChancePercent > 0 ? $", {DismountChancePercent:0}% dismount" : "")}";
    }

    [DisplayName("Composed Aura (Beta)"),
     Description("PILOT/BETA - periodic radius sweep: damage, optional slow, optional heal (target allies instead of enemies), optional weapon-drop/dismount chance per tick."),
     UsedImplicitly]
    public class ComposedAuraPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Damage Per Tick"), Description("Damage dealt to each agent caught in the sweep, per tick. 0 = no damage (useful if you only want the Slow/Heal)."), PropertyOrder(1), UsedImplicitly]
        public int DamagePerTick { get; set; } = 8;

        [DisplayName("Radius (meters)"), Description("How far from the hero this aura reaches."), PropertyOrder(2), UsedImplicitly]
        public float Radius { get; set; } = 8f;

        [DisplayName("Tick Interval (seconds)"), Description("How often the aura re-evaluates and re-applies."), PropertyOrder(3), UsedImplicitly]
        public float TickIntervalSeconds { get; set; } = 3f;

        [DisplayName("Max Agents Per Tick"), Description("Caps how many nearby agents this aura can hit per tick, closest first."), PropertyOrder(4), UsedImplicitly]
        public int MaxAgentsPerTick { get; set; } = 5;

        [DisplayName("Target Enemies"), Description("Checked = hits enemies (damage aura). Unchecked = hits allies (heal-style aura - pair with 0 damage and Slow off)."), PropertyOrder(5), UsedImplicitly]
        public bool TargetEnemies { get; set; } = true;

        [DisplayName("Show Contour"), Description("Highlights agents caught in the aura - visual only."), PropertyOrder(6), UsedImplicitly]
        public bool ShowContour { get; set; } = true;

        [DisplayName("Contour Color"), Description("Outline color shown on agents caught in the aura - visual only."),
         Editor(typeof(ColorEditor), typeof(ColorEditor)), PropertyOrder(7), UsedImplicitly]
        public WpfColor ContourColor { get; set; } = WpfColor.FromArgb(0xFF, 0xFF, 0x22, 0x00);

        [DisplayName("Slow Enabled"), Description("If on, agents caught in the aura also have their movement speed capped (combine with damage for a Blizzard-style combo)."), PropertyOrder(8), UsedImplicitly]
        public bool SlowEnabled { get; set; } = false;

        [DisplayName("Slow Speed Limit"), Description("Maximum movement speed fraction (0-1) while inside the aura - lower is a stronger slow."), PropertyOrder(9), UsedImplicitly]
        public float SlowSpeedLimit { get; set; } = 0.5f;

        [DisplayName("Heal Per Tick"), Description("HP restored to each agent caught in the aura, per tick. Only makes sense with Target Enemies OFF (heal allies). 0 = disabled."), PropertyOrder(10), UsedImplicitly]
        public float HealPerTick { get; set; } = 0f;

        [DisplayName("Include Self"), Description("Only matters with Target Enemies OFF: whether the hero's own agent is included as a target of this aura (e.g. so a heal aura also heals the caster). ON by default, matching the original Heal Aura's 'Heal Self' behavior."), PropertyOrder(11), UsedImplicitly]
        public bool IncludeSelf { get; set; } = true;

        [DisplayName("Weapon Drop Chance (%)"), Description("Chance per tick that each agent caught in the aura drops their weapon. 0 = disabled."), PropertyOrder(12), UsedImplicitly]
        public float WeaponDropChancePercent { get; set; } = 0f;

        [DisplayName("Dismount Chance (%)"), Description("Chance per tick that each mounted agent caught in the aura is dismounted. 0 = disabled."), PropertyOrder(13), UsedImplicitly]
        public float DismountChancePercent { get; set; } = 0f;

        [DisplayName("Buff: Target Damage Bonus (%)"), Description("Percentage bonus/penalty applied to outgoing damage of every agent caught in the aura, for as long as they stay in it. Positive = buff, negative = debuff. Works on enemies, allies, and regular troops (not just other adopted heroes). 0 = disabled."), PropertyOrder(14), UsedImplicitly]
        public float TargetDamageBonusPercent { get; set; } = 0f;

        [DisplayName("Buff: Target Armor Bonus (%)"), Description("Percentage bonus/penalty applied to armor (head/torso/arms/legs) of every agent caught in the aura, for as long as they stay in it. Positive = buff, negative = debuff. Works on enemies, allies, and regular troops. 0 = disabled."), PropertyOrder(15), UsedImplicitly]
        public float TargetArmorBonusPercent { get; set; } = 0f;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = (uint)((ContourColor.A << 24) | (ContourColor.R << 16) | (ContourColor.G << 8) | ContourColor.B);
            int damagePerTick = PowerProgression.ScaleInt(this, hero, nameof(DamagePerTick), DamagePerTick);
            float radius      = PowerProgression.ScaleFloat(this, hero, nameof(Radius), Radius);
            int maxAgents     = PowerProgression.ScaleInt(this, hero, nameof(MaxAgentsPerTick), MaxAgentsPerTick);
            bool slowEnabled = SlowEnabled;
            float slowLimit = SlowSpeedLimit;
            float healPerTick = PowerProgression.ScaleFloat(this, hero, nameof(HealPerTick), HealPerTick);
            float weaponDropChancePercent = WeaponDropChancePercent;
            float dismountChancePercent = DismountChancePercent;
            float targetDamageBonusPercent = TargetDamageBonusPercent;
            float targetArmorBonusPercent = TargetArmorBonusPercent;
            var armorConfigs = new Dictionary<Agent, AgentModifierConfig>();

            void OnAgentEnter(Agent target)
            {
                if (targetDamageBonusPercent != 0f)
                    ComposedAuraDamageTracker.Set(target, targetDamageBonusPercent);
                if (targetArmorBonusPercent != 0f)
                {
                    var config = new AgentModifierConfig();
                    float armorMult = 100f + targetArmorBonusPercent;
                    config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ArmorHead, ModifierPercent = armorMult });
                    config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ArmorTorso, ModifierPercent = armorMult });
                    config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ArmorArms, ModifierPercent = armorMult });
                    config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ArmorLegs, ModifierPercent = armorMult });
                    armorConfigs[target] = config;
                    BLTAgentModifierBehavior.Current?.Add(target, config);
                }
            }

            void OnAgentExit(Agent target)
            {
                if (targetDamageBonusPercent != 0f)
                    ComposedAuraDamageTracker.Remove(target);
                if (armorConfigs.TryGetValue(target, out var config))
                {
                    BLTAgentModifierBehavior.Current?.Remove(target, config);
                    armorConfigs.Remove(target);
                }
            }

            void OnAuraTick(Agent heroAgent, Agent target)
            {
                if (healPerTick > 0f)
                    try { target.Health = Math.Min(target.Health + healPerTick, target.HealthLimit); } catch { }
                if (slowEnabled)
                    try { target.SetMaximumSpeedLimit(slowLimit, false); } catch { }
                if (damagePerTick > 0)
                {
                    try
                    {
                        var dir = Vec3.Up;
                        var blow = new Blow(heroAgent.Index)
                        {
                            AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Pierce,
                            BoneIndex = target.Monster.HeadLookDirectionBoneIndex, GlobalPosition = target.Position,
                            BaseMagnitude = damagePerTick, InflictedDamage = damagePerTick,
                            SwingDirection = dir, Direction = dir, DamageCalculated = true,
                            VictimBodyPart = BoneBodyPartType.Chest,
                        };
                        if (dismountChancePercent > 0f)
                        {
                            var hitBehavior = new HitBehavior { DismountChance = dismountChancePercent };
                            blow.BlowFlag = hitBehavior.AddFlags(target, blow.BlowFlag);
                        }
                        target.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, target, blow));
                    }
                    catch { }
                }
                if (weaponDropChancePercent > 0f && MBRandom.RandomFloat * 100f < weaponDropChancePercent)
                {
                    try
                    {
                        for (var wi = EquipmentIndex.WeaponItemBeginSlot; wi < EquipmentIndex.NumAllWeaponSlots; wi++)
                        {
                            if (!target.Equipment[wi].IsEmpty) { target.DropItem(wi); break; }
                        }
                    }
                    catch { }
                }
            }

            var aura = new AuraSweepEffect(radius, TickIntervalSeconds, maxAgents, TargetEnemies,
                ShowContour, color, OnAuraTick, IncludeSelf, OnAgentEnter, OnAgentExit);
            aura.Wire(hero, handlers);
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => aura.Cleanup();
            handlers.OnMissionOver += aura.Cleanup;
            if (targetDamageBonusPercent != 0f) handlers.OnMissionOver += ComposedAuraDamageTracker.Clear;
        }

        public override LocString Description =>
            $"Composed Aura (Beta): {DamagePerTick}/tick r{Radius:0}m"
            + $"{(SlowEnabled ? $" (slow {SlowSpeedLimit:0.##}x)" : "")}"
            + $"{(HealPerTick > 0 ? $" (heal {HealPerTick:0}/tick)" : "")}"
            + $"{(TargetDamageBonusPercent != 0 ? $" (dmg buff {TargetDamageBonusPercent:0}%)" : "")}"
            + $"{(TargetArmorBonusPercent != 0 ? $" (armor buff {TargetArmorBonusPercent:0}%)" : "")}";
    }

    // Always = the original constant version. HpScaling reproduces Berserk (bonus grows smoothly
    // the lower HP gets, active continuously below the threshold). HpThreshold reproduces Last
    // Stand (fires once HP drops below the threshold, applies a flat bonus for a fixed duration).
    public enum SelfBuffTriggerMode { Always, HpScaling, HpThreshold }

    [DisplayName("Composed Self-Buff (Beta)"),
     Description("PILOT/BETA - self damage bonus/reduction. Trigger Mode picks the activation style: Always (constant), HP Scaling (Berserk-style - grows as HP drops), HP Threshold (Last Stand-style - fires once below a threshold for a fixed duration). PERSONAL ONLY: the power-handler bus a composed power runs on is scoped to the hero that owns it, so this cannot buff allies or debuff enemies (that needs different infrastructure, not yet built - see the plan doc)."),
     UsedImplicitly]
    public class ComposedSelfBuffPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Damage Bonus (%)"), Description("Percentage bonus added to this hero's own outgoing damage. Always: flat. HP Scaling: value reached at 0 HP, scales down to 0 at the threshold. HP Threshold: flat bonus while triggered."), PropertyOrder(1), UsedImplicitly]
        public float DamageBonusPercent { get; set; } = 0f;

        [DisplayName("Damage Reduction (%)"), Description("Percentage reduction applied to damage this hero takes. Same scaling rules per Trigger Mode as Damage Bonus."), PropertyOrder(2), UsedImplicitly]
        public float DamageReductionPercent { get; set; } = 0f;

        [DisplayName("Trigger Mode"), Description("Always = constant. HP Scaling = Berserk-style (grows as HP drops below Threshold HP %). HP Threshold = Last Stand-style (fires once below Threshold HP %, lasts Duration Seconds)."), PropertyOrder(3), UsedImplicitly]
        public SelfBuffTriggerMode TriggerMode { get; set; } = SelfBuffTriggerMode.Always;

        [DisplayName("Threshold HP (%)"), Description("Used by HP Scaling and HP Threshold modes: the HP percent below which the effect starts (HP Scaling) or triggers (HP Threshold). Ignored in Always mode."), PropertyOrder(4), UsedImplicitly]
        public float ThresholdHpPercent { get; set; } = 50f;

        [DisplayName("Duration (seconds)"), Description("HP Threshold mode only: how long the buff lasts once triggered."), PropertyOrder(5), UsedImplicitly]
        public float DurationSeconds { get; set; } = 10f;

        [DisplayName("Once Per Battle"), Description("HP Threshold mode only: whether this can only trigger once per battle."), PropertyOrder(6), UsedImplicitly]
        public bool OncePerBattle { get; set; } = true;

        [DisplayName("Show Contour"), Description("HP Scaling / HP Threshold modes only: highlights the hero with a colored outline while the effect is active - visual only."), PropertyOrder(7), UsedImplicitly]
        public bool ShowContour { get; set; } = true;

        [DisplayName("Contour Color"), Description("Outline color shown on the hero while the effect is active - visual only."),
         Editor(typeof(ColorEditor), typeof(ColorEditor)), PropertyOrder(8), UsedImplicitly]
        public WpfColor ContourColor { get; set; } = WpfColor.FromArgb(0xFF, 0x8B, 0x00, 0x00);

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float damageBonusPercent = PowerProgression.ScaleFloat(this, hero, nameof(DamageBonusPercent), DamageBonusPercent);
            float damageReductionPercent = PowerProgression.ScaleFloat(this, hero, nameof(DamageReductionPercent), DamageReductionPercent);
            uint color = (uint)((ContourColor.A << 24) | (ContourColor.R << 16) | (ContourColor.G << 8) | ContourColor.B);

            if (TriggerMode == SelfBuffTriggerMode.Always)
            {
                if (damageBonusPercent != 0f)
                {
                    handlers.OnDoDamage += (attacker, victim, blowParams) =>
                    {
                        blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * (1f + damageBonusPercent / 100f));
                    };
                }
                if (damageReductionPercent != 0f)
                {
                    handlers.OnTakeDamage += (victim, attacker, blowParams) =>
                    {
                        blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * (1f - damageReductionPercent / 100f));
                    };
                }
                return;
            }

            if (TriggerMode == SelfBuffTriggerMode.HpScaling)
            {
                // Berserk-style: bonus/reduction ramps up smoothly the further HP drops below the
                // threshold, reaching the configured percent at 0 HP. Extracted pattern from
                // BerserkPower.OnActivation above, generalized to also scale incoming-damage
                // reduction (the original only scaled outgoing damage).
                float threshold = ThresholdHpPercent / 100f;
                Agent trackedAgent = null;
                bool active = false;

                handlers.OnDoDamage += (attacker, victim, blowParams) =>
                {
                    if (attacker == null || attacker.HealthLimit <= 0) return;
                    var current = hero.GetAgent();
                    if (current != null && current != trackedAgent)
                    {
                        if (active && ShowContour) try { SafeSetContourColor(trackedAgent, null, false); } catch { }
                        trackedAgent = current;
                        active = false;
                    }
                    float hpRatio = attacker.Health / attacker.HealthLimit;
                    if (hpRatio >= threshold)
                    {
                        if (active && ShowContour) try { SafeSetContourColor(attacker, null, false); } catch { }
                        active = false;
                        return;
                    }
                    if (!active)
                    {
                        active = true;
                        if (ShowContour) try { SafeSetContourColor(attacker, color, true); } catch { }
                    }
                    if (damageBonusPercent != 0f)
                    {
                        float scaleRatio = 1f - (hpRatio / threshold);
                        blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * (1f + (damageBonusPercent / 100f) * scaleRatio));
                    }
                };

                if (damageReductionPercent != 0f)
                {
                    handlers.OnTakeDamage += (victim, attacker, blowParams) =>
                    {
                        if (victim == null || victim.HealthLimit <= 0) return;
                        float hpRatio = victim.Health / victim.HealthLimit;
                        if (hpRatio >= threshold) return;
                        float scaleRatio = 1f - (hpRatio / threshold);
                        blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * (1f - (damageReductionPercent / 100f) * scaleRatio));
                    };
                }

                void CleanupScaling()
                {
                    if (active && ShowContour) try { SafeSetContourColor(trackedAgent, null, false); } catch { }
                    active = false;
                }
                if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => CleanupScaling();
                handlers.OnMissionOver += CleanupScaling;
                return;
            }

            // HpThreshold: Last Stand-style. Extracted pattern from LastStandPower.OnActivation
            // above - fires once HP would drop below the threshold, flat bonus/reduction for a
            // fixed window, optionally once per battle.
            {
                float triggerHpPercent = ThresholdHpPercent;
                float durationSeconds = DurationSeconds;
                Agent trackedAgent = null;
                bool triggered = false;
                float expiryTime = 0f;

                void CheckReset()
                {
                    var cur = hero.GetAgent();
                    if (cur != null && cur != trackedAgent)
                    {
                        if (triggered && ShowContour) try { SafeSetContourColor(trackedAgent, null, false); } catch { }
                        trackedAgent = cur;
                        triggered = false;
                        expiryTime = 0f;
                    }
                }

                handlers.OnTakeDamage += (victim, attacker, blowParams) =>
                {
                    CheckReset();
                    if (triggered && OncePerBattle) return;
                    if (victim.HealthLimit <= 0) return;
                    float newHp = victim.Health - blowParams.blow.InflictedDamage;
                    if (newHp > victim.HealthLimit * triggerHpPercent / 100f) return;
                    triggered = true;
                    expiryTime = (Mission.Current?.CurrentTime ?? 0f) + durationSeconds;
                    if (ShowContour)
                        try { SafeSetContourColor(victim, color, true); } catch { }
                };

                handlers.OnDoDamage += (attacker, victim, blowParams) =>
                {
                    if (!triggered) return;
                    float now = Mission.Current?.CurrentTime ?? 0f;
                    if (now > expiryTime)
                    {
                        if (ShowContour) try { SafeSetContourColor(attacker, null, false); } catch { }
                        return;
                    }
                    if (damageBonusPercent != 0f)
                        blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * (1f + damageBonusPercent / 100f));
                };

                handlers.OnTakeDamage += (victim, attacker, blowParams) =>
                {
                    if (!triggered) return;
                    if ((Mission.Current?.CurrentTime ?? 0f) > expiryTime) return;
                    if (damageReductionPercent != 0f)
                        blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * (1f - damageReductionPercent / 100f));
                };

                void CleanupThreshold()
                {
                    if (triggered && ShowContour) try { SafeSetContourColor(trackedAgent, null, false); } catch { }
                    triggered = false;
                }
                if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => CleanupThreshold();
                handlers.OnMissionOver += CleanupThreshold;
            }
        }

        public override LocString Description =>
            $"Composed Self-Buff (Beta): +{DamageBonusPercent:0}% dmg, -{DamageReductionPercent:0}% taken";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 1. POISON STRIKE
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    public enum PoisonApplyOn { Both, RangedOnly, MeleeOnly }

    [DisplayName("Poison Strike"),
     Description("Each hit poisons the target, dealing damage over time every 2 seconds"),
     UsedImplicitly]
    public class PoisonStrikePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Poison Damage Per Tick"), Description("Damage dealt each tick while poisoned."), UsedImplicitly]
        public int PoisonDamagePerTick { get; set; } = 10;

        [DisplayName("Poison Duration (ticks)"), Description("How many ticks the poison effect lasts."), UsedImplicitly]
        public int PoisonDurationTicks { get; set; } = 5;

        [DisplayName("Apply On"), Description("Which kind of attacks this effect applies to (melee, ranged, or both)."), UsedImplicitly]
        public PoisonApplyOn ApplyOn { get; set; } = PoisonApplyOn.Both;

        [DisplayName("Show Poison Contour"), Description("Highlights poisoned agents with a colored outline - visual only."), UsedImplicitly]
        public bool ShowPoisonContour { get; set; } = true;

        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly]
        public string PoisonContourColor { get; set; } = "FF00CC00";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            int poisonDamagePerTick = PowerProgression.ScaleInt(this, hero, nameof(PoisonDamagePerTick), PoisonDamagePerTick);
            int poisonDurationTicks = PowerProgression.ScaleInt(this, hero, nameof(PoisonDurationTicks), PoisonDurationTicks);

            var poisonedAgents = new Dictionary<Agent, int>();

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (victim == null || !victim.IsActive()) return;
                if (attacker == null || !victim.IsEnemyOf(attacker)) return;
                if (ApplyOn != PoisonApplyOn.Both)
                {
                    bool isRanged = attacker != null
                        && !attacker.WieldedWeapon.IsEmpty
                        && attacker.WieldedWeapon.CurrentUsageItem?.IsRangedWeapon == true;
                    if (ApplyOn == PoisonApplyOn.RangedOnly && !isRanged) return;
                    if (ApplyOn == PoisonApplyOn.MeleeOnly && isRanged) return;
                }
                poisonedAgents[victim] = poisonDurationTicks;
                if (ShowPoisonContour)
                    try { SafeSetContourColor(victim, Convert.ToUInt32(PoisonContourColor, 16), true); }
                    catch { SafeSetContourColor(victim, 0xFF00CC00u, true); }
            };

            handlers.OnSlowTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;
                foreach (var key in poisonedAgents.Keys.ToList())
                {
                    if (key == null || !key.IsActive()) { poisonedAgents.Remove(key); continue; }
                    try
                    {
                        var dir = Vec3.Up;
                        var blow = new Blow(heroAgent.Index)
                        {
                            AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Pierce,
                            BoneIndex = key.Monster.HeadLookDirectionBoneIndex, GlobalPosition = key.Position,
                            BaseMagnitude = poisonDamagePerTick, InflictedDamage = poisonDamagePerTick,
                            SwingDirection = dir, Direction = dir, DamageCalculated = true,
                            VictimBodyPart = BoneBodyPartType.Chest,
                        };
                        key.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, key, blow));
                    }
                    catch { }
                    poisonedAgents[key]--;
                    if (poisonedAgents[key] <= 0)
                    {
                        if (ShowPoisonContour) try { SafeSetContourColor(key, null, false); } catch { }
                        poisonedAgents.Remove(key);
                    }
                }
            };

            void Cleanup()
            {
                if (ShowPoisonContour)
                    foreach (var a in poisonedAgents.Keys) try { SafeSetContourColor(a, null, false); } catch { }
                poisonedAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Poison: {PoisonDamagePerTick} dmg/2s for {PoisonDurationTicks * 2}s";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 2. BERSERK
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Berserk"),
     Description("The lower the hero's HP, the more damage they deal"),
     UsedImplicitly]
    public class BerserkPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Max Damage Bonus (%)"), Description("Highest possible damage bonus this effect can reach."), UsedImplicitly]
        public float MaxDamageBonusPercent { get; set; } = 75f;

        [DisplayName("Threshold HP (%)"), Description("Triggers once HP drops to or below this percent of max."), UsedImplicitly]
        public float ThresholdHpPercent { get; set; } = 80f;

        [DisplayName("Show Berserk Contour"), Description("Highlights the hero with a colored outline while Berserk is active - visual only."), UsedImplicitly]
        public bool ShowContour { get; set; } = true;

        [DisplayName("Berserk Contour Color (hex AARRGGBB)"), Description("Outline color shown while Berserk is active, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue."), UsedImplicitly]
        public string ContourColor { get; set; } = "FF8B0000";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float maxDamageBonusPercent = PowerProgression.ScaleFloat(this, hero, nameof(MaxDamageBonusPercent), MaxDamageBonusPercent);
            float thresholdHpPercent    = PowerProgression.ScaleFloat(this, hero, nameof(ThresholdHpPercent), ThresholdHpPercent);

            Agent trackedAgent = null;
            bool berserkActive = false;

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (attacker == null || attacker.HealthLimit <= 0) return;
                var current = hero.GetAgent();
                if (current != null && current != trackedAgent)
                {
                    if (berserkActive && ShowContour)
                        try { SafeSetContourColor(trackedAgent, null, false); } catch { }
                    trackedAgent = current;
                    berserkActive = false;
                }

                float hpRatio = attacker.Health / attacker.HealthLimit;
                float threshold = thresholdHpPercent / 100f;
                if (hpRatio >= threshold)
                {
                    if (berserkActive && ShowContour)
                        try { SafeSetContourColor(attacker, null, false); } catch { }
                    berserkActive = false;
                    return;
                }

                if (!berserkActive)
                {
                    berserkActive = true;
                    Log.ShowInformation($"BERSERK! {hero.Name} rages, up to +{maxDamageBonusPercent:0}% DMG!", hero.CharacterObject);
                    if (ShowContour)
                        try
                        {
                            uint color = Convert.ToUInt32(ContourColor, 16);
                            SafeSetContourColor(attacker, color, true);
                        }
                        catch { SafeSetContourColor(attacker, 0xFF8B0000u, true); }
                }
                float berserkRatio = 1f - (hpRatio / threshold);
                float multiplier = 1f + (maxDamageBonusPercent / 100f) * berserkRatio;
                blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * multiplier);
            };

            void Cleanup()
            {
                if (berserkActive && ShowContour)
                    try { SafeSetContourColor(trackedAgent, null, false); } catch { }
                berserkActive = false;
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Berserk: up to +{MaxDamageBonusPercent:0}% DMG below {ThresholdHpPercent:0}% HP";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 3. LAST STAND
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Last Stand"),
     Description("When HP drops below threshold, triggers a temporary massive buff"),
     UsedImplicitly]
    public class LastStandPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("HP Trigger Threshold (%)"), Description("Triggers once the hero's HP drops to or below this percent of max."), UsedImplicitly]
        public float TriggerHpPercent { get; set; } = 20f;

        [DisplayName("Damage Bonus (%)"), Description("Percentage bonus added to damage dealt while this effect is active."), UsedImplicitly]
        public float DamageBonusPercent { get; set; } = 100f;

        [DisplayName("Damage Reduction (%)"), Description("Percentage reduction applied to incoming damage while this effect is active."), UsedImplicitly]
        public float DamageReductionPercent { get; set; } = 50f;

        [DisplayName("Duration (seconds)"), Description("How long (in seconds) this effect lasts once triggered."), UsedImplicitly]
        public float DurationSeconds { get; set; } = 10f;

        [DisplayName("Once Per Battle"), Description("Whether this effect can only trigger once per battle."), UsedImplicitly]
        public bool OncePerBattle { get; set; } = true;

        [DisplayName("Show Last Stand Contour"), Description("Highlights the hero with a colored outline while Last Stand is active - visual only."), UsedImplicitly]
        public bool ShowContour { get; set; } = true;

        [DisplayName("Last Stand Contour Color (hex AARRGGBB)"), Description("Outline color shown while Last Stand is active, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue."), UsedImplicitly]
        public string ContourColor { get; set; } = "FFFFFFFF";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float triggerHpPercent       = PowerProgression.ScaleFloat(this, hero, nameof(TriggerHpPercent), TriggerHpPercent);
            float damageBonusPercent     = PowerProgression.ScaleFloat(this, hero, nameof(DamageBonusPercent), DamageBonusPercent);
            float damageReductionPercent = PowerProgression.ScaleFloat(this, hero, nameof(DamageReductionPercent), DamageReductionPercent);
            float durationSeconds        = PowerProgression.ScaleFloat(this, hero, nameof(DurationSeconds), DurationSeconds);

            Agent trackedAgent = null;
            bool triggered = false;
            float expiryTime = 0f;

            void CheckReset()
            {
                var cur = hero.GetAgent();
                if (cur != null && cur != trackedAgent)
                {
                    if (triggered && ShowContour)
                        try { SafeSetContourColor(trackedAgent, null, false); } catch { }
                    trackedAgent = cur;
                    triggered = false;
                    expiryTime = 0f;
                }
            }

            handlers.OnTakeDamage += (victim, attacker, blowParams) =>
            {
                CheckReset();
                if (triggered && OncePerBattle) return;
                if (victim.HealthLimit <= 0) return;
                float newHp = victim.Health - blowParams.blow.InflictedDamage;
                if (newHp > victim.HealthLimit * triggerHpPercent / 100f) return;
                triggered = true;
                expiryTime = (Mission.Current?.CurrentTime ?? 0f) + durationSeconds;
                Log.ShowInformation($"LAST STAND! {hero.Name} fights to the death!", hero.CharacterObject);
                if (ShowContour)
                    try
                    {
                        uint color = Convert.ToUInt32(ContourColor, 16);
                        SafeSetContourColor(victim, color, true);
                    }
                    catch { SafeSetContourColor(victim, 0xFFFFFFFFu, true); }
            };

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (!triggered) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now > expiryTime)
                {
                    if (ShowContour) try { SafeSetContourColor(attacker, null, false); } catch { }
                    return;
                }
                blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * (1f + damageBonusPercent / 100f));
            };

            handlers.OnTakeDamage += (victim, attacker, blowParams) =>
            {
                if (!triggered) return;
                if ((Mission.Current?.CurrentTime ?? 0f) > expiryTime) return;
                blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * (1f - damageReductionPercent / 100f));
            };

            void Cleanup()
            {
                if (triggered && ShowContour)
                    try { SafeSetContourColor(trackedAgent, null, false); } catch { }
                triggered = false;
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Last Stand: below {TriggerHpPercent:0}% HP -> +{DamageBonusPercent:0}% DMG, -{DamageReductionPercent:0}% DMG taken for {DurationSeconds:0}s";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 4. TAUNT
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Taunt"),
     Description("Enemies within range are forced to target this hero"),
     UsedImplicitly]
    public class TauntPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Taunt Range (meters)"), Description("How far (in meters) enemies can be forced to target this hero."), UsedImplicitly]
        public float TauntRange { get; set; } = 8f;

        [DisplayName("Max Enemies To Taunt"), Description("Maximum number of enemies that can be forced to target this hero at once."), UsedImplicitly]
        public int MaxEnemies { get; set; } = 10;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float tauntRange = PowerProgression.ScaleFloat(this, hero, nameof(TauntRange), TauntRange);
            int   maxEnemies = PowerProgression.ScaleInt(this, hero, nameof(MaxEnemies), MaxEnemies);

            var nearbyBuffer = new MBList<Agent>();

            handlers.OnSlowTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                nearbyBuffer.Clear();
                int taunted = 0;
                foreach (var enemy in Mission.Current
                    .GetNearbyAgents(heroAgent.Position.AsVec2, tauntRange, nearbyBuffer)
                    .Where(a => a.IsActive() && a.IsEnemyOf(heroAgent) && !a.IsMount && a.GetAdoptedHero() == null))
                {
                    if (taunted >= maxEnemies) break;
                    try { enemy.SetTargetAgent(heroAgent); } catch { }
                    taunted++;
                }
            };
        }

        public override LocString Description =>
            $"Taunt: forces up to {MaxEnemies} enemies within {TauntRange:0}m to target this hero";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 5. HEAL AURA
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Heal Aura"),
     Description("Heals all friendly units within range every 2 seconds"),
     UsedImplicitly]
    public class HealAuraPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Heal Per Tick"), Description("HP restored each tick."), UsedImplicitly]
        public float HealPerTick { get; set; } = 5f;

        [DisplayName("Heal Range (meters)"), Description("How far from the hero (in meters) allies are healed."), UsedImplicitly]
        public float HealRange { get; set; } = 10f;

        [DisplayName("Heal Self"), Description("Whether the hero itself is included when healing nearby allies."), UsedImplicitly]
        public bool HealSelf { get; set; } = true;

        [DisplayName("Show Heal Contour"), Description("Highlights healed agents with a colored outline - visual only."), UsedImplicitly]
        public bool ShowHealContour { get; set; } = true;

        [DisplayName("Heal Contour Color (hex AARRGGBB)"), Description("Outline color shown on healed agents, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue."), UsedImplicitly]
        public string HealContourColor { get; set; } = "FF00FF44";

        [DisplayName("Tick Interval (seconds)"), Description("How often (in seconds) this effect re-evaluates and re-applies to nearby agents."), UsedImplicitly]
        public float TickIntervalSeconds { get; set; } = 3f;

        [DisplayName("Max Agents Per Tick"), Description("Caps how many nearby agents this effect can hit per tick, closest first - keeps performance in check in large battles."), UsedImplicitly]
        public int MaxAgentsPerTick { get; set; } = 8;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float healPerTick = PowerProgression.ScaleFloat(this, hero, nameof(HealPerTick), HealPerTick);
            float healRange   = PowerProgression.ScaleFloat(this, hero, nameof(HealRange), HealRange);
            float tickInterval = PowerProgression.ScaleFloat(this, hero, nameof(TickIntervalSeconds), TickIntervalSeconds);
            int   maxAgents    = PowerProgression.ScaleInt(this, hero, nameof(MaxAgentsPerTick), MaxAgentsPerTick);
            float lastTick     = -999f;

            var nearbyBuffer = new MBList<Agent>();
            var lastInRange = new HashSet<Agent>();

            handlers.OnSlowTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive())
                {
                    foreach (var a in lastInRange) try { SafeSetContourColor(a, null, false); } catch { }
                    lastInRange.Clear();
                    return;
                }
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastTick < tickInterval) return;
                lastTick = now;
                nearbyBuffer.Clear();
                var nowInRange = new HashSet<Agent>(Mission.Current
                    .GetNearbyAgents(heroAgent.Position.AsVec2, healRange, nearbyBuffer)
                    .Where(a => a.IsActive() && !a.IsMount && a.IsFriendOf(heroAgent))
                    .OrderBy(a => a.Position.Distance(heroAgent.Position))
                    .Take(Math.Max(1, maxAgents)));

                foreach (var a in lastInRange)
                    if (!nowInRange.Contains(a)) try { SafeSetContourColor(a, null, false); } catch { }

                if (ShowHealContour)
                {
                    uint color = Convert.ToUInt32(HealContourColor, 16);
                    foreach (var a in nowInRange) try { SafeSetContourColor(a, color, true); } catch { }
                }
                foreach (var ally in nowInRange)
                {
                    if (!HealSelf && ally == heroAgent) continue;
                    ally.Health = Math.Min(ally.Health + healPerTick, ally.HealthLimit);
                }
                lastInRange.Clear();
                foreach (var a in nowInRange) lastInRange.Add(a);
            };

            void Cleanup()
            {
                foreach (var a in lastInRange) try { SafeSetContourColor(a, null, false); } catch { }
                lastInRange.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Heal Aura: +{HealPerTick:0} HP/2s to all allies within {HealRange:0}m";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 6. DAMAGE AURA
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Damage Aura"),
     Description("Deals damage to all enemies within range every 2 seconds"),
     UsedImplicitly]
    public class DamageAuraPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Damage Per Tick"), Description("Damage dealt on each tick of this effect."), UsedImplicitly]
        public int DamagePerTick { get; set; } = 8;

        [DisplayName("Damage Range (meters)"), Description("How far from the hero (in meters) this damage effect reaches."), UsedImplicitly]
        public float DamageRange { get; set; } = 8f;

        [DisplayName("Show Damage Contour"), Description("Highlights damaged agents with a colored outline - visual only."), UsedImplicitly]
        public bool ShowDamageContour { get; set; } = true;

        [DisplayName("Damage Contour Color (hex AARRGGBB)"), Description("Outline color shown on damaged agents, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue."), UsedImplicitly]
        public string DamageContourColor { get; set; } = "FFFF2200";

        [DisplayName("Max Agents Per Tick"), Description("Caps how many nearby agents this effect can hit per tick, closest first - keeps performance in check in large battles."), UsedImplicitly]
        public int MaxAgentsPerTick { get; set; } = 5;

        [DisplayName("Tick Interval (seconds)"), Description("How often (in seconds) this effect re-evaluates and re-applies to nearby agents."), UsedImplicitly]
        public float TickIntervalSeconds { get; set; } = 3f;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            int   damagePerTick = PowerProgression.ScaleInt(this, hero, nameof(DamagePerTick), DamagePerTick);
            float damageRange   = PowerProgression.ScaleFloat(this, hero, nameof(DamageRange), DamageRange);
            int   maxAgents     = PowerProgression.ScaleInt(this, hero, nameof(MaxAgentsPerTick), MaxAgentsPerTick);
            float tickInterval  = PowerProgression.ScaleFloat(this, hero, nameof(TickIntervalSeconds), TickIntervalSeconds);
            float lastTick      = -999f;

            var nearbyBuffer = new MBList<Agent>();
            var lastInRange = new HashSet<Agent>();

            handlers.OnSlowTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive())
                {
                    foreach (var a in lastInRange) try { SafeSetContourColor(a, null, false); } catch { }
                    lastInRange.Clear();
                    return;
                }
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastTick < tickInterval) return;
                lastTick = now;
                nearbyBuffer.Clear();
                // Limit to the nearest N enemies per tick (Max Agents Per Tick)
                var nowInRange = new HashSet<Agent>(Mission.Current
                    .GetNearbyAgents(heroAgent.Position.AsVec2, damageRange, nearbyBuffer)
                    .Where(a => a.IsActive() && !a.IsMount && a.IsEnemyOf(heroAgent))
                    .OrderBy(a => a.Position.Distance(heroAgent.Position))
                    .Take(Math.Max(1, maxAgents)));

                foreach (var a in lastInRange)
                    if (!nowInRange.Contains(a)) try { SafeSetContourColor(a, null, false); } catch { }

                if (ShowDamageContour)
                {
                    uint color = Convert.ToUInt32(DamageContourColor, 16);
                    foreach (var a in nowInRange) try { SafeSetContourColor(a, color, true); } catch { }
                }
                foreach (var enemy in nowInRange)
                {
                    try
                    {
                        var dir = Vec3.Up;
                        var blow = new Blow(heroAgent.Index)
                        {
                            AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Pierce,
                            BoneIndex = enemy.Monster.HeadLookDirectionBoneIndex, GlobalPosition = enemy.Position,
                            BaseMagnitude = damagePerTick, InflictedDamage = damagePerTick,
                            SwingDirection = dir, Direction = dir, DamageCalculated = true,
                            VictimBodyPart = BoneBodyPartType.Chest,
                        };
                        enemy.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, enemy, blow));
                    }
                    catch { }
                }
                lastInRange.Clear();
                foreach (var a in nowInRange) lastInRange.Add(a);
            };

            void Cleanup()
            {
                foreach (var a in lastInRange) try { SafeSetContourColor(a, null, false); } catch { }
                lastInRange.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Damage Aura: {DamagePerTick} dmg/2s to all enemies within {DamageRange:0}m";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 7. COMMANDER AURA (Reward)
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Commander Aura"),
     Description("Channel points reward: buffs all allied units near the hero for the rest of the battle"),
     UsedImplicitly]
    public class CommanderAuraReward : IRewardHandler
    {
        public Type RewardConfigType => typeof(CommanderAuraSettings);

        public class CommanderAuraSettings
        {
            [DisplayName("Damage Bonus (%)"), Description("Percentage bonus added to damage dealt while this effect is active."), UsedImplicitly] public float DamageBonusPercent { get; set; } = 25f;
            [DisplayName("Armor Bonus (%)"), Description("Percentage bonus added to armor while this effect is active."), UsedImplicitly] public float ArmorBonusPercent { get; set; } = 20f;
            [DisplayName("Speed Bonus (%)"), Description("Percentage bonus added to movement speed."), UsedImplicitly] public float SpeedBonusPercent { get; set; } = 15f;
            [DisplayName("Aura Range (meters)"), Description("How far from the hero (in meters) this aura reaches."), UsedImplicitly] public float Range { get; set; } = 20f;
            [DisplayName("Duration (seconds)"), Description("How long (in seconds) this effect lasts once triggered."), UsedImplicitly] public float DurationSeconds { get; set; } = 60f;
            [DisplayName("Gold Cost"), Description("BLT gold required to use this. Set to 0 to make it free."), UsedImplicitly] public int GoldCost { get; set; } = 0;
        }

        public void Enqueue(ReplyContext context, object config)
        {
            var settings = config as CommanderAuraSettings ?? new CommanderAuraSettings();
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (hero == null) { ActionManager.SendReply(context, "You don't have an adopted hero."); return; }

            var heroAgent = hero.GetAgent();
            if (heroAgent == null || !heroAgent.IsActive()) { ActionManager.SendReply(context, "Your hero is not in an active battle."); return; }

            if (settings.GoldCost > 0)
            {
                int gold = BLTAdoptAHeroCampaignBehavior.Current?.GetHeroGold(hero) ?? 0;
                if (gold < settings.GoldCost) { ActionManager.SendReply(context, $"Not enough BLT gold (need {settings.GoldCost}, have {gold})."); return; }
                BLTAdoptAHeroCampaignBehavior.Current?.ChangeHeroGold(hero, -settings.GoldCost, true);
            }

            var nearbyBuffer = new MBList<Agent>();
            var allies = Mission.Current.GetNearbyAgents(heroAgent.Position.AsVec2, settings.Range, nearbyBuffer)
                .Where(a => a.IsActive() && !a.IsMount && a.IsFriendOf(heroAgent)).ToList();

            if (allies.Count == 0) { ActionManager.SendReply(context, "No allies in range to buff."); return; }

            var modifierConfig = BuildModifierConfig(settings);
            foreach (var ally in allies)
            {
                try
                {
                    if (settings.DurationSeconds > 0f)
                        BLTTimedBuffBehavior.AddTimedBuff(ally, modifierConfig, settings.DurationSeconds);
                    else
                        BLTAgentModifierBehavior.Current?.Add(ally, modifierConfig);
                }
                catch { }
            }

            string durationStr = settings.DurationSeconds > 0f ? $" for {settings.DurationSeconds:0}s" : " for the rest of the battle";
            Log.ShowInformation($"Commander Aura! {allies.Count} allies buffed{durationStr}!", hero.CharacterObject);
            ActionManager.SendReply(context, $"Commander Aura! {allies.Count} allies: +{settings.DamageBonusPercent:0}% DMG, +{settings.ArmorBonusPercent:0}% armor, +{settings.SpeedBonusPercent:0}% speed{durationStr}.");
        }

        private static AgentModifierConfig BuildModifierConfig(CommanderAuraSettings s)
        {
            var config = new AgentModifierConfig();
            if (s.DamageBonusPercent != 0)
                config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.SwingSpeedMultiplier, ModifierPercent = 100f + s.DamageBonusPercent });
            if (s.ArmorBonusPercent != 0)
                config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ArmorHead, ModifierPercent = 100f + s.ArmorBonusPercent });
            if (s.SpeedBonusPercent != 0)
                config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.MaxSpeedMultiplier, ModifierPercent = 100f + s.SpeedBonusPercent });
            return config;
        }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 8. CURSE AURA
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Curse Aura"),
     Description("Passive aura: slows nearby enemies, dismounts riders, and can knock them down"),
     UsedImplicitly]
    public class CurseAuraPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Aura Range (m)"), Description("How far from the hero (in meters) this aura reaches."), UsedImplicitly] public float AuraRange { get; set; } = 8f;
        [DisplayName("Speed Slow (%)"), Description("Percentage reduction to movement speed."), UsedImplicitly] public float SpeedSlowPercent { get; set; } = 40f;
        [DisplayName("Attack Speed Slow (%)"), Description("Percentage reduction to attack/swing speed."), UsedImplicitly] public float AttackSlowPercent { get; set; } = 30f;
        [DisplayName("Tick Interval (seconds)"), Description("How often (in seconds) this effect re-evaluates and re-applies to nearby agents."), UsedImplicitly] public float TickInterval { get; set; } = 3f;
        [DisplayName("Max Agents Per Tick"), Description("Caps how many nearby agents this effect can hit per tick, closest first - keeps performance in check in large battles."), UsedImplicitly] public int MaxAgentsPerTick { get; set; } = 5;
        [DisplayName("Dismount Enabled"), Description("Whether mounted targets can be knocked off their horse by this effect."), UsedImplicitly] public bool DismountEnabled { get; set; } = true;
        [DisplayName("Dismount Chance Per Tick (%)"), Description("Chance per tick that a mounted target is dismounted."), UsedImplicitly] public float DismountChancePercent { get; set; } = 20f;
        [DisplayName("Knockdown Enabled"), Description("Whether affected agents can be knocked down by this effect."), UsedImplicitly] public bool KnockdownEnabled { get; set; } = true;
        [DisplayName("Knockdown Chance Per Tick (%)"), Description("Chance per tick that an affected agent is knocked down."), UsedImplicitly] public float KnockdownChancePercent { get; set; } = 10f;
        [DisplayName("Weapon Drop Enabled"), Description("Whether affected agents can be made to drop their weapon."), UsedImplicitly] public bool WeaponDropEnabled { get; set; } = true;
        [DisplayName("Weapon Drop Chance Per Tick (%)"), Description("Chance per tick that an affected agent drops their weapon."), UsedImplicitly] public float WeaponDropChancePercent { get; set; } = 5f;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FF8800FF";
        [DisplayName("Show Message"), Description("Whether to post a chat/feed message when this effect triggers."), UsedImplicitly] public bool ShowMessage { get; set; } = true;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float auraRange              = PowerProgression.ScaleFloat(this, hero, nameof(AuraRange), AuraRange);
            float speedSlowPercent       = PowerProgression.ScaleFloat(this, hero, nameof(SpeedSlowPercent), SpeedSlowPercent);
            float attackSlowPercent      = PowerProgression.ScaleFloat(this, hero, nameof(AttackSlowPercent), AttackSlowPercent);
            float dismountChancePercent  = PowerProgression.ScaleFloat(this, hero, nameof(DismountChancePercent), DismountChancePercent);
            float knockdownChancePercent = PowerProgression.ScaleFloat(this, hero, nameof(KnockdownChancePercent), KnockdownChancePercent);
            float weaponDropChancePercent= PowerProgression.ScaleFloat(this, hero, nameof(WeaponDropChancePercent), WeaponDropChancePercent);

            int maxAgents = PowerProgression.ScaleInt(this, hero, nameof(MaxAgentsPerTick), MaxAgentsPerTick);
            var nearbyBuffer = new MBList<Agent>();
            var cursedAgents = new HashSet<Agent>();
            float lastTick = 0f;

            handlers.OnSlowTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive())
                {
                    RestoreAll(cursedAgents);
                    cursedAgents.Clear();
                    return;
                }
                nearbyBuffer.Clear();
                var nowInRange = new HashSet<Agent>(Mission.Current
                    .GetNearbyAgents(heroAgent.Position.AsVec2, auraRange, nearbyBuffer)
                    .Where(a => a.IsActive() && !a.IsMount && a.IsEnemyOf(heroAgent))
                    .OrderBy(a => a.Position.Distance(heroAgent.Position))
                    .Take(Math.Max(1, maxAgents)));

                foreach (var a in cursedAgents)
                {
                    if (!nowInRange.Contains(a))
                    {
                        RestoreAgent(a);
                        if (ShowContour) try { SafeSetContourColor(a, null, false); } catch { }
                    }
                }
                if (speedSlowPercent > 0f || attackSlowPercent > 0f)
                    foreach (var a in nowInRange)
                        if (!cursedAgents.Contains(a)) ApplyCurse(a, speedSlowPercent, attackSlowPercent);

                if (ShowContour)
                {
                    uint color = Convert.ToUInt32(ContourColor, 16);
                    foreach (var a in nowInRange) try { SafeSetContourColor(a, color, true); } catch { }
                }
                cursedAgents.Clear();
                foreach (var a in nowInRange) cursedAgents.Add(a);
            };

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastTick < TickInterval) return;
                lastTick = now;

                foreach (var enemy in cursedAgents.ToList())
                {
                    if (enemy == null || !enemy.IsActive()) continue;
                    if (DismountEnabled && enemy.MountAgent != null && enemy.MountAgent.IsActive() &&
                        MBRandom.RandomFloat * 100f < dismountChancePercent)
                    {
                        try
                        {
                            var mount = enemy.MountAgent;
                            var dir = Vec3.Up;
                            var blow = new Blow(heroAgent.Index)
                            {
                                AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Blunt,
                                BoneIndex = mount.Monster.HeadLookDirectionBoneIndex, GlobalPosition = mount.Position,
                                BaseMagnitude = mount.HealthLimit, InflictedDamage = (int)mount.HealthLimit,
                                SwingDirection = dir, Direction = dir, DamageCalculated = true,
                                VictimBodyPart = BoneBodyPartType.Chest,
                            };
                            mount.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, mount, blow));
                        }
                        catch { }
                    }
                    if (KnockdownEnabled && !enemy.HasMount && MBRandom.RandomFloat * 100f < knockdownChancePercent)
                    {
                        try
                        {
                            var fallAction = ActionIndexCache.Create("act_strike_fall_back_heavy_back_rise");
                            enemy.SetActionChannel(0, fallAction, ignorePriority: true, 0UL);
                        }
                        catch { }
                    }
                    if (WeaponDropEnabled && MBRandom.RandomFloat * 100f < weaponDropChancePercent)
                    {
                        try
                        {
                            for (var wi = EquipmentIndex.WeaponItemBeginSlot; wi < EquipmentIndex.NumAllWeaponSlots; wi++)
                            {
                                if (!enemy.Equipment[wi].IsEmpty)
                                {
                                    enemy.DropItem(wi);
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                }
            };

            if (ShowMessage)
            {
                bool announced = false;
                handlers.OnSlowTick += dt =>
                {
                    if (announced) return;
                    var heroAgent = hero.GetAgent();
                    if (heroAgent != null && heroAgent.IsActive() && cursedAgents.Count > 0)
                    {
                        announced = true;
                        Log.ShowInformation($"CURSE AURA! {hero.Name} weakens nearby foes!", hero.CharacterObject);
                    }
                };
            }

            void Cleanup() { RestoreAll(cursedAgents); cursedAgents.Clear(); }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        private void ApplyCurse(Agent agent, float speedSlowPercent, float attackSlowPercent)
        {
            try
            {
                if (speedSlowPercent > 0f) agent.SetMaximumSpeedLimit(1f - speedSlowPercent / 100f, true);
                if (attackSlowPercent > 0f)
                {
                    var config = new AgentModifierConfig();
                    config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.SwingSpeedMultiplier, ModifierPercent = 100f - attackSlowPercent });
                    BLTAgentModifierBehavior.Current?.Add(agent, config);
                }
            }
            catch { }
        }

        private static void RestoreAgent(Agent agent)
        {
            if (agent == null || !agent.IsActive()) return;
            try { agent.SetMaximumSpeedLimit(1f, true); } catch { }
        }

        private static void RestoreAll(HashSet<Agent> agents)
        {
            foreach (var a in agents) RestoreAgent(a);
        }

        public override LocString Description =>
            $"Curse Aura: slows enemies {SpeedSlowPercent:0}% move/{AttackSlowPercent:0}% attack in {AuraRange:0}m";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 9. BUFF AURA
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Buff Aura"),
     Description("Passive aura: buffs all friendly units in range"),
     UsedImplicitly]
    public class BuffAuraPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Aura Range (m)"), Description("How far from the hero (in meters) this aura reaches."), UsedImplicitly] public float AuraRange { get; set; } = 10f;
        [DisplayName("Damage Bonus (%)"), Description("Percentage bonus added to damage dealt while this effect is active."), UsedImplicitly] public float DamageBonusPercent { get; set; } = 20f;
        [DisplayName("Armor Bonus (%)"), Description("Percentage bonus added to armor while this effect is active."), UsedImplicitly] public float ArmorBonusPercent { get; set; } = 20f;
        [DisplayName("Move Speed Bonus (%)"), Description("Percentage bonus added to movement speed."), UsedImplicitly] public float MoveSpeedBonusPercent { get; set; } = 15f;
        [DisplayName("Attack Speed Bonus (%)"), Description("Percentage bonus added to attack/swing speed."), UsedImplicitly] public float AttackSpeedBonusPercent { get; set; } = 15f;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FFFFD700";
        [DisplayName("Tick Interval (seconds)"), Description("How often (in seconds) this effect re-evaluates and re-applies to nearby agents."), UsedImplicitly] public float TickIntervalSeconds { get; set; } = 3f;
        [DisplayName("Max Agents Per Tick"), Description("Caps how many nearby agents this effect can hit per tick, closest first - keeps performance in check in large battles."), UsedImplicitly] public int MaxAgentsPerTick { get; set; } = 8;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float tickInterval = PowerProgression.ScaleFloat(this, hero, nameof(TickIntervalSeconds), TickIntervalSeconds);
            int   maxAgents    = PowerProgression.ScaleInt(this, hero, nameof(MaxAgentsPerTick), MaxAgentsPerTick);
            float lastTick     = -999f;
            float auraRange               = PowerProgression.ScaleFloat(this, hero, nameof(AuraRange), AuraRange);
            float damageBonusPercent      = PowerProgression.ScaleFloat(this, hero, nameof(DamageBonusPercent), DamageBonusPercent);
            float armorBonusPercent       = PowerProgression.ScaleFloat(this, hero, nameof(ArmorBonusPercent), ArmorBonusPercent);
            float moveSpeedBonusPercent   = PowerProgression.ScaleFloat(this, hero, nameof(MoveSpeedBonusPercent), MoveSpeedBonusPercent);
            float attackSpeedBonusPercent = PowerProgression.ScaleFloat(this, hero, nameof(AttackSpeedBonusPercent), AttackSpeedBonusPercent);

            var nearbyBuffer = new MBList<Agent>();
            var buffedAgents = new HashSet<Agent>();

            handlers.OnSlowTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive())
                {
                    foreach (var a in buffedAgents) { RemoveBuff(a, damageBonusPercent, armorBonusPercent, moveSpeedBonusPercent, attackSpeedBonusPercent); if (ShowContour) try { SafeSetContourColor(a, null, false); } catch { } }
                    buffedAgents.Clear();
                    return;
                }
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastTick < tickInterval) return;
                lastTick = now;
                nearbyBuffer.Clear();
                var nowInRange = new HashSet<Agent>(Mission.Current
                    .GetNearbyAgents(heroAgent.Position.AsVec2, auraRange, nearbyBuffer)
                    .Where(a => a.IsActive() && !a.IsMount && a.IsFriendOf(heroAgent))
                    .OrderBy(a => a.Position.Distance(heroAgent.Position))
                    .Take(Math.Max(1, maxAgents)));

                foreach (var a in buffedAgents)
                    if (!nowInRange.Contains(a)) { RemoveBuff(a, damageBonusPercent, armorBonusPercent, moveSpeedBonusPercent, attackSpeedBonusPercent); if (ShowContour) try { SafeSetContourColor(a, null, false); } catch { } }

                foreach (var a in nowInRange)
                    if (!buffedAgents.Contains(a)) ApplyBuff(a, damageBonusPercent, armorBonusPercent, moveSpeedBonusPercent, attackSpeedBonusPercent);

                if (ShowContour)
                {
                    uint color = Convert.ToUInt32(ContourColor, 16);
                    foreach (var a in nowInRange) try { SafeSetContourColor(a, color, true); } catch { }
                }
                buffedAgents.Clear();
                foreach (var a in nowInRange) buffedAgents.Add(a);
            };

            void Cleanup()
            {
                foreach (var a in buffedAgents) { RemoveBuff(a, damageBonusPercent, armorBonusPercent, moveSpeedBonusPercent, attackSpeedBonusPercent); if (ShowContour) try { SafeSetContourColor(a, null, false); } catch { } }
                buffedAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        private void ApplyBuff(Agent a, float damageBonusPercent, float armorBonusPercent, float moveSpeedBonusPercent, float attackSpeedBonusPercent)
        {
            try
            {
                var config = new AgentModifierConfig();
                if (damageBonusPercent > 0f) config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.SwingSpeedMultiplier, ModifierPercent = 100f + damageBonusPercent });
                if (armorBonusPercent > 0f) config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ArmorHead, ModifierPercent = 100f + armorBonusPercent });
                if (moveSpeedBonusPercent > 0f) config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.MaxSpeedMultiplier, ModifierPercent = 100f + moveSpeedBonusPercent });
                if (attackSpeedBonusPercent > 0f) config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ThrustOrRangedReadySpeedMultiplier, ModifierPercent = 100f + attackSpeedBonusPercent });
                if (config.Properties.Count > 0) BLTAgentModifierBehavior.Current?.Add(a, config);
            }
            catch { }
        }

        private void RemoveBuff(Agent a, float damageBonusPercent, float armorBonusPercent, float moveSpeedBonusPercent, float attackSpeedBonusPercent)
        {
            if (a == null || !a.IsActive()) return;
            try
            {
                var config = new AgentModifierConfig();
                if (damageBonusPercent > 0f) config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.SwingSpeedMultiplier, ModifierPercent = 10000f / (100f + damageBonusPercent) });
                if (armorBonusPercent > 0f) config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ArmorHead, ModifierPercent = 10000f / (100f + armorBonusPercent) });
                if (moveSpeedBonusPercent > 0f) config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.MaxSpeedMultiplier, ModifierPercent = 10000f / (100f + moveSpeedBonusPercent) });
                if (attackSpeedBonusPercent > 0f) config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ThrustOrRangedReadySpeedMultiplier, ModifierPercent = 10000f / (100f + attackSpeedBonusPercent) });
                if (config.Properties.Count > 0) BLTAgentModifierBehavior.Current?.Add(a, config);
            }
            catch { }
        }

        public override LocString Description =>
            $"Buff Aura: allies in {AuraRange:0}m get +{DamageBonusPercent:0}% DMG +{ArmorBonusPercent:0}% armor +{MoveSpeedBonusPercent:0}% spd";
    }

    // TIMED BUFF TRACKER
    internal class BLTTimedBuffBehavior : MissionBehavior
    {
        public static BLTTimedBuffBehavior Current { get; private set; }
        private readonly List<(Agent agent, float expiry, AgentModifierConfig negator)> pending
            = new List<(Agent, float, AgentModifierConfig)>();

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        public static void AddTimedBuff(Agent agent, AgentModifierConfig config, float duration)
        {
            BLTAgentModifierBehavior.Current?.Add(agent, config);
            var negator = new AgentModifierConfig();
            foreach (var prop in config.Properties)
                negator.Properties.Add(new PropertyModifierDef { Name = prop.Name, ModifierPercent = prop.ModifierPercent > 0f ? 10000f / prop.ModifierPercent : 100f });
            float expiry = (Mission.Current?.CurrentTime ?? 0f) + duration;
            if (Current == null) Mission.Current?.AddMissionBehavior(new BLTTimedBuffBehavior());
            Current?.pending.Add((agent, expiry, negator));
        }

        public override void OnBehaviorInitialize() { base.OnBehaviorInitialize(); Current = this; }
        public override void OnRemoveBehavior() { base.OnRemoveBehavior(); if (Current == this) Current = null; }

        public override void OnMissionTick(float dt)
        {
            if (pending.Count == 0) return;
            float now = Mission.Current?.CurrentTime ?? 0f;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var (agent, expiry, negator) = pending[i];
                if (now >= expiry)
                {
                    pending.RemoveAt(i);
                    if (agent != null && agent.IsActive())
                        try { BLTAgentModifierBehavior.Current?.Add(agent, negator); } catch { }
                }
            }
        }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 10. BUFF REWARD
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Buff (Reward)"),
     Description("Twitch channel point reward: temporarily boosts hero stats"),
     UsedImplicitly]
    public class BuffReward : IRewardHandler
    {
        public Type RewardConfigType => typeof(BuffRewardSettings);

        public class BuffRewardSettings
        {
            [DisplayName("Damage Bonus (%)"), Description("Percentage bonus added to damage dealt while this effect is active."), UsedImplicitly] public float DamageBonusPercent { get; set; } = 30f;
            [DisplayName("Armor Bonus (%)"), Description("Percentage bonus added to armor while this effect is active."), UsedImplicitly] public float ArmorBonusPercent { get; set; } = 20f;
            [DisplayName("Speed Bonus (%)"), Description("Percentage bonus added to movement speed."), UsedImplicitly] public float SpeedBonusPercent { get; set; } = 20f;
            [DisplayName("Duration (seconds)"), Description("How long (in seconds) this effect lasts once triggered."), UsedImplicitly] public float DurationSeconds { get; set; } = 30f;
            [DisplayName("Gold Cost"), Description("BLT gold required to use this. Set to 0 to make it free."), UsedImplicitly] public int GoldCost { get; set; } = 0;
            [DisplayName("Show Message"), Description("Whether to post a chat/feed message when this effect triggers."), UsedImplicitly] public bool ShowMessage { get; set; } = true;
        }

        public void Enqueue(ReplyContext context, object config)
        {
            var s = config as BuffRewardSettings ?? new BuffRewardSettings();
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (hero == null) { ActionManager.SendReply(context, "You don't have an adopted hero."); return; }
            var heroAgent = hero.GetAgent();
            if (heroAgent == null || !heroAgent.IsActive()) { ActionManager.SendReply(context, "Your hero is not in an active battle."); return; }
            if (s.GoldCost > 0)
            {
                int gold = BLTAdoptAHeroCampaignBehavior.Current?.GetHeroGold(hero) ?? 0;
                if (gold < s.GoldCost) { ActionManager.SendReply(context, $"Not enough BLT gold (need {s.GoldCost}, have {gold})."); return; }
                BLTAdoptAHeroCampaignBehavior.Current?.ChangeHeroGold(hero, -s.GoldCost, true);
            }
            var modifierConfig = new AgentModifierConfig();
            if (s.DamageBonusPercent != 0) modifierConfig.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.SwingSpeedMultiplier, ModifierPercent = 100f + s.DamageBonusPercent });
            if (s.ArmorBonusPercent != 0) modifierConfig.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ArmorHead, ModifierPercent = 100f + s.ArmorBonusPercent });
            if (s.SpeedBonusPercent != 0) modifierConfig.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.MaxSpeedMultiplier, ModifierPercent = 100f + s.SpeedBonusPercent });
            try
            {
                if (s.DurationSeconds > 0f) BLTTimedBuffBehavior.AddTimedBuff(heroAgent, modifierConfig, s.DurationSeconds);
                else BLTAgentModifierBehavior.Current?.Add(heroAgent, modifierConfig);
            }
            catch { }
            if (s.ShowMessage) Log.ShowInformation($"BUFF! {hero.Name} is empowered for {s.DurationSeconds:0}s!", hero.CharacterObject);
            ActionManager.SendReply(context, $"Buffed for {s.DurationSeconds:0}s.");
        }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 11. TELEPORT REWARD
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    public enum TeleportMode { Ally, Enemy, Random }

    [DisplayName("Teleport (Reward)"),
     Description("Twitch channel point reward: teleports the hero to an ally or enemy"),
     UsedImplicitly]
    public class TeleportReward : IRewardHandler
    {
        public Type RewardConfigType => typeof(TeleportRewardSettings);

        public class TeleportRewardSettings
        {
            [DisplayName("Teleport Mode"), Description("Whether the teleport targets an ally, an enemy, or picks randomly."), UsedImplicitly] public TeleportMode Mode { get; set; } = TeleportMode.Ally;
            [DisplayName("Search Range (m)"), Description("How far (in meters) to look for a valid target."), UsedImplicitly] public float SearchRange { get; set; } = 80f;
            [DisplayName("Offset Distance (m)"), Description("Distance in meters to offset the teleport landing spot from the target, so you don't land inside them."), UsedImplicitly] public float OffsetDistance { get; set; } = 2f;
            [DisplayName("Gold Cost"), Description("BLT gold required to use this. Set to 0 to make it free."), UsedImplicitly] public int GoldCost { get; set; } = 0;
            [DisplayName("Show Message"), Description("Whether to post a chat/feed message when this effect triggers."), UsedImplicitly] public bool ShowMessage { get; set; } = true;
            [DisplayName("Splash Damage On Enemy Teleport"), Description("Extra damage dealt in a small radius when teleporting onto an enemy."), UsedImplicitly] public int SplashDamage { get; set; } = 20;
            [DisplayName("Splash Radius (m)"), Description("Radius in meters of the splash/area effect around the impact point."), UsedImplicitly] public float SplashRadius { get; set; } = 3f;
        }

        public void Enqueue(ReplyContext context, object config)
        {
            var s = config as TeleportRewardSettings ?? new TeleportRewardSettings();
            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (hero == null) { ActionManager.SendReply(context, "You don't have an adopted hero."); return; }
            var heroAgent = hero.GetAgent();
            if (heroAgent == null || !heroAgent.IsActive()) { ActionManager.SendReply(context, "Hero is not in battle."); return; }
            if (s.GoldCost > 0)
            {
                int gold = BLTAdoptAHeroCampaignBehavior.Current?.GetHeroGold(hero) ?? 0;
                if (gold < s.GoldCost) { ActionManager.SendReply(context, $"Not enough gold (need {s.GoldCost}, have {gold})."); return; }
                BLTAdoptAHeroCampaignBehavior.Current?.ChangeHeroGold(hero, -s.GoldCost, true);
            }
            try
            {
                var target = TeleportHelpers.FindTarget(heroAgent, s.Mode, s.SearchRange);
                if (target == null) { ActionManager.SendReply(context, "No valid target found."); return; }
                TeleportHelpers.TeleportToTarget(heroAgent, target, s.OffsetDistance);
                if (s.Mode == TeleportMode.Enemy || (s.Mode == TeleportMode.Random && target.IsEnemyOf(heroAgent)))
                    TeleportHelpers.ApplySplashDamage(heroAgent, target, s.SplashRadius, s.SplashDamage);
                if (s.ShowMessage) Log.ShowInformation($"TELEPORT! {hero.Name} vanishes in a flash!", hero.CharacterObject);
                ActionManager.SendReply(context, $"Teleported!");
            }
            catch { ActionManager.SendReply(context, "Teleport failed."); }
        }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 12. TELEPORT PASSIVE (active + passive)
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Teleport (Passive)"),
     Description("Auto-teleport every X seconds during battle"),
     UsedImplicitly]
    public class TeleportPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Interval (seconds)"), Description("How often (in seconds) this effect repeats/re-triggers."), UsedImplicitly] public float IntervalSeconds { get; set; } = 15f;
        [DisplayName("Teleport Mode"), Description("Whether the teleport targets an ally, an enemy, or picks randomly."), UsedImplicitly] public TeleportMode Mode { get; set; } = TeleportMode.Enemy;
        [DisplayName("Search Range (m)"), Description("How far (in meters) to look for a valid target."), UsedImplicitly] public float SearchRange { get; set; } = 60f;
        [DisplayName("Offset Distance (m)"), Description("Distance in meters to offset the teleport landing spot from the target, so you don't land inside them."), UsedImplicitly] public float OffsetDistance { get; set; } = 2f;
        [DisplayName("Only When In Danger"), Description("Only triggers when the hero's HP is below the danger threshold."), UsedImplicitly] public bool OnlyWhenInDanger { get; set; } = false;
        [DisplayName("Danger HP Threshold (%)"), Description("HP percentage below which the hero is considered 'in danger' for this effect's purposes."), UsedImplicitly] public float DangerHpPercent { get; set; } = 30f;
        [DisplayName("Show Message"), Description("Whether to post a chat/feed message when this effect triggers."), UsedImplicitly] public bool ShowMessage { get; set; } = true;
        [DisplayName("Splash Damage On Enemy Teleport"), Description("Extra damage dealt in a small radius when teleporting onto an enemy."), UsedImplicitly] public int SplashDamage { get; set; } = 20;
        [DisplayName("Splash Radius (m)"), Description("Radius in meters of the splash/area effect around the impact point."), UsedImplicitly] public float SplashRadius { get; set; } = 3f;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float searchRange = PowerProgression.ScaleFloat(this, hero, nameof(SearchRange), SearchRange);
            int   splashDamage = PowerProgression.ScaleInt(this, hero, nameof(SplashDamage), SplashDamage);
            float splashRadius = PowerProgression.ScaleFloat(this, hero, nameof(SplashRadius), SplashRadius);

            float lastTeleport = 0f;

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastTeleport < IntervalSeconds) return;
                if (OnlyWhenInDanger && Mode != TeleportMode.Enemy)
                    if (heroAgent.Health / heroAgent.HealthLimit * 100f >= DangerHpPercent) return;
                var target = TeleportHelpers.FindTarget(heroAgent, Mode, searchRange);
                if (target == null) return;
                lastTeleport = now;
                try
                {
                    TeleportHelpers.TeleportToTarget(heroAgent, target, OffsetDistance);
                    if (target.IsEnemyOf(heroAgent))
                        TeleportHelpers.ApplySplashDamage(heroAgent, target, splashRadius, splashDamage);
                    if (ShowMessage) Log.ShowInformation($"TELEPORT! {hero.Name} vanishes!", hero.CharacterObject);
                }
                catch { }
            };
        }

        public override LocString Description =>
            $"Auto-Teleport: every {IntervalSeconds:0}s to {Mode} within {SearchRange:0}m";
    }

    internal static class TeleportHelpers
    {
        public static Agent FindTarget(Agent heroAgent, TeleportMode mode, float range)
        {
            var buffer = new MBList<Agent>();
            var nearby = Mission.Current.GetNearbyAgents(heroAgent.Position.AsVec2, range, buffer);
            if (mode == TeleportMode.Random)
                mode = MBRandom.RandomInt(2) == 0 ? TeleportMode.Ally : TeleportMode.Enemy;
            if (mode == TeleportMode.Ally)
            {
                var allies = nearby.Where(a => a != heroAgent && a.IsActive() && !a.IsMount && a.IsFriendOf(heroAgent)).ToList();
                return allies.Count > 0 ? allies[MBRandom.RandomInt(allies.Count)] : null;
            }
            return nearby.Where(a => a.IsActive() && !a.IsMount && a.IsEnemyOf(heroAgent))
                .OrderBy(a => a.Position.DistanceSquared(heroAgent.Position)).FirstOrDefault();
        }

        public static void TeleportToTarget(Agent heroAgent, Agent target, float offsetDistance)
        {
            var targetPos = target.GetWorldPosition();
            if (offsetDistance > 0f)
            {
                var dir = (heroAgent.Position - target.Position).NormalizedCopy();
                targetPos.SetVec2(targetPos.AsVec2 + dir.AsVec2 * offsetDistance);
            }
            heroAgent.TeleportToPosition(targetPos.GetNavMeshVec3());
        }

        public static void ApplySplashDamage(Agent heroAgent, Agent target, float splashRadius, int splashDamage)
        {
            if (splashRadius <= 0f || splashDamage <= 0) return;
            var buffer = new MBList<Agent>();
            foreach (var victim in Mission.Current.GetNearbyAgents(target.Position.AsVec2, splashRadius, buffer)
                .Where(a => a.IsActive() && !a.IsMount && a.IsEnemyOf(heroAgent)).ToList())
            {
                try
                {
                    var dir = Vec3.Up;
                    var blow = new Blow(heroAgent.Index)
                    {
                        AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Blunt,
                        BoneIndex = victim.Monster.HeadLookDirectionBoneIndex, GlobalPosition = victim.Position,
                        BaseMagnitude = splashDamage, InflictedDamage = splashDamage,
                        SwingDirection = dir, Direction = dir, DamageCalculated = true,
                        VictimBodyPart = BoneBodyPartType.Chest,
                    };
                    victim.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, victim, blow));
                }
                catch { }
            }
        }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 13. JUMP ATTACK
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Jump Attack"),
     Description("Periodically dashes to the nearest enemy with damage and knockback"),
     UsedImplicitly]
    public class JumpAttackPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Cooldown (seconds)"), Description("Minimum time (in seconds) between activations of this effect."), UsedImplicitly] public float CooldownSeconds { get; set; } = 8f;
        [DisplayName("Detection Range (m)"), Description("How far (in meters) this effect can detect valid targets."), UsedImplicitly] public float DetectionRange { get; set; } = 6f;
        [DisplayName("Base Damage"), Description("Base damage dealt before any modifiers."), UsedImplicitly] public int BaseDamage { get; set; } = 30;
        [DisplayName("Damage Bonus (%)"), Description("Percentage bonus added to damage dealt while this effect is active."), UsedImplicitly] public float DamageBonusPercent { get; set; } = 50f;
        [DisplayName("Knockback Enabled"), Description("Whether this effect can physically push back the targets it hits."), UsedImplicitly] public bool KnockbackEnabled { get; set; } = true;
        [DisplayName("Knockback Distance (m)"), Description("How far (in meters) hit targets get pushed back."), UsedImplicitly] public float KnockbackDistance { get; set; } = 3f;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float cooldownSeconds   = PowerProgression.ScaleFloat(this, hero, nameof(CooldownSeconds), CooldownSeconds);
            float detectionRange    = PowerProgression.ScaleFloat(this, hero, nameof(DetectionRange), DetectionRange);
            int   baseDamage        = PowerProgression.ScaleInt(this, hero, nameof(BaseDamage), BaseDamage);
            float damageBonusPercent= PowerProgression.ScaleFloat(this, hero, nameof(DamageBonusPercent), DamageBonusPercent);
            float knockbackDistance = PowerProgression.ScaleFloat(this, hero, nameof(KnockbackDistance), KnockbackDistance);

            float lastJump = 0f;

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastJump < cooldownSeconds) return;
                var buffer = new MBList<Agent>();
                var nearest = Mission.Current.GetNearbyAgents(heroAgent.Position.AsVec2, detectionRange, buffer)
                    .Where(a => a.IsActive() && !a.IsMount && a.IsEnemyOf(heroAgent))
                    .OrderBy(a => a.Position.DistanceSquared(heroAgent.Position)).FirstOrDefault();
                if (nearest == null) return;
                lastJump = now;
                try
                {
                    var direction = (nearest.Position - heroAgent.Position).NormalizedCopy();
                    var landPos = nearest.GetWorldPosition();
                    landPos.SetVec2(landPos.AsVec2 - direction.AsVec2 * 1.5f);
                    heroAgent.TeleportToPosition(landPos.GetNavMeshVec3());
                    int finalDamage = (int)(baseDamage * (1f + damageBonusPercent / 100f));
                    var blow = new Blow(heroAgent.Index)
                    {
                        AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Blunt,
                        BoneIndex = nearest.Monster.HeadLookDirectionBoneIndex, GlobalPosition = nearest.Position,
                        BaseMagnitude = baseDamage, InflictedDamage = finalDamage,
                        SwingDirection = direction, Direction = direction, DamageCalculated = true,
                        VictimBodyPart = BoneBodyPartType.Chest,
                    };
                    nearest.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, nearest, blow));
                    if (KnockbackEnabled && nearest.IsActive())
                    {
                        var knockbackPos = nearest.GetWorldPosition();
                        knockbackPos.SetVec2(knockbackPos.AsVec2 + direction.AsVec2 * knockbackDistance);
                        nearest.SetScriptedPosition(ref knockbackPos, false, Agent.AIScriptedFrameFlags.NeverSlowDown);
                    }
                }
                catch { }
            };
        }

        public override LocString Description =>
            $"Jump Attack: every {CooldownSeconds:0}s dashes to nearest enemy in {DetectionRange:0}m, deals {BaseDamage}+{DamageBonusPercent:0}%";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 14. KICK
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Kick"),
     Description("Periodically performs a kick on the nearest close enemy, with knockback"),
     UsedImplicitly]
    public class KickPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Cooldown (seconds)"), Description("Minimum time (in seconds) between activations of this effect."), UsedImplicitly] public float CooldownSeconds { get; set; } = 6f;
        [DisplayName("Kick Range (m)"), Description("Maximum range in meters for the kick to connect."), UsedImplicitly] public float KickRange { get; set; } = 2.5f;
        [DisplayName("Damage"), Description("Damage dealt by this effect."), UsedImplicitly] public int Damage { get; set; } = 20;
        [DisplayName("Knockback Distance (m)"), Description("How far (in meters) hit targets get pushed back."), UsedImplicitly] public float KnockbackDistance { get; set; } = 4f;
        [DisplayName("Use Left Leg"), Description("Whether the left leg is used for this effect's targeting/animation instead of the right."), UsedImplicitly] public bool UseLeftLeg { get; set; } = false;
        [DisplayName("Show Message"), Description("Whether to post a chat/feed message when this effect triggers."), UsedImplicitly] public bool ShowMessage { get; set; } = false;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float cooldownSeconds = PowerProgression.ScaleFloat(this, hero, nameof(CooldownSeconds), CooldownSeconds);
            float kickRange       = PowerProgression.ScaleFloat(this, hero, nameof(KickRange), KickRange);

            float lastKick = 0f;

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastKick < cooldownSeconds) return;
                var buffer = new MBList<Agent>();
                var nearest = Mission.Current.GetNearbyAgents(heroAgent.Position.AsVec2, kickRange, buffer)
                    .Where(a => a.IsActive() && !a.IsMount && a.IsEnemyOf(heroAgent))
                    .OrderBy(a => a.Position.DistanceSquared(heroAgent.Position)).FirstOrDefault();
                if (nearest == null) return;
                lastKick = now;
                try
                {
                    var direction = (nearest.Position - heroAgent.Position).NormalizedCopy();
                    try
                    {
                        string actionName = UseLeftLeg ? "act_kick_left_leg" : "act_kick_right_leg";
                        heroAgent.SetActionChannel(1, ActionIndexCache.Create(actionName), ignorePriority: true, 0UL);
                    }
                    catch { }
                    var blow = new Blow(heroAgent.Index)
                    {
                        AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Blunt,
                        BoneIndex = nearest.Monster.HeadLookDirectionBoneIndex, GlobalPosition = nearest.Position,
                        BaseMagnitude = Damage, InflictedDamage = Damage,
                        SwingDirection = direction, Direction = direction, DamageCalculated = true,
                        VictimBodyPart = BoneBodyPartType.Abdomen,
                    };
                    nearest.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, nearest, blow));
                    if (KnockbackDistance > 0f && nearest.IsActive())
                    {
                        var knockbackPos = nearest.GetWorldPosition();
                        knockbackPos.SetVec2(knockbackPos.AsVec2 + direction.AsVec2 * KnockbackDistance);
                        nearest.SetScriptedPosition(ref knockbackPos, false, Agent.AIScriptedFrameFlags.NeverSlowDown);
                    }
                    if (ShowMessage) Log.ShowInformation($"KICK! {hero.Name} kicks the enemy!", hero.CharacterObject);
                }
                catch { }
            };
        }

        public override LocString Description =>
            $"Kick: every {CooldownSeconds:0}s kicks nearest enemy within {KickRange:0.#}m for {Damage} dmg, knockback {KnockbackDistance:0.#}m";
    }


    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // 16. BURNING STRIKE
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [DisplayName("Burning Strike"),
     Description("Each hit sets the enemy on fire: contour + fire DoT damage"),
     UsedImplicitly]
    public class BurningStrikePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Fire Damage Per Tick"), Description("Burn damage dealt each tick."), UsedImplicitly] public int FireDamagePerTick { get; set; } = 12;
        [DisplayName("Burn Duration (ticks)"), Description("How many ticks the burn effect lasts."), UsedImplicitly] public int BurnDurationTicks { get; set; } = 4;
        [DisplayName("Refresh On Hit"), Description("Whether landing another hit refreshes this effect's duration instead of stacking."), UsedImplicitly] public bool RefreshOnHit { get; set; } = true;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FFFF4400";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            int fireDamagePerTick = PowerProgression.ScaleInt(this, hero, nameof(FireDamagePerTick), FireDamagePerTick);
            int burnDurationTicks = PowerProgression.ScaleInt(this, hero, nameof(BurnDurationTicks), BurnDurationTicks);
            var burningAgents = new Dictionary<Agent, int>();

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (victim == null || !victim.IsActive()) return;
                if (attacker == null || !victim.IsEnemyOf(attacker)) return;
                if (RefreshOnHit || !burningAgents.ContainsKey(victim)) burningAgents[victim] = burnDurationTicks;
                if (ShowContour)
                {
                    try { SafeSetContourColor(victim, Convert.ToUInt32(ContourColor, 16), true); }
                    catch { SafeSetContourColor(victim, 0xFFFF4400u, true); }
                }
                // Efekt eksplozji ognia przy trafieniu
                try
                {
                    int psysId = ParticleSystemManager.GetRuntimeIdByName("explosion_fire_medium");
                    if (psysId >= 0)
                    {
                        var frame = MatrixFrame.Identity;
                        frame.origin = victim.Position + new Vec3(0f, 0f, 0.8f);
                        Mission.Current?.Scene?.CreateBurstParticle(psysId, frame);
                    }
                }
                catch { }
            };

            handlers.OnSlowTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;
                foreach (var key in burningAgents.Keys.ToList())
                {
                    if (key == null || !key.IsActive()) { if (ShowContour) try { SafeSetContourColor(key, null, false); } catch { } burningAgents.Remove(key); continue; }
                    try
                    {
                        var dir = Vec3.Up;
                        var blow = new Blow(heroAgent.Index)
                        {
                            AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Pierce,
                            BoneIndex = key.Monster.HeadLookDirectionBoneIndex, GlobalPosition = key.Position,
                            BaseMagnitude = fireDamagePerTick, InflictedDamage = fireDamagePerTick,
                            SwingDirection = dir, Direction = dir, DamageCalculated = true,
                            VictimBodyPart = BoneBodyPartType.Chest,
                        };
                        key.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, key, blow));
                    }
                    catch { }
                    burningAgents[key]--;
                    if (burningAgents[key] <= 0) { if (ShowContour) try { SafeSetContourColor(key, null, false); } catch { } burningAgents.Remove(key); }
                }
            };

            void Cleanup()
            {
                if (ShowContour) foreach (var a in burningAgents.Keys) try { SafeSetContourColor(a, null, false); } catch { }
                burningAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Burning Strike: hit ignites enemy ({FireDamagePerTick} fire dmg/2s for {BurnDurationTicks * 2}s)";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 16. FROST STRIKE
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Frost Strike"),
     Description("Hits slow the enemy's movement speed for a duration, ice blue contour"),
     UsedImplicitly]
    public class FrostStrikePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Slow Speed Limit"), Description("Maximum movement speed fraction (0-1) allowed while slowed - lower is a stronger slow."), UsedImplicitly] public float SlowSpeedLimit { get; set; } = 0.4f;
        [DisplayName("Frost Duration (ticks)"), Description("How many ticks the frost/slow effect lasts."), UsedImplicitly] public int FrostDurationTicks { get; set; } = 4;
        [DisplayName("Apply On"), Description("Which kind of attacks this effect applies to (melee, ranged, or both)."), UsedImplicitly] public PoisonApplyOn ApplyOn { get; set; } = PoisonApplyOn.Both;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FF00AAFF";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float slowSpeedLimit = PowerProgression.ScaleFloat(this, hero, nameof(SlowSpeedLimit), SlowSpeedLimit);
            int frostDurationTicks = PowerProgression.ScaleInt(this, hero, nameof(FrostDurationTicks), FrostDurationTicks);
            var frostedAgents = new Dictionary<Agent, int>();

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (victim == null || !victim.IsActive() || attacker == null || !victim.IsEnemyOf(attacker)) return;
                if (ApplyOn != PoisonApplyOn.Both)
                {
                    bool isRanged = !attacker.WieldedWeapon.IsEmpty && attacker.WieldedWeapon.CurrentUsageItem?.IsRangedWeapon == true;
                    if (ApplyOn == PoisonApplyOn.RangedOnly && !isRanged) return;
                    if (ApplyOn == PoisonApplyOn.MeleeOnly && isRanged) return;
                }
                frostedAgents[victim] = frostDurationTicks;
                try { victim.SetMaximumSpeedLimit(slowSpeedLimit, false); } catch { }
                if (ShowContour) try { SafeSetContourColor(victim, Convert.ToUInt32(ContourColor, 16), true); } catch { }
            };

            handlers.OnSlowTick += dt =>
            {
                foreach (var key in frostedAgents.Keys.ToList())
                {
                    if (key == null || !key.IsActive())
                    {
                        if (ShowContour) try { SafeSetContourColor(key, null, false); } catch { }
                        frostedAgents.Remove(key); continue;
                    }
                    frostedAgents[key]--;
                    if (frostedAgents[key] <= 0)
                    {
                        try { key.SetMaximumSpeedLimit(-1f, false); } catch { }
                        if (ShowContour) try { SafeSetContourColor(key, null, false); } catch { }
                        frostedAgents.Remove(key);
                    }
                }
            };

            void Cleanup()
            {
                foreach (var a in frostedAgents.Keys)
                {
                    try { a?.SetMaximumSpeedLimit(-1f, false); } catch { }
                    if (ShowContour) try { SafeSetContourColor(a, null, false); } catch { }
                }
                frostedAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Frost Strike: hit slows enemy to {SlowSpeedLimit:0.##}x speed for {FrostDurationTicks * 2}s";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 17. VAMPIRISM STRIKE
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Vampirism Strike"),
     Description("Each hit heals the hero for a % of damage dealt, purple contour on victim"),
     UsedImplicitly]
    public class VampirismStrikePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Lifesteal (%)"), Description("Percentage of damage dealt returned to the attacker as healing."), UsedImplicitly] public float LifestealPercent { get; set; } = 20f;
        [DisplayName("Show Contour On Victim"), Description("Highlights the hit target with a colored outline - visual only."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FF880088";
        [DisplayName("Contour Duration (ticks)"), Description("How many ticks the visual outline stays visible."), UsedImplicitly] public int ContourDurationTicks { get; set; } = 2;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            var drainedAgents = new Dictionary<Agent, int>();

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (victim == null || !victim.IsActive() || attacker == null || !victim.IsEnemyOf(attacker)) return;
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || attacker != heroAgent) return;
                int dmg = blowParams.blow.InflictedDamage;
                if (dmg <= 0) return;
                float lifestealPercent = PowerProgression.ScaleFloat(this, hero, nameof(LifestealPercent), LifestealPercent);
                float heal = dmg * lifestealPercent / 100f;
                try { heroAgent.Health = Math.Min(heroAgent.HealthLimit, heroAgent.Health + heal); } catch { }
                if (ShowContour)
                {
                    drainedAgents[victim] = ContourDurationTicks;
                    try { SafeSetContourColor(victim, Convert.ToUInt32(ContourColor, 16), true); } catch { }
                }
            };

            handlers.OnSlowTick += dt =>
            {
                foreach (var key in drainedAgents.Keys.ToList())
                {
                    if (key == null || !key.IsActive()) { try { SafeSetContourColor(key, null, false); } catch { } drainedAgents.Remove(key); continue; }
                    drainedAgents[key]--;
                    if (drainedAgents[key] <= 0) { try { SafeSetContourColor(key, null, false); } catch { } drainedAgents.Remove(key); }
                }
            };

            void Cleanup()
            {
                foreach (var a in drainedAgents.Keys) try { SafeSetContourColor(a, null, false); } catch { }
                drainedAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description => $"Vampirism Strike: heals {LifestealPercent}% of damage dealt";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 18. CHAIN LIGHTNING
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Chain Lightning"),
     Description("Each hit chains lightning to nearby enemies, yellow contour"),
     UsedImplicitly]
    public class ChainLightningPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Chain Damage"), Description("Damage dealt to each additional target the effect chains to."), UsedImplicitly] public int ChainDamage { get; set; } = 25;
        [DisplayName("Chain Radius"), Description("How far (in meters) the effect can jump/chain from one target to the next."), UsedImplicitly] public float ChainRadius { get; set; } = 5f;
        [DisplayName("Max Targets"), Description("Maximum number of targets this effect can hit at once."), UsedImplicitly] public int MaxTargets { get; set; } = 3;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FFFFFF00";
        [DisplayName("Contour Duration (ticks)"), Description("How many ticks the visual outline stays visible."), UsedImplicitly] public int ContourDurationTicks { get; set; } = 1;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            int chainDamage = PowerProgression.ScaleInt(this, hero, nameof(ChainDamage), ChainDamage);
            float chainRadius = PowerProgression.ScaleFloat(this, hero, nameof(ChainRadius), ChainRadius);
            int maxTargets = PowerProgression.ScaleInt(this, hero, nameof(MaxTargets), MaxTargets);
            var zappedAgents = new Dictionary<Agent, int>();

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (victim == null || !victim.IsActive() || attacker == null || !victim.IsEnemyOf(attacker)) return;
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || attacker != heroAgent) return;

                var nearby = Mission.Current?.Agents
                    ?.Where(a => a != null && a != victim && a.IsActive() && a.IsEnemyOf(heroAgent)
                                 && a.Position.Distance(victim.Position) <= chainRadius)
                    .OrderBy(a => a.Position.Distance(victim.Position))
                    .Take(maxTargets)
                    .ToList();
                if (nearby == null) return;

                var dir = Vec3.Up;
                foreach (var target in nearby)
                {
                    try
                    {
                        var blow = new Blow(heroAgent.Index)
                        {
                            AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Pierce,
                            BoneIndex = target.Monster.HeadLookDirectionBoneIndex, GlobalPosition = target.Position,
                            BaseMagnitude = chainDamage, InflictedDamage = chainDamage,
                            SwingDirection = dir, Direction = dir, DamageCalculated = true,
                            VictimBodyPart = BoneBodyPartType.Chest,
                        };
                        target.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, target, blow));
                    }
                    catch { }
                    zappedAgents[target] = ContourDurationTicks;
                    if (ShowContour) try { SafeSetContourColor(target, Convert.ToUInt32(ContourColor, 16), true); } catch { }
                }
            };

            handlers.OnSlowTick += dt =>
            {
                foreach (var key in zappedAgents.Keys.ToList())
                {
                    if (key == null || !key.IsActive()) { if (ShowContour) try { SafeSetContourColor(key, null, false); } catch { } zappedAgents.Remove(key); continue; }
                    zappedAgents[key]--;
                    if (zappedAgents[key] <= 0) { if (ShowContour) try { SafeSetContourColor(key, null, false); } catch { } zappedAgents.Remove(key); }
                }
            };

            void Cleanup()
            {
                if (ShowContour) foreach (var a in zappedAgents.Keys) try { SafeSetContourColor(a, null, false); } catch { }
                zappedAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Chain Lightning: hit chains {ChainDamage} dmg to {MaxTargets} enemies in {ChainRadius:0.#}m";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 19. BLEED STRIKE
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Bleed Strike"),
     Description("Hits cause bleeding (stackable DoT), dark red contour"),
     UsedImplicitly]
    public class BleedStrikePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Bleed Damage Per Tick"), Description("Bleed damage dealt each tick."), UsedImplicitly] public int BleedDamagePerTick { get; set; } = 8;
        [DisplayName("Bleed Duration (ticks)"), Description("How many ticks the bleed effect lasts."), UsedImplicitly] public int BleedDurationTicks { get; set; } = 6;
        [DisplayName("Max Stacks"), Description("Maximum number of times this effect can stack on the same target."), UsedImplicitly] public int MaxStacks { get; set; } = 5;
        [DisplayName("Apply On"), Description("Which kind of attacks this effect applies to (melee, ranged, or both)."), UsedImplicitly] public PoisonApplyOn ApplyOn { get; set; } = PoisonApplyOn.MeleeOnly;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FFAA0000";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            // stacks: agent -> (ticks remaining, stack count)
            var bleedingAgents = new Dictionary<Agent, (int ticks, int stacks)>();

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (victim == null || !victim.IsActive() || attacker == null || !victim.IsEnemyOf(attacker)) return;
                if (ApplyOn != PoisonApplyOn.Both)
                {
                    bool isRanged = !attacker.WieldedWeapon.IsEmpty && attacker.WieldedWeapon.CurrentUsageItem?.IsRangedWeapon == true;
                    if (ApplyOn == PoisonApplyOn.RangedOnly && !isRanged) return;
                    if (ApplyOn == PoisonApplyOn.MeleeOnly && isRanged) return;
                }
                int stacks = bleedingAgents.TryGetValue(victim, out var cur) ? Math.Min(cur.stacks + 1, MaxStacks) : 1;
                bleedingAgents[victim] = (BleedDurationTicks, stacks);
                if (ShowContour) try { SafeSetContourColor(victim, Convert.ToUInt32(ContourColor, 16), true); } catch { }
            };

            handlers.OnSlowTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;
                foreach (var key in bleedingAgents.Keys.ToList())
                {
                    if (key == null || !key.IsActive()) { if (ShowContour) try { SafeSetContourColor(key, null, false); } catch { } bleedingAgents.Remove(key); continue; }
                    var (ticks, stacks) = bleedingAgents[key];
                    try
                    {
                        int dmg = BleedDamagePerTick * stacks;
                        var dir = Vec3.Up;
                        var blow = new Blow(heroAgent.Index)
                        {
                            AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Pierce,
                            BoneIndex = key.Monster.HeadLookDirectionBoneIndex, GlobalPosition = key.Position,
                            BaseMagnitude = dmg, InflictedDamage = dmg,
                            SwingDirection = dir, Direction = dir, DamageCalculated = true,
                            VictimBodyPart = BoneBodyPartType.Chest,
                        };
                        key.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, key, blow));
                    }
                    catch { }
                    ticks--;
                    if (ticks <= 0) { if (ShowContour) try { SafeSetContourColor(key, null, false); } catch { } bleedingAgents.Remove(key); }
                    else bleedingAgents[key] = (ticks, stacks);
                }
            };

            void Cleanup()
            {
                if (ShowContour) foreach (var a in bleedingAgents.Keys) try { SafeSetContourColor(a, null, false); } catch { }
                bleedingAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Bleed Strike: {BleedDamagePerTick} dmg/2s per stack (max {MaxStacks}), melee only";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 20. FEAR AURA
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Fear Aura"),
     Description("Enemies near the hero have a chance to flee/panic each tick, dark purple contour"),
     UsedImplicitly]
    public class FearAuraPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Aura Radius"), Description("How far from the hero (in meters) this aura reaches."), UsedImplicitly] public float Radius { get; set; } = 6f;
        [DisplayName("Fear Chance Per Tick (%)"), Description("Chance per tick that a nearby enemy becomes feared."), UsedImplicitly] public float FearChancePercent { get; set; } = 15f;
        [DisplayName("Fear Tick Interval (seconds)"), Description("How often (in seconds) the fear chance is re-rolled for nearby enemies."), UsedImplicitly] public float FearTickInterval { get; set; } = 3f;
        [DisplayName("Max Agents Per Tick"), Description("Caps how many nearby agents this effect can hit per tick, closest first - keeps performance in check in large battles."), UsedImplicitly] public int MaxAgentsPerTick { get; set; } = 5;
        [DisplayName("Contour Update Interval (seconds)"), Description("How often (in seconds) the visual outline is refreshed."), UsedImplicitly] public float ContourUpdateInterval { get; set; } = 0.5f;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FF440044";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float Radius = PowerProgression.ScaleFloat(this, hero, nameof(this.Radius), this.Radius);
            float FearChancePercent = PowerProgression.ScaleFloat(this, hero, nameof(this.FearChancePercent), this.FearChancePercent);
            int maxAgents = PowerProgression.ScaleInt(this, hero, nameof(MaxAgentsPerTick), MaxAgentsPerTick);
            var fearedAgents = new HashSet<Agent>();
            var lastFearTime = new Dictionary<Agent, float>();
            float lastContourUpdate = -999f;

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;
                float now = Mission.Current?.CurrentTime ?? 0f;

                // Contour i cleanup co ContourUpdateInterval sekund
                if (now - lastContourUpdate >= ContourUpdateInterval)
                {
                    lastContourUpdate = now;

                    foreach (var a in fearedAgents.ToList())
                        if (a == null || !a.IsActive()) { if (ShowContour) try { SafeSetContourColor(a, null, false); } catch { } fearedAgents.Remove(a); lastFearTime.Remove(a); }

                    var inRange = Mission.Current?.Agents
                        ?.Where(a => a != null && a.IsActive() && a.IsEnemyOf(heroAgent)
                                     && a.Position.Distance(heroAgent.Position) <= Radius)
                        .OrderBy(a => a.Position.Distance(heroAgent.Position))
                        .Take(Math.Max(1, maxAgents))
                        .ToList();
                    if (inRange == null) return;

                    foreach (var enemy in inRange)
                    {
                        fearedAgents.Add(enemy);
                        if (ShowContour) try { SafeSetContourColor(enemy, Convert.ToUInt32(ContourColor, 16), true); } catch { }
                    }

                    foreach (var a in fearedAgents.ToList())
                    {
                        if (a == null || !a.IsActive()) { if (ShowContour) try { SafeSetContourColor(a, null, false); } catch { } fearedAgents.Remove(a); lastFearTime.Remove(a); continue; }
                        if (a.Position.Distance(heroAgent.Position) > Radius)
                        {
                            if (ShowContour) try { SafeSetContourColor(a, null, false); } catch { }
                            fearedAgents.Remove(a); lastFearTime.Remove(a);
                        }
                    }
                }

                // Fear push co FearTickInterval sekund per agent
                foreach (var enemy in fearedAgents.ToList())
                {
                    if (enemy == null || !enemy.IsActive()) continue;
                    if (lastFearTime.TryGetValue(enemy, out float last) && now - last < FearTickInterval) continue;
                    if (MBRandom.RandomFloat * 100f >= FearChancePercent) { lastFearTime[enemy] = now; continue; }
                    try
                    {
                        Vec3 awayDir3 = (enemy.Position - heroAgent.Position);
                        if (awayDir3.Length > 0.01f) awayDir3 = awayDir3.NormalizedCopy();
                        var awayDir2 = new Vec2(awayDir3.x, awayDir3.y).Normalized();
                        enemy.SetMovementDirection(awayDir2);
                    }
                    catch { }
                    lastFearTime[enemy] = now;
                }
            };

            void Cleanup()
            {
                if (ShowContour) foreach (var a in fearedAgents) try { SafeSetContourColor(a, null, false); } catch { }
                fearedAgents.Clear(); lastFearTime.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Fear Aura: {FearChancePercent}% chance to flee every {FearTickInterval:0.#}s within {Radius:0.#}m";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 21. SLOW AURA
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Slow Aura"),
     Description("Enemies within radius are slowed, cyan contour"),
     UsedImplicitly]
    public class SlowAuraPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Aura Radius"), Description("How far from the hero (in meters) this aura reaches."), UsedImplicitly] public float Radius { get; set; } = 5f;
        [DisplayName("Slow Speed Limit"), Description("Maximum movement speed fraction (0-1) allowed while slowed - lower is a stronger slow."), UsedImplicitly] public float SlowSpeedLimit { get; set; } = 0.5f;
        [DisplayName("Tick Interval (seconds)"), Description("How often (in seconds) this effect re-evaluates and re-applies to nearby agents."), UsedImplicitly] public float UpdateInterval { get; set; } = 3f;
        [DisplayName("Max Agents Per Tick"), Description("Caps how many nearby agents this effect can hit per tick, closest first - keeps performance in check in large battles."), UsedImplicitly] public int MaxAgentsPerTick { get; set; } = 5;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FF00FFCC";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float Radius = PowerProgression.ScaleFloat(this, hero, nameof(this.Radius), this.Radius);
            int maxAgents = PowerProgression.ScaleInt(this, hero, nameof(MaxAgentsPerTick), MaxAgentsPerTick);
            var slowedAgents = new HashSet<Agent>();
            float lastUpdate = -999f;

            handlers.OnMissionTick += dt =>
            {
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastUpdate < UpdateInterval) return;
                lastUpdate = now;

                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;

                var inRange = Mission.Current?.Agents
                    ?.Where(a => a != null && a.IsActive() && a.IsEnemyOf(heroAgent)
                                 && a.Position.Distance(heroAgent.Position) <= Radius)
                    .OrderBy(a => a.Position.Distance(heroAgent.Position))
                    .Take(Math.Max(1, maxAgents))
                    .ToHashSet() ?? new HashSet<Agent>();

                foreach (var a in slowedAgents.ToList())
                {
                    if (a == null || !a.IsActive() || !inRange.Contains(a))
                    {
                        try { a?.SetMaximumSpeedLimit(-1f, false); if (ShowContour) SafeSetContourColor(a, null, false); } catch { }
                        slowedAgents.Remove(a);
                    }
                }

                foreach (var a in inRange)
                {
                    if (!slowedAgents.Contains(a))
                    {
                        try { a.SetMaximumSpeedLimit(SlowSpeedLimit, false); if (ShowContour) SafeSetContourColor(a, Convert.ToUInt32(ContourColor, 16), true); } catch { }
                        slowedAgents.Add(a);
                    }
                }
            };

            void Cleanup()
            {
                foreach (var a in slowedAgents)
                {
                    try { a?.SetMaximumSpeedLimit(-1f, false); if (ShowContour) SafeSetContourColor(a, null, false); } catch { }
                }
                slowedAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Slow Aura: enemies within {Radius:0.#}m slowed to {SlowSpeedLimit:0.##}x speed";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 22. WEAKNESS AURA
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Weakness Aura"),
     Description("Enemies within radius deal reduced damage, grey contour"),
     UsedImplicitly]
    public class WeaknessAuraPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Aura Radius"), Description("How far from the hero (in meters) this aura reaches."), UsedImplicitly] public float Radius { get; set; } = 6f;
        [DisplayName("Damage Reduction (%)"), Description("Percentage reduction applied to incoming damage while this effect is active."), UsedImplicitly] public float DamageReductionPercent { get; set; } = 30f;
        [DisplayName("Tick Interval (seconds)"), Description("How often (in seconds) this effect re-evaluates and re-applies to nearby agents."), UsedImplicitly] public float UpdateInterval { get; set; } = 3f;
        [DisplayName("Max Agents Per Tick"), Description("Caps how many nearby agents this effect can hit per tick, closest first - keeps performance in check in large battles."), UsedImplicitly] public int MaxAgentsPerTick { get; set; } = 5;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FF888888";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float Radius = PowerProgression.ScaleFloat(this, hero, nameof(this.Radius), this.Radius);
            float DamageReductionPercent = PowerProgression.ScaleFloat(this, hero, nameof(this.DamageReductionPercent), this.DamageReductionPercent);
            int maxAgents = PowerProgression.ScaleInt(this, hero, nameof(MaxAgentsPerTick), MaxAgentsPerTick);
            var weakenedAgents = new HashSet<Agent>();
            float lastUpdate = -999f;

            handlers.OnMissionTick += dt =>
            {
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastUpdate < UpdateInterval) return;
                lastUpdate = now;

                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;

                var inRange = Mission.Current?.Agents
                    ?.Where(a => a != null && a.IsActive() && a.IsEnemyOf(heroAgent)
                                 && a.Position.Distance(heroAgent.Position) <= Radius)
                    .OrderBy(a => a.Position.Distance(heroAgent.Position))
                    .Take(Math.Max(1, maxAgents))
                    .ToHashSet() ?? new HashSet<Agent>();

                foreach (var a in weakenedAgents.ToList())
                {
                    if (a == null || !a.IsActive() || !inRange.Contains(a))
                    {
                        if (ShowContour) try { SafeSetContourColor(a, null, false); } catch { }
                        weakenedAgents.Remove(a);
                    }
                }

                foreach (var a in inRange)
                {
                    if (!weakenedAgents.Contains(a))
                    {
                        if (ShowContour) try { SafeSetContourColor(a, Convert.ToUInt32(ContourColor, 16), true); } catch { }
                        weakenedAgents.Add(a);
                    }
                }
            };

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (attacker == null || !weakenedAgents.Contains(attacker)) return;
                float mult = 1f - DamageReductionPercent / 100f;
                blowParams.blow.BaseMagnitude *= mult;
                blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * mult);
            };

            void Cleanup()
            {
                if (ShowContour) foreach (var a in weakenedAgents) try { SafeSetContourColor(a, null, false); } catch { }
                weakenedAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Weakness Aura: enemies within {Radius:0.#}m deal {DamageReductionPercent}% less damage";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 23. BATTLE CRY AURA
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Battle Cry Aura"),
     Description("Allies within radius move/attack faster, gold contour"),
     UsedImplicitly]
    public class BattleCryAuraPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Aura Radius"), Description("How far from the hero (in meters) this aura reaches."), UsedImplicitly] public float Radius { get; set; } = 8f;
        [DisplayName("Speed Boost Multiplier"), Description("Multiplier applied to movement speed while boosted (1.0 = no change)."), UsedImplicitly] public float SpeedBoostMultiplier { get; set; } = 1.3f;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FFFFAA00";
        [DisplayName("Tick Interval (seconds)"), Description("How often (in seconds) this effect re-evaluates and re-applies to nearby agents."), UsedImplicitly] public float TickIntervalSeconds { get; set; } = 3f;
        [DisplayName("Max Agents Per Tick"), Description("Caps how many nearby agents this effect can hit per tick, closest first - keeps performance in check in large battles."), UsedImplicitly] public int MaxAgentsPerTick { get; set; } = 8;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float Radius = PowerProgression.ScaleFloat(this, hero, nameof(this.Radius), this.Radius);
            float SpeedBoostMultiplier = PowerProgression.ScaleFloat(this, hero, nameof(this.SpeedBoostMultiplier), this.SpeedBoostMultiplier);
            float tickInterval = PowerProgression.ScaleFloat(this, hero, nameof(TickIntervalSeconds), TickIntervalSeconds);
            int   maxAgents    = PowerProgression.ScaleInt(this, hero, nameof(MaxAgentsPerTick), MaxAgentsPerTick);
            float lastTick     = -999f;
            var boostedAgents = new HashSet<Agent>();

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastTick < tickInterval) return;
                lastTick = now;

                var inRange = Mission.Current?.Agents
                    ?.Where(a => a != null && a.IsActive() && !a.IsEnemyOf(heroAgent) && a != heroAgent
                                 && a.Position.Distance(heroAgent.Position) <= Radius)
                    .OrderBy(a => a.Position.Distance(heroAgent.Position))
                    .Take(Math.Max(1, maxAgents))
                    .ToHashSet() ?? new HashSet<Agent>();

                foreach (var a in boostedAgents.ToList())
                {
                    if (a == null || !a.IsActive() || !inRange.Contains(a))
                    {
                        try { a?.SetMaximumSpeedLimit(-1f, false); if (ShowContour) SafeSetContourColor(a, null, false); } catch { }
                        boostedAgents.Remove(a);
                    }
                }

                foreach (var a in inRange)
                {
                    if (!a.HasMount && !boostedAgents.Contains(a))
                    {
                        try { a.SetMaximumSpeedLimit(SpeedBoostMultiplier, false); if (ShowContour) SafeSetContourColor(a, Convert.ToUInt32(ContourColor, 16), true); } catch { }
                        boostedAgents.Add(a);
                    }
                }
            };

            void Cleanup()
            {
                foreach (var a in boostedAgents)
                {
                    try { a?.SetMaximumSpeedLimit(-1f, false); if (ShowContour) SafeSetContourColor(a, null, false); } catch { }
                }
                boostedAgents.Clear();
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Battle Cry Aura: allies within {Radius:0.#}m get {SpeedBoostMultiplier:0.##}x speed";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 24. BLOOD RAGE
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Blood Rage"),
     Description("Each kill adds a damage bonus stack that decays over time, orange-red contour on hero"),
     UsedImplicitly]
    public class BloodRagePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Damage Bonus Per Stack (%)"), Description("Extra damage percentage added per stack of this effect."), UsedImplicitly] public float DamageBonusPerStack { get; set; } = 10f;
        [DisplayName("Max Stacks"), Description("Maximum number of times this effect can stack on the same target."), UsedImplicitly] public int MaxStacks { get; set; } = 8;
        [DisplayName("Stack Duration (ticks)"), Description("How many ticks each stack of this effect lasts before expiring."), UsedImplicitly] public int StackDurationTicks { get; set; } = 5;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FFFF4400";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float DamageBonusPerStack = PowerProgression.ScaleFloat(this, hero, nameof(this.DamageBonusPerStack), this.DamageBonusPerStack);
            int MaxStacks = PowerProgression.ScaleInt(this, hero, nameof(this.MaxStacks), this.MaxStacks);
            int stacks = 0;
            int ticksRemaining = 0;

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;
                // Kill detection: victim dies from this blow
                if (attacker == heroAgent && victim != null && victim.IsEnemyOf(heroAgent)
                    && victim.Health - blowParams.blow.InflictedDamage <= 0f)
                {
                    stacks = Math.Min(stacks + 1, MaxStacks);
                    ticksRemaining = StackDurationTicks;
                    if (ShowContour) try { SafeSetContourColor(heroAgent, Convert.ToUInt32(ContourColor, 16), true); } catch { }
                    Log.ShowInformation($"BLOOD RAGE! {hero.Name} — {stacks} stacks ({stacks * DamageBonusPerStack:0}% bonus dmg)", hero.CharacterObject);
                }
                // Apply damage bonus
                if (stacks > 0 && attacker == heroAgent)
                {
                    float mult = 1f + stacks * DamageBonusPerStack / 100f;
                    blowParams.blow.BaseMagnitude *= mult;
                    blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * mult);
                }
            };

            handlers.OnSlowTick += dt =>
            {
                if (stacks <= 0) return;
                ticksRemaining--;
                if (ticksRemaining <= 0)
                {
                    stacks = Math.Max(0, stacks - 1);
                    ticksRemaining = stacks > 0 ? StackDurationTicks : 0;
                    if (stacks == 0)
                    {
                        var heroAgent = hero.GetAgent();
                        if (ShowContour) try { SafeSetContourColor(heroAgent, null, false); } catch { }
                    }
                }
            };

            void Cleanup()
            {
                stacks = 0;
                var heroAgent = hero.GetAgent();
                if (ShowContour) try { SafeSetContourColor(heroAgent, null, false); } catch { }
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Blood Rage: +{DamageBonusPerStack}% dmg per kill stack (max {MaxStacks})";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 25. VENGEANCE
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Vengeance"),
     Description("When hero takes heavy damage, instantly counter-attacks the attacker"),
     UsedImplicitly]
    public class VengeancePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Trigger Damage Threshold"), Description("Minimum damage that must be dealt/received to trigger this effect."), UsedImplicitly] public int TriggerDamageThreshold { get; set; } = 20;
        [DisplayName("Counter Damage Multiplier"), Description("Multiplier applied to damage dealt back at an attacker."), UsedImplicitly] public float CounterDamageMultiplier { get; set; } = 1.5f;
        [DisplayName("Cooldown (seconds)"), Description("Minimum time (in seconds) between activations of this effect."), UsedImplicitly] public float CooldownSeconds { get; set; } = 5f;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float lastTrigger = -999f;

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || victim != heroAgent) return;
                if (blowParams.blow.InflictedDamage < TriggerDamageThreshold) return;

                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastTrigger < CooldownSeconds) return;
                if (attacker == null || !attacker.IsActive()) return;
                lastTrigger = now;

                int counterDmg = (int)(blowParams.blow.InflictedDamage * CounterDamageMultiplier);
                try
                {
                    var dir = (attacker.Position - heroAgent.Position);
                    if (dir.Length > 0.01f) dir = dir.NormalizedCopy();
                    var blow = new Blow(heroAgent.Index)
                    {
                        AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Blunt,
                        BoneIndex = attacker.Monster.HeadLookDirectionBoneIndex, GlobalPosition = attacker.Position,
                        BaseMagnitude = counterDmg, InflictedDamage = counterDmg,
                        SwingDirection = dir, Direction = dir, DamageCalculated = true,
                        VictimBodyPart = BoneBodyPartType.Chest,
                    };
                    attacker.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, attacker, blow));
                    Log.ShowInformation($"VENGEANCE! {hero.Name} counters for {counterDmg} dmg!", hero.CharacterObject);
                }
                catch { }
            };
        }

        public override LocString Description =>
            $"Vengeance: counter-attack {CounterDamageMultiplier:0.#}x damage when hit for {TriggerDamageThreshold}+";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 26. SHADOWSTEP
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Shadowstep"),
     Description("Periodically teleports hero behind the nearest enemy"),
     UsedImplicitly]
    public class ShadowstepPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Cooldown (seconds)"), Description("Minimum time (in seconds) between activations of this effect."), UsedImplicitly] public float CooldownSeconds { get; set; } = 10f;
        [DisplayName("Max Range"), Description("Maximum range in meters this effect can reach."), UsedImplicitly] public float MaxRange { get; set; } = 30f;
        [DisplayName("Behind Distance"), Description("Distance behind the target used for positioning this effect."), UsedImplicitly] public float BehindDistance { get; set; } = 1.5f;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float MaxRange = PowerProgression.ScaleFloat(this, hero, nameof(this.MaxRange), this.MaxRange);
            float lastStep = -999f;

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastStep < CooldownSeconds) return;

                var target = Mission.Current?.Agents
                    ?.Where(a => a != null && a.IsActive() && a.IsEnemyOf(heroAgent)
                                 && a.Position.Distance(heroAgent.Position) <= MaxRange)
                    .OrderBy(a => a.Position.Distance(heroAgent.Position))
                    .FirstOrDefault();
                if (target == null) return;

                lastStep = now;
                try
                {
                    Vec3 facing = (target.Position - heroAgent.Position).NormalizedCopy();
                    Vec3 behindPos = target.Position - facing * BehindDistance;
                    behindPos.z = target.Position.z;
                    heroAgent.TeleportToPosition(behindPos);
                    Log.ShowInformation($"SHADOWSTEP! {hero.Name} appears behind {target.Name}!", hero.CharacterObject);
                }
                catch { }
            };
        }

        public override LocString Description =>
            $"Shadowstep: teleport behind nearest enemy every {CooldownSeconds:0}s";
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 28. IRON SKIN
    // ══════════════════════════════════════════════════════════════════════════════

    [DisplayName("Iron Skin"),
     Description("Periodically activates a damage reduction shield, silver contour on hero"),
     UsedImplicitly]
    public class IronSkinPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Damage Reduction (%)"), Description("Percentage reduction applied to incoming damage while this effect is active."), UsedImplicitly] public float DamageReductionPercent { get; set; } = 50f;
        [DisplayName("Active Duration (seconds)"), Description("How long (in seconds) this effect stays active once triggered."), UsedImplicitly] public float ActiveDurationSeconds { get; set; } = 4f;
        [DisplayName("Cooldown (seconds)"), Description("Minimum time (in seconds) between activations of this effect."), UsedImplicitly] public float CooldownSeconds { get; set; } = 15f;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FFC0C0C0";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float DamageReductionPercent = PowerProgression.ScaleFloat(this, hero, nameof(this.DamageReductionPercent), this.DamageReductionPercent);
            float ActiveDurationSeconds = PowerProgression.ScaleFloat(this, hero, nameof(this.ActiveDurationSeconds), this.ActiveDurationSeconds);
            float activeUntil = -1f;
            float nextActivation = 0f;

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;
                float now = Mission.Current?.CurrentTime ?? 0f;

                if (activeUntil > 0f && now >= activeUntil)
                {
                    activeUntil = -1f;
                    nextActivation = now + CooldownSeconds;
                    if (ShowContour) try { SafeSetContourColor(heroAgent, null, false); } catch { }
                }

                if (activeUntil < 0f && now >= nextActivation)
                {
                    activeUntil = now + ActiveDurationSeconds;
                    if (ShowContour) try { SafeSetContourColor(heroAgent, Convert.ToUInt32(ContourColor, 16), true); } catch { }
                    Log.ShowInformation($"IRON SKIN! {hero.Name} hardens for {ActiveDurationSeconds:0}s!", hero.CharacterObject);
                }
            };

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || victim != heroAgent) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now >= activeUntil) return;
                float mult = 1f - DamageReductionPercent / 100f;
                blowParams.blow.BaseMagnitude *= mult;
                blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * mult);
            };

            void Cleanup()
            {
                var heroAgent = hero.GetAgent();
                if (ShowContour) try { SafeSetContourColor(heroAgent, null, false); } catch { }
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Iron Skin: {DamageReductionPercent}% dmg reduction for {ActiveDurationSeconds:0}s every {CooldownSeconds:0}s";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Thunder Strike — hits stun nearby enemies with lightning flash
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Thunder Strike"),
     Description("Each hit deals bonus electric damage and stuns the target briefly"),
     UsedImplicitly]
    public class ThunderStrikePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Bonus Damage"), Description("Extra flat damage added by this effect."), UsedImplicitly] public int BonusDamage { get; set; } = 20;
        [DisplayName("Stun Duration (seconds)"), Description("How long (in seconds) the target is stunned."), UsedImplicitly] public float StunDuration { get; set; } = 1.5f;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FFFFFF00";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFFFFFF00; }

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || attacker != heroAgent || victim == null || !victim.IsActive()) return;
                try
                {
                    if (ShowContour) SafeSetContourColor(victim, color, true);
                    victim.SetMaximumSpeedLimit(0f, false);
                    float until = (Mission.Current?.CurrentTime ?? 0f) + StunDuration;
                    handlers.OnMissionTick += dt =>
                    {
                        if ((Mission.Current?.CurrentTime ?? 0f) >= until && victim.IsActive())
                            victim.SetMaximumSpeedLimit(-1f, false);
                    };
                }
                catch { }
            };
        }

        public override LocString Description =>
            $"Thunder Strike: stun {StunDuration:0.#}s + yellow flash on hit";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Shield Wall — periodic invulnerability burst
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Shield Wall"),
     Description("Every X seconds, the hero becomes invulnerable for a short duration"),
     UsedImplicitly]
    public class ShieldWallPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Cooldown (seconds)"), Description("Minimum time (in seconds) between activations of this effect."), UsedImplicitly] public float CooldownSeconds { get; set; } = 30f;
        [DisplayName("Invulnerability Duration (seconds)"), Description("How long (in seconds) the hero is immune to damage."), UsedImplicitly] public float InvulnerabilitySeconds { get; set; } = 3f;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FFC0C0C0";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFFC0C0C0; }

            float lastActivation = -999f;
            bool active = false;
            float activeUntil = -1f;

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                float now = Mission.Current?.CurrentTime ?? 0f;

                if (active && now >= activeUntil)
                {
                    active = false;
                    if (ShowContour) try { SafeSetContourColor(heroAgent, null, false); } catch { }
                }

                if (!active && now - lastActivation >= CooldownSeconds)
                {
                    lastActivation = now;
                    activeUntil = now + InvulnerabilitySeconds;
                    active = true;
                    if (ShowContour) try { SafeSetContourColor(heroAgent, color, true); } catch { }
                }

                if (active)
                {
                    try { heroAgent.Health = Math.Max(heroAgent.Health, heroAgent.HealthLimit * 0.01f + 1f); } catch { }
                }
            };

            void Cleanup()
            {
                var heroAgent = hero.GetAgent();
                if (ShowContour) try { SafeSetContourColor(heroAgent, null, false); } catch { }
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Shield Wall: invulnerable for {InvulnerabilitySeconds:0.#}s every {CooldownSeconds:0.#}s";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Second Wind — auto-heal when HP drops below threshold
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Second Wind"),
     Description("When HP drops below threshold, automatically heals the hero once per battle"),
     UsedImplicitly]
    public class SecondWindPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("HP Trigger Threshold (%)"), Description("Triggers once the hero's HP drops to or below this percent of max."), UsedImplicitly] public float TriggerPercent { get; set; } = 25f;
        [DisplayName("Heal Amount (%)"), Description("Amount healed, as a percentage of max HP."), UsedImplicitly] public float HealPercent { get; set; } = 50f;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FF00FF88";
        [DisplayName("Contour Duration (seconds)"), Description("How long (in seconds) the visual outline stays visible."), UsedImplicitly] public float ContourDuration { get; set; } = 3f;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFF00FF88; }

            float HealPercent = PowerProgression.ScaleFloat(this, hero, nameof(this.HealPercent), this.HealPercent);
            bool used = false;
            float contourUntil = -1f;

            handlers.OnMissionTick += dt =>
            {
                if (used) return;
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                float now = Mission.Current?.CurrentTime ?? 0f;

                if (contourUntil > 0f && now >= contourUntil)
                {
                    contourUntil = -1f;
                    if (ShowContour) try { SafeSetContourColor(heroAgent, null, false); } catch { }
                }

                float hpPct = heroAgent.Health / heroAgent.HealthLimit * 100f;
                if (hpPct <= TriggerPercent)
                {
                    used = true;
                    float healAmount = heroAgent.HealthLimit * HealPercent / 100f;
                    heroAgent.Health = Math.Min(heroAgent.Health + healAmount, heroAgent.HealthLimit);
                    contourUntil = now + ContourDuration;
                    if (ShowContour) try { SafeSetContourColor(heroAgent, color, true); } catch { }
                    Log.ShowInformation($"{hero.Name}: Second Wind!", hero.CharacterObject);
                }
            };

            void Cleanup()
            {
                var heroAgent = hero.GetAgent();
                if (ShowContour) try { SafeSetContourColor(heroAgent, null, false); } catch { }
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Second Wind: heals {HealPercent:0}% HP once when below {TriggerPercent:0}% HP";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Dodge — periodic chance to negate incoming damage
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Dodge"),
     Description("Gives the hero a chance to completely dodge incoming attacks"),
     UsedImplicitly]
    public class DodgePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Dodge Chance (%)"), Description("Chance to avoid an incoming hit entirely."), UsedImplicitly] public float DodgeChancePercent { get; set; } = 20f;
        [DisplayName("Cooldown Between Dodges (seconds)"), Description("Minimum time (in seconds) between dodge triggers."), UsedImplicitly] public float CooldownSeconds { get; set; } = 5f;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FF00CFFF";
        [DisplayName("Contour Duration (seconds)"), Description("How long (in seconds) the visual outline stays visible."), UsedImplicitly] public float ContourDuration { get; set; } = 0.5f;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFF00CFFF; }

            float DodgeChancePercent = PowerProgression.ScaleFloat(this, hero, nameof(this.DodgeChancePercent), this.DodgeChancePercent);
            float lastDodge = -999f;
            float contourUntil = -1f;
            var rng = new Random();

            handlers.OnTakeDamage += (victim, attacker, blowParams) =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || victim != heroAgent) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastDodge < CooldownSeconds) return;
                if (rng.NextDouble() * 100.0 < DodgeChancePercent)
                {
                    lastDodge = now;
                    contourUntil = now + ContourDuration;
                    blowParams.blow.InflictedDamage = 0;
                    if (ShowContour) try { SafeSetContourColor(heroAgent, color, true); } catch { }
                }
            };

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (contourUntil > 0f && now >= contourUntil)
                {
                    contourUntil = -1f;
                    if (ShowContour) try { SafeSetContourColor(heroAgent, null, false); } catch { }
                }
            };

            void Cleanup()
            {
                var heroAgent = hero.GetAgent();
                if (ShowContour) try { SafeSetContourColor(heroAgent, null, false); } catch { }
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Dodge: {DodgeChancePercent:0}% chance to negate hit (cooldown {CooldownSeconds:0.#}s)";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Backstab — bonus damage when hitting from behind
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Backstab"),
     Description("Deals massive bonus damage when attacking from behind"),
     UsedImplicitly]
    public class BackstabPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Bonus Damage Multiplier (%)"), Description("Multiplier applied to damage dealt while this effect's condition is met."), UsedImplicitly] public float BonusPercent { get; set; } = 100f;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FF440044";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFF440044; }
            float BonusPercent = PowerProgression.ScaleFloat(this, hero, nameof(this.BonusPercent), this.BonusPercent);

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || attacker != heroAgent || victim == null || !victim.IsActive()) return;
                try
                {
                    var heroForward = heroAgent.Frame.rotation.f;
                    var toVictim = (victim.Position - heroAgent.Position).NormalizedCopy();
                    float dot = Vec3.DotProduct(heroForward, toVictim);
                    if (dot < -0.3f)
                    {
                        float mult = 1f + BonusPercent / 100f;
                        blowParams.blow.BaseMagnitude *= mult;
                        blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * mult);
                        if (ShowContour) SafeSetContourColor(victim, color, true);
                    }
                }
                catch { }
            };
        }

        public override LocString Description =>
            $"Backstab: +{BonusPercent:0}% bonus damage when attacking from behind";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Execute — bonus damage on low HP targets
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Execute"),
     Description("Deals massive bonus damage to targets below HP threshold"),
     UsedImplicitly]
    public class ExecutePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Target HP Threshold (%)"), Description("Only applies to targets at or below this HP percentage."), UsedImplicitly] public float ThresholdPercent { get; set; } = 20f;
        [DisplayName("Bonus Damage Multiplier (%)"), Description("Multiplier applied to damage dealt while this effect's condition is met."), UsedImplicitly] public float BonusPercent { get; set; } = 150f;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FFAA0000";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFFAA0000; }

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || attacker != heroAgent || victim == null || !victim.IsActive()) return;
                try
                {
                    float hpPct = victim.Health / victim.HealthLimit * 100f;
                    if (hpPct <= ThresholdPercent)
                    {
                        float mult = 1f + BonusPercent / 100f;
                        blowParams.blow.BaseMagnitude *= mult;
                        blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * mult);
                        if (ShowContour) SafeSetContourColor(victim, color, true);
                    }
                }
                catch { }
            };
        }

        public override LocString Description =>
            $"Execute: +{BonusPercent:0}% dmg on targets below {ThresholdPercent:0}% HP";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Shockwave — periodic knockback burst around the hero
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Shockwave"),
     Description("Periodically releases a shockwave that knocks back nearby enemies"),
     UsedImplicitly]
    public class ShockwavePower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Radius (meters)"), Description("Effect radius in meters."), UsedImplicitly] public float Radius { get; set; } = 6f;
        [DisplayName("Knockback Force"), Description("How strongly hit targets are pushed back."), UsedImplicitly] public float KnockbackForce { get; set; } = 8f;
        [DisplayName("Damage"), Description("Damage dealt by this effect."), UsedImplicitly] public int Damage { get; set; } = 15;
        [DisplayName("Cooldown (seconds)"), Description("Minimum time (in seconds) between activations of this effect."), UsedImplicitly] public float CooldownSeconds { get; set; } = 12f;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FF8800FF";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFF8800FF; }

            float Radius = PowerProgression.ScaleFloat(this, hero, nameof(this.Radius), this.Radius);
            float lastShockwave = -999f;

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastShockwave < CooldownSeconds) return;
                lastShockwave = now;

                try
                {
                    if (ShowContour) SafeSetContourColor(heroAgent, color, true);
                    var pos = heroAgent.Position;
                    foreach (var a in Mission.Current.Agents.ToList())
                    {
                        if (a == heroAgent || !a.IsActive() || a.IsEnemyOf(heroAgent) == false) continue;
                        if (a.Position.Distance(pos) > Radius) continue;
                        try
                        {
                            var dir = (a.Position - pos).NormalizedCopy();
                            if (ShowContour) SafeSetContourColor(a, color, true);
                        }
                        catch { }
                    }
                }
                catch { }
            };

            void Cleanup()
            {
                var heroAgent = hero.GetAgent();
                if (ShowContour) try { SafeSetContourColor(heroAgent, null, false); } catch { }
            }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Shockwave: knockback + {Damage} dmg in {Radius:0.#}m radius every {CooldownSeconds:0.#}s";
    }

    // ══════════════════════════════════════════════════════════════════════
    // War Banner — allies in range get morale/speed boost
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("War Banner"),
     Description("Allies within radius gain a speed and morale boost while hero is alive"),
     UsedImplicitly]
    public class WarBannerPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Aura Radius (meters)"), Description("How far from the hero (in meters) this aura reaches."), UsedImplicitly] public float Radius { get; set; } = 12f;
        [DisplayName("Speed Multiplier"), Description("Multiplier applied to movement speed (1.0 = no change)."), UsedImplicitly] public float SpeedMult { get; set; } = 1.2f;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FFFFDD00";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFFFFDD00; }
            float Radius = PowerProgression.ScaleFloat(this, hero, nameof(this.Radius), this.Radius);
            float SpeedMult = PowerProgression.ScaleFloat(this, hero, nameof(this.SpeedMult), this.SpeedMult);
            var boosted = new HashSet<Agent>();

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) { return; }

                var inRange = Mission.Current?.Agents
                    ?.Where(a => a != null && a.IsActive() && !a.IsEnemyOf(heroAgent) && a != heroAgent
                                 && a.Position.Distance(heroAgent.Position) <= Radius)
                    .ToHashSet() ?? new HashSet<Agent>();

                foreach (var a in boosted.ToList())
                {
                    if (a == null || !a.IsActive() || !inRange.Contains(a))
                    {
                        try { a?.SetMaximumSpeedLimit(-1f, false); if (ShowContour) SafeSetContourColor(a, null, false); } catch { }
                        boosted.Remove(a);
                    }
                }
                foreach (var a in inRange)
                {
                    if (!a.HasMount && !boosted.Contains(a))
                    {
                        try { a.SetMaximumSpeedLimit(SpeedMult, false); if (ShowContour) SafeSetContourColor(a, color, true); } catch { }
                        boosted.Add(a);
                    }
                }
            };

            void Cleanup() { foreach (var a in boosted) { try { a?.SetMaximumSpeedLimit(-1f, false); if (ShowContour) SafeSetContourColor(a, null, false); } catch { } } boosted.Clear(); }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"War Banner: allies within {Radius:0.#}m get {SpeedMult:0.##}x speed";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Mark Target — enemies near hero take bonus damage from all sources
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Mark Target"),
     Description("Enemies near the hero are marked — all allies deal bonus damage to them"),
     UsedImplicitly]
    public class MarkTargetPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Mark Radius (meters)"), Description("How far from the hero (in meters) enemies get marked by this effect."), UsedImplicitly] public float Radius { get; set; } = 8f;
        [DisplayName("Bonus Damage Multiplier (%)"), Description("Multiplier applied to damage dealt while this effect's condition is met."), UsedImplicitly] public float BonusPercent { get; set; } = 30f;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FFFF4400";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFFFF4400; }
            float Radius = PowerProgression.ScaleFloat(this, hero, nameof(this.Radius), this.Radius);
            float BonusPercent = PowerProgression.ScaleFloat(this, hero, nameof(this.BonusPercent), this.BonusPercent);
            var marked = new HashSet<Agent>();

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;

                var inRange = Mission.Current?.Agents
                    ?.Where(a => a != null && a.IsActive() && a.IsEnemyOf(heroAgent)
                                 && a.Position.Distance(heroAgent.Position) <= Radius)
                    .ToHashSet() ?? new HashSet<Agent>();

                foreach (var a in marked.ToList())
                {
                    if (a == null || !a.IsActive() || !inRange.Contains(a))
                    { if (ShowContour) try { SafeSetContourColor(a, null, false); } catch { } marked.Remove(a); }
                }
                foreach (var a in inRange)
                {
                    if (!marked.Contains(a))
                    { if (ShowContour) try { SafeSetContourColor(a, color, true); } catch { } marked.Add(a); }
                }
            };

            handlers.OnDoDamage += (attacker, victim, blowParams) =>
            {
                if (victim == null || !marked.Contains(victim)) return;
                float mult = 1f + BonusPercent / 100f;
                blowParams.blow.BaseMagnitude *= mult;
                blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * mult);
            };

            void Cleanup() { foreach (var a in marked) { if (ShowContour) try { SafeSetContourColor(a, null, false); } catch { } } marked.Clear(); }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Mark Target: enemies within {Radius:0.#}m take +{BonusPercent:0}% damage from all";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Rallying Cry — one-time burst heal for all nearby allies
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Rallying Cry"),
     Description("Once per battle, heals all friendly units in radius when hero HP drops below threshold"),
     UsedImplicitly]
    public class RallyingCryPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Heal Amount"), Description("Flat HP restored."), UsedImplicitly] public int HealAmount { get; set; } = 40;
        [DisplayName("Radius (meters)"), Description("Effect radius in meters."), UsedImplicitly] public float Radius { get; set; } = 15f;
        [DisplayName("HP Trigger Threshold (%)"), Description("Triggers once the hero's HP drops to or below this percent of max."), UsedImplicitly] public float TriggerPercent { get; set; } = 30f;
        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FF00FF44";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFF00FF44; }
            int HealAmount = PowerProgression.ScaleInt(this, hero, nameof(this.HealAmount), this.HealAmount);
            float Radius = PowerProgression.ScaleFloat(this, hero, nameof(this.Radius), this.Radius);
            bool used = false;

            handlers.OnMissionTick += dt =>
            {
                if (used) return;
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                float hpPct = heroAgent.Health / heroAgent.HealthLimit * 100f;
                if (hpPct > TriggerPercent) return;

                used = true;
                Log.ShowInformation($"RALLYING CRY! {hero.Name} rallies the troops!", hero.CharacterObject);
                if (ShowContour) try { SafeSetContourColor(heroAgent, color, true); } catch { }

                foreach (var a in Mission.Current?.Agents?.ToList() ?? new List<Agent>())
                {
                    if (a == null || !a.IsActive() || a.IsEnemyOf(heroAgent)) continue;
                    if (a.Position.Distance(heroAgent.Position) > Radius) continue;
                    try
                    {
                        a.Health = Math.Min(a.Health + HealAmount, a.HealthLimit);
                        if (ShowContour) SafeSetContourColor(a, color, true);
                    }
                    catch { }
                }
            };
        }

        public override LocString Description =>
            $"Rallying Cry: heals all allies {HealAmount}HP in {Radius:0.#}m when below {TriggerPercent:0}% HP (once)";
    }

    // ══════════════════════════════════════════════════════════════════════
    // UNIFIED AURA — consolidates Taunt/Damage/Curse/Fear/Slow/Weakness/
    // Shockwave/Mark Target/Heal/Buff/BattleCry/Rallying Cry into ONE
    // configurable power (pick a Target side + an Effect type). Per the TOR
    // Discord discussion (Sawtooth44/Randomchair22): one modular base with a
    // property-style effect picker beats hunting through 10+ near-identical
    // aura classes. The old individual classes are left untouched (existing
    // configs referencing them keep working) - this is purely additive.
    // ══════════════════════════════════════════════════════════════════════

    // Plain-string ItemsSource dropdowns for AuraPower.Target/Effect below - gives BLTConfigure a
    // proper picker without going back to real C# enums (kept as strings for YAML compatibility).
    public class AuraTargetItemsSource : IItemsSource
    {
        public ItemCollection GetValues() => new ItemCollection { { "Enemies", "Enemies" }, { "Allies", "Allies" } };
    }
    public class AuraEffectItemsSource : IItemsSource
    {
        public ItemCollection GetValues() => new ItemCollection
        {
            { "Taunt", "Taunt" }, { "Damage", "Damage" }, { "Curse", "Curse" }, { "Fear", "Fear" },
            { "Slow", "Slow" }, { "Weakness", "Weakness" }, { "Shockwave", "Shockwave" }, { "MarkTarget", "MarkTarget" },
            { "Heal", "Heal" }, { "Buff", "Buff" }, { "BattleCry", "BattleCry" }, { "RallyingCry", "RallyingCry" },
        };
    }

    [DisplayName("Aura"),
     Description("Unified, configurable aura: pick a target side (Enemies/Allies) and an effect type"),
     UsedImplicitly]
    public class AuraPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Target"), Description("Who the aura affects: Enemies or Allies"),
         ItemsSource(typeof(AuraTargetItemsSource)), UsedImplicitly]
        public string Target { get; set; } = "Enemies";

        [DisplayName("Effect"), Description("What the aura does"),
         ItemsSource(typeof(AuraEffectItemsSource)), UsedImplicitly]
        public string Effect { get; set; } = "Damage";

        [DisplayName("Range (meters)"), Description("Effect range in meters."), UsedImplicitly] public float Range { get; set; } = 8f;
        [DisplayName("Tick Interval (seconds)"), Description("How often (in seconds) this effect re-evaluates and re-applies to nearby agents."), UsedImplicitly] public float TickIntervalSeconds { get; set; } = 3f;
        [DisplayName("Max Agents Per Tick"), Description("Caps how many nearby agents this effect can hit per tick, closest first - keeps performance in check in large battles."), UsedImplicitly] public int MaxAgentsPerTick { get; set; } = 8;

        [DisplayName("Magnitude"), UsedImplicitly,
         Description("Meaning depends on Effect: Damage/Heal/Shockwave/RallyingCry=HP, Curse/Fear/Slow/Weakness/Buff/BattleCry=% stat modifier, MarkTarget=bonus damage %")]
        public float Magnitude { get; set; } = 20f;

        [DisplayName("RallyingCry HP Trigger (%)"), Description("Only used when Effect=RallyingCry: triggers once when the hero's own HP drops to/below this."), UsedImplicitly]
        public float TriggerHpPercent { get; set; } = 30f;

        [DisplayName("Show Contour"), Description("Highlights affected agents with a colored outline while the effect is active - visual only, no gameplay effect."), UsedImplicitly] public bool ShowContour { get; set; } = true;
        [DisplayName("Contour Color (hex AARRGGBB)"), Description("Outline color shown when Show Contour is on, as an 8-digit hex code: AA=alpha, RR=red, GG=green, BB=blue (e.g. FFFF0000 = solid red)."), UsedImplicitly] public string ContourColor { get; set; } = "FFFFAA00";

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float range = PowerProgression.ScaleFloat(this, hero, nameof(Range), Range);
            float tickInterval = PowerProgression.ScaleFloat(this, hero, nameof(TickIntervalSeconds), TickIntervalSeconds);
            int maxAgents = PowerProgression.ScaleInt(this, hero, nameof(MaxAgentsPerTick), MaxAgentsPerTick);
            float magnitude = PowerProgression.ScaleFloat(this, hero, nameof(Magnitude), Magnitude);
            uint color = 0;
            try { color = Convert.ToUInt32(ContourColor, 16); } catch { color = 0xFFFFAA00; }

            // Rallying Cry is a one-shot threshold trigger, not a continuous tick aura - handled separately.
            if (Effect == "RallyingCry")
            {
                bool used = false;
                handlers.OnMissionTick += dt =>
                {
                    if (used) return;
                    var heroAgent = hero.GetAgent();
                    if (heroAgent == null || !heroAgent.IsActive() || heroAgent.HealthLimit <= 0f) return;
                    float hpPct = heroAgent.Health / heroAgent.HealthLimit * 100f;
                    if (hpPct > TriggerHpPercent) return;
                    used = true;
                    Log.ShowInformation($"RALLYING CRY! {hero.Name} rallies the troops!", hero.CharacterObject);
                    foreach (var a in Mission.Current?.Agents?.ToList() ?? new List<Agent>())
                    {
                        if (a == null || !a.IsActive() || a.IsMount || a.IsEnemyOf(heroAgent)) continue;
                        if (a.Position.Distance(heroAgent.Position) > range) continue;
                        try { a.Health = Math.Min(a.Health + magnitude, a.HealthLimit); if (ShowContour) SafeSetContourColor(a, color, true); }
                        catch { }
                    }
                };
                return;
            }

            // Shockwave is a periodic burst on cooldown, not a sustained per-agent-in-range effect.
            if (Effect == "Shockwave")
            {
                float lastBurst = -999f;
                handlers.OnMissionTick += dt =>
                {
                    var heroAgent = hero.GetAgent();
                    if (heroAgent == null || !heroAgent.IsActive()) return;
                    float now = Mission.Current?.CurrentTime ?? 0f;
                    if (now - lastBurst < tickInterval) return;
                    lastBurst = now;
                    var nearbyBuffer = new MBList<Agent>();
                    var targets = Mission.Current.GetNearbyAgents(heroAgent.Position.AsVec2, range, nearbyBuffer)
                        .Where(a => a.IsActive() && !a.IsMount && a != heroAgent
                            && (Target == "Enemies" ? a.IsEnemyOf(heroAgent) : a.IsFriendOf(heroAgent)))
                        .OrderBy(a => a.Position.Distance(heroAgent.Position))
                        .Take(Math.Max(1, maxAgents));
                    foreach (var a in targets)
                    {
                        try
                        {
                            var dir = Vec3.Up;
                            var blow = new Blow(heroAgent.Index)
                            {
                                AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Blunt,
                                BoneIndex = a.Monster.HeadLookDirectionBoneIndex, GlobalPosition = a.Position,
                                BaseMagnitude = magnitude, InflictedDamage = (int)magnitude,
                                SwingDirection = dir, Direction = dir, DamageCalculated = true,
                                VictimBodyPart = BoneBodyPartType.Chest,
                            };
                            a.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, a, blow));
                            if (ShowContour) SafeSetContourColor(a, color, true);
                        }
                        catch { }
                    }
                };
                return;
            }

            // Mark Target amplifies damage dealt BY ALLIES to marked enemies - needs OnDoDamage, not a per-tick effect.
            if (Effect == "MarkTarget")
            {
                var marked = new HashSet<Agent>();
                handlers.OnMissionTick += dt =>
                {
                    var heroAgent = hero.GetAgent();
                    if (heroAgent == null || !heroAgent.IsActive()) return;
                    var inRange = new HashSet<Agent>(Mission.Current?.Agents
                        ?.Where(a => a != null && a.IsActive() && a.IsEnemyOf(heroAgent)
                                     && a.Position.Distance(heroAgent.Position) <= range) ?? Enumerable.Empty<Agent>());
                    foreach (var a in marked.ToList())
                        if (!inRange.Contains(a)) { if (ShowContour) try { SafeSetContourColor(a, null, false); } catch { } marked.Remove(a); }
                    foreach (var a in inRange)
                        if (!marked.Contains(a)) { if (ShowContour) try { SafeSetContourColor(a, color, true); } catch { } marked.Add(a); }
                };
                handlers.OnDoDamage += (attacker, victim, blowParams) =>
                {
                    if (victim == null || !marked.Contains(victim)) return;
                    float mult = 1f + magnitude / 100f;
                    blowParams.blow.BaseMagnitude *= mult;
                    blowParams.blow.InflictedDamage = (int)(blowParams.blow.InflictedDamage * mult);
                };
                void CleanupMark() { foreach (var a in marked) { if (ShowContour) try { SafeSetContourColor(a, null, false); } catch { } } marked.Clear(); }
                if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => CleanupMark();
                handlers.OnMissionOver += CleanupMark;
                return;
            }

            // Everything else (Taunt/Damage/Curse/Fear/Slow/Weakness/Heal/Buff/BattleCry) shares the
            // same "find N nearest targets in range, apply on entry, revert on exit" shape.
            AgentModifierConfig BuildConfig()
            {
                var c = new AgentModifierConfig();
                switch (Effect)
                {
                    case "Curse":
                        c.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.SwingSpeedMultiplier, ModifierPercent = 100f - magnitude });
                        break;
                    case "Fear":
                        c.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.SwingSpeedMultiplier, ModifierPercent = 100f - magnitude });
                        c.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ThrustOrRangedReadySpeedMultiplier, ModifierPercent = 100f - magnitude });
                        break;
                    case "Weakness":
                        c.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ArmorHead, ModifierPercent = 100f - magnitude });
                        break;
                    case "Buff":
                        c.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.SwingSpeedMultiplier, ModifierPercent = 100f + magnitude });
                        c.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ArmorHead, ModifierPercent = 100f + magnitude });
                        break;
                }
                return c;
            }
            AgentModifierConfig Negate(AgentModifierConfig cfg)
            {
                var n = new AgentModifierConfig();
                foreach (var p in cfg.Properties)
                    n.Properties.Add(new PropertyModifierDef { Name = p.Name, ModifierPercent = p.ModifierPercent > 0f ? 10000f / p.ModifierPercent : 100f });
                return n;
            }

            void ApplyEffect(Agent a)
            {
                try
                {
                    switch (Effect)
                    {
                        case "Taunt": a.SetTargetAgent(hero.GetAgent()); break;
                        case "Curse": a.SetMaximumSpeedLimit(1f - magnitude / 100f, true); BLTAgentModifierBehavior.Current?.Add(a, BuildConfig()); break;
                        case "Slow": a.SetMaximumSpeedLimit(1f - magnitude / 100f, true); break;
                        case "BattleCry": a.SetMaximumSpeedLimit(1f + magnitude / 100f, true); break;
                        case "Fear": case "Weakness": case "Buff":
                            BLTAgentModifierBehavior.Current?.Add(a, BuildConfig()); break;
                    }
                }
                catch { }
            }
            void RemoveEffect(Agent a)
            {
                if (a == null || !a.IsActive()) return;
                try
                {
                    switch (Effect)
                    {
                        case "Curse": a.SetMaximumSpeedLimit(1f, true); BLTAgentModifierBehavior.Current?.Add(a, Negate(BuildConfig())); break;
                        case "Slow": case "BattleCry": a.SetMaximumSpeedLimit(1f, true); break;
                        case "Fear": case "Weakness": case "Buff":
                            BLTAgentModifierBehavior.Current?.Add(a, Negate(BuildConfig())); break;
                    }
                }
                catch { }
            }

            var nearbyBuf = new MBList<Agent>();
            var inAura = new HashSet<Agent>();
            float lastTick = -999f;

            handlers.OnSlowTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive())
                {
                    foreach (var a in inAura) RemoveEffect(a);
                    inAura.Clear();
                    return;
                }
                float now = Mission.Current?.CurrentTime ?? 0f;
                if (now - lastTick < tickInterval) return;
                lastTick = now;

                nearbyBuf.Clear();
                var nowIn = new HashSet<Agent>(Mission.Current
                    .GetNearbyAgents(heroAgent.Position.AsVec2, range, nearbyBuf)
                    .Where(a => a.IsActive() && !a.IsMount
                        && (Target == "Enemies" ? a.IsEnemyOf(heroAgent) : a.IsFriendOf(heroAgent)))
                    .OrderBy(a => a.Position.Distance(heroAgent.Position))
                    .Take(Math.Max(1, maxAgents)));

                foreach (var a in inAura)
                    if (!nowIn.Contains(a)) { RemoveEffect(a); if (ShowContour) try { SafeSetContourColor(a, null, false); } catch { } }

                foreach (var a in nowIn)
                {
                    if (Effect == "Damage")
                    {
                        try
                        {
                            var dir = Vec3.Up;
                            var blow = new Blow(heroAgent.Index)
                            {
                                AttackType = AgentAttackType.Standard, DamageType = DamageTypes.Pierce,
                                BoneIndex = a.Monster.HeadLookDirectionBoneIndex, GlobalPosition = a.Position,
                                BaseMagnitude = magnitude, InflictedDamage = (int)magnitude,
                                SwingDirection = dir, Direction = dir, DamageCalculated = true,
                                VictimBodyPart = BoneBodyPartType.Chest,
                            };
                            a.RegisterBlow(blow, AgentHelpers.CreateCollisionDataFromBlow(heroAgent, a, blow));
                        }
                        catch { }
                    }
                    else if (Effect == "Heal")
                    {
                        try { a.Health = Math.Min(a.Health + magnitude, a.HealthLimit); } catch { }
                    }
                    else if (Effect == "Taunt" || !inAura.Contains(a))
                    {
                        ApplyEffect(a);
                    }
                }

                if (ShowContour)
                    foreach (var a in nowIn) try { SafeSetContourColor(a, color, true); } catch { }

                inAura.Clear();
                foreach (var a in nowIn) inAura.Add(a);
            };

            void Cleanup() { foreach (var a in inAura) RemoveEffect(a); inAura.Clear(); }
            if (deactivationHandler != null) deactivationHandler.OnDeactivate += _ => Cleanup();
            handlers.OnMissionOver += Cleanup;
        }

        public override LocString Description =>
            $"Aura: {Effect} on {Target} within {Range:0}m";
    }

    // ══════════════════════════════════════════════════════════════════════
    // NECROMANCY — chance to reanimate a slain enemy troop as an ally when
    // this hero lands the kill. One-time only (no second reanimation if the
    // raised unit dies again). Never reanimates other players' BLT heroes or
    // wanderers - only ordinary troops. Kills by the raised unit pay gold to
    // the owner, tracked the same way as wanderer kills.
    // ══════════════════════════════════════════════════════════════════════

    [DisplayName("Necromancy"),
     Description("Chance to reanimate a slain enemy troop as an ally when this hero lands the kill (one-time; never other players' heroes/wanderers)"),
     UsedImplicitly]
    public class NecromancyPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Reanimate Chance (%)"), Description("Chance to reanimate a slain enemy troop as an ally when this hero lands the kill."), UsedImplicitly] public float ChancePercent { get; set; } = 15f;
        [DisplayName("Gold Per Kill (by raised unit)"), Description("Gold awarded to the owner each time the reanimated unit kills an enemy."), UsedImplicitly] public int GoldPerKill { get; set; } = 50;
        [DisplayName("Max Active Reanimated Units"), Description("Maximum number of reanimated units this hero can have alive at once."), UsedImplicitly] public int MaxActive { get; set; } = 2;
        [DisplayName("Show Message"), Description("Whether to post a chat/feed message when this effect triggers."), UsedImplicitly] public bool ShowMessage { get; set; } = true;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        // Spawning inside OnGotAKill itself crashes the game ("Collection was modified; enumeration
        // operation may not execute") - that handler fires from deep inside the engine's own
        // Mission.OnAgentRemoved dispatch, and SpawnTroop mutates the agent list mid-enumeration.
        // So: only decide/queue here, then do the actual SpawnTroop on the next OnMissionTick,
        // exactly like WandererSpawnMissionBehavior's deferred-spawn queue.
        private class PendingRaise
        {
            public Hero Hero;
            public Agent HeroAgent;
            public CharacterObject VictimChar;
            public Vec3 Position;
        }

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float chance = PowerProgression.ScaleFloat(this, hero, nameof(ChancePercent), ChancePercent);
            int goldPerKill = PowerProgression.ScaleInt(this, hero, nameof(GoldPerKill), GoldPerKill);
            int maxActive = PowerProgression.ScaleInt(this, hero, nameof(MaxActive), MaxActive);
            var raisedAgents = new HashSet<Agent>();
            var pending = new List<PendingRaise>();

            handlers.OnGotAKill += (attackerAgent, victimAgent, agentState, blow) =>
            {
                try
                {
                    if (victimAgent == null || agentState != AgentState.Killed) return;
                    if (!(victimAgent.Character is CharacterObject victimChar)) return;
                    if (victimChar.HeroObject != null) return; // never a real hero (BLT-adopted or otherwise)
                    if (BLTWandererBehavior.IsWandererStringId(victimChar.StringId)) return; // never someone's wanderer

                    raisedAgents.RemoveWhere(a => a == null || !a.IsActive());
                    if (raisedAgents.Count + pending.Count >= maxActive) return;
                    if (MBRandom.RandomFloat * 100f > chance) return;

                    var heroAgent = attackerAgent;
                    if (heroAgent == null || !heroAgent.IsActive()) return;

                    pending.Add(new PendingRaise { Hero = hero, HeroAgent = heroAgent, VictimChar = victimChar, Position = victimAgent.Position });
                }
                catch (Exception ex) { Log.Exception("NecromancyPower.OnGotAKill", ex); }
            };

            handlers.OnMissionTick += dt =>
            {
                if (pending.Count == 0) return;
                try
                {
                    foreach (var p in pending)
                    {
                        try
                        {
                            if (p.HeroAgent == null || !p.HeroAgent.IsActive()) continue;

                            PartyBase party = p.HeroAgent.Origin is TaleWorlds.CampaignSystem.AgentOrigins.PartyAgentOrigin pao ? pao.Party
                                : p.HeroAgent.Origin?.BattleCombatant as PartyBase;
                            if (party == null) continue;

                            var raised = Mission.Current.SpawnTroop(
                                new TaleWorlds.CampaignSystem.AgentOrigins.PartyAgentOrigin(party, p.VictimChar)
                                , isPlayerSide: p.HeroAgent.Team != null && p.HeroAgent.Team.Side == Mission.Current.PlayerTeam?.Side
                                , hasFormation: true
                                , spawnWithHorse: false
                                , isReinforcement: false
                                , formationTroopCount: 1
                                , formationTroopIndex: 0
                                , isAlarmed: true
                                , wieldInitialWeapons: true
                                , forceDismounted: false
                                , initialPosition: p.Position
                                , initialDirection: p.HeroAgent.GetMovementDirection()
                            );
                            if (raised == null) continue;
                            if (p.HeroAgent.Team != null && raised.Team != p.HeroAgent.Team) raised.SetTeam(p.HeroAgent.Team, true);
                            if (p.HeroAgent.Formation != null) raised.Formation = p.HeroAgent.Formation;
                            raisedAgents.Add(raised);
                            NecromancyMissionBehavior.Track(raised, p.Hero, goldPerKill);

                            if (ShowMessage) Log.ShowInformation($"Necromancy! {p.Hero.Name} reanimates a fallen {p.VictimChar.Name}!", p.Hero.CharacterObject);
                        }
                        catch (Exception ex) { Log.Exception("NecromancyPower.ProcessPendingRaise", ex); }
                    }
                    pending.Clear();
                }
                catch (Exception ex) { Log.Exception("NecromancyPower.OnMissionTick", ex); }
            };
        }

        public override LocString Description =>
            $"Necromancy: {ChancePercent:0}% chance to reanimate a slain enemy as an ally (one-time, up to {MaxActive} active)";
    }

    // Tracks kills made by reanimated Necromancy units so their owner gets gold, same pattern as
    // WandererSpawnMissionBehavior's wandererAgentToOwner (raised units aren't hero-power-gated
    // agents, so this can't go through PowerHandler - needs its own lightweight mission behavior).
    internal class NecromancyMissionBehavior : MissionBehavior
    {
        public static NecromancyMissionBehavior Current { get; private set; }
        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;
        private readonly Dictionary<Agent, (Hero owner, int goldPerKill)> raisedAgentToOwner = new Dictionary<Agent, (Hero, int)>();

        public override void OnBehaviorInitialize() { base.OnBehaviorInitialize(); Current = this; }
        public override void OnRemoveBehavior() { base.OnRemoveBehavior(); if (Current == this) Current = null; }

        public static void Track(Agent raised, Hero owner, int goldPerKill)
        {
            if (Current == null) Mission.Current?.AddMissionBehavior(new NecromancyMissionBehavior());
            if (Current != null) Current.raisedAgentToOwner[raised] = (owner, goldPerKill);
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            try
            {
                if (affectorAgent == null || agentState != AgentState.Killed) return;
                if (!raisedAgentToOwner.TryGetValue(affectorAgent, out var entry) || entry.owner == null) return;
                if (entry.goldPerKill > 0) BLTAdoptAHeroCampaignBehavior.Current?.ChangeHeroGold(entry.owner, entry.goldPerKill, true);
            }
            catch (Exception ex) { Log.Exception("NecromancyMissionBehavior.OnAgentRemoved", ex); }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Stealth — hero becomes hard to target periodically
    // ══════════════════════════════════════════════════════════════════════
    [DisplayName("Stealth"),
     Description("Periodically makes the hero invisible to enemies (they lose target)"),
     UsedImplicitly]
    public class StealthPower : DurationMissionHeroPowerDefBase, IHeroPowerPassive
    {
        [DisplayName("Stealth Duration (seconds)"), Description("How long the hero stays hidden/untargetable."), UsedImplicitly] public float StealthDuration { get; set; } = 4f;
        [DisplayName("Cooldown (seconds)"), Description("Minimum time (in seconds) between activations of this effect."), UsedImplicitly] public float CooldownSeconds { get; set; } = 20f;

        void IHeroPowerPassive.OnHeroJoinedBattle(Hero hero, PowerHandler.Handlers handlers)
            => OnActivation(hero, handlers);

        protected override void OnActivation(Hero hero, PowerHandler.Handlers handlers,
            Agent agent = null, DeactivationHandler deactivationHandler = null)
        {
            float StealthDuration = PowerProgression.ScaleFloat(this, hero, nameof(this.StealthDuration), this.StealthDuration);
            float lastActivation = -999f;
            bool stealthActive = false;
            float stealthUntil = -1f;

            handlers.OnMissionTick += dt =>
            {
                var heroAgent = hero.GetAgent();
                if (heroAgent == null || !heroAgent.IsActive()) return;
                float now = Mission.Current?.CurrentTime ?? 0f;

                if (stealthActive && now >= stealthUntil)
                {
                    stealthActive = false;
                    // break stealth on all enemies targeting this agent
                    try
                    {
                        foreach (var a in Mission.Current.Agents.ToList())
                        {
                            if (a == null || !a.IsActive() || !a.IsEnemyOf(heroAgent)) continue;
                            if (a.GetLookAgent() == heroAgent) a.ResetLookAgent();
                        }
                    }
                    catch { }
                }

                if (!stealthActive && now - lastActivation >= CooldownSeconds)
                {
                    lastActivation = now;
                    stealthUntil = now + StealthDuration;
                    stealthActive = true;
                    try
                    {
                        foreach (var a in Mission.Current.Agents.ToList())
                        {
                            if (a == null || !a.IsActive() || !a.IsEnemyOf(heroAgent)) continue;
                            if (a.GetLookAgent() == heroAgent) a.ResetLookAgent();
                        }
                    }
                    catch { }
                    Log.ShowInformation($"{hero.Name} enters stealth!", hero.CharacterObject);
                }
            };
        }

        public override LocString Description =>
            $"Stealth: invisible for {StealthDuration:0.#}s every {CooldownSeconds:0.#}s";
    }

    // ════════════════════════════════════════════════════════════════════
    //  POWER PROGRESSION — config
    // ════════════════════════════════════════════════════════════════════

    public class PowerTierDef
    {
        [DisplayName("Tier Name"), Description("Display name shown for this tier in the UI."), UsedImplicitly]
        public string Name { get; set; } = "Tier";
        [DisplayName("Kills Required"), Description("Number of kills needed to reach this tier/unlock this effect."), UsedImplicitly]
        public int KillsRequired { get; set; } = 0;
        [DisplayName("Battles Required"), Description("Number of battles needed to reach this tier/unlock this effect."), UsedImplicitly]
        public int BattlesRequired { get; set; } = 0;
        public override string ToString() => $"{Name} (K{KillsRequired}/B{BattlesRequired})";
    }

    // Bazowa sekcja progresji jednej mocy. Każda CSV-property (string) nazwana DOKŁADNIE jak
    // skalowalna właściwość mocy (np. PoisonDamagePerTick). Dopasowanie po TYPIE mocy (GetTargetType).
    public abstract class PowerProgressionSection
    {
        // Nazwa typu mocy (np. "PoisonStrikePower"). Metoda (nie property) — nie pokazuje się w UI ani nie serializuje.
        public abstract string GetTargetType();

        public static List<float> ParseCsv(string csv)
        {
            var list = new List<float>();
            if (string.IsNullOrWhiteSpace(csv)) return list;
            foreach (var part in csv.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (float.TryParse(part.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float v))
                    list.Add(v);
            return list;
        }

        public List<float> Get(string propName)
        {
            var p = GetType().GetProperty(propName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (p == null || p.PropertyType != typeof(string) || !p.CanWrite) return null;
            return ParseCsv(p.GetValue(this) as string);
        }

        public IEnumerable<KeyValuePair<string, List<float>>> AllEntries()
        {
            foreach (var p in GetType().GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (p.PropertyType != typeof(string) || !p.CanWrite) continue;
                var vals = ParseCsv(p.GetValue(this) as string);
                if (vals.Count > 0) yield return new KeyValuePair<string, List<float>>(p.Name, vals);
            }
        }
    }

    // ── Sekcje mocy MBGA ──
    public class PoisonStrikeSection : PowerProgressionSection
    {
        public override string GetTargetType() => "PoisonStrikePower";
        [DisplayName("Poison Damage Per Tick"), Description("Damage dealt each tick while poisoned."), UsedImplicitly] public string PoisonDamagePerTick { get; set; } = "";
        [DisplayName("Poison Duration Ticks"), Description("How many ticks the poison effect lasts."), UsedImplicitly] public string PoisonDurationTicks { get; set; } = "";
    }
    public class BerserkSection : PowerProgressionSection
    {
        public override string GetTargetType() => "BerserkPower";
        [DisplayName("Max Damage Bonus (%)"), Description("Highest possible damage bonus this effect can reach."), UsedImplicitly] public string MaxDamageBonusPercent { get; set; } = "";
        [DisplayName("Threshold HP (%)"), Description("Triggers once HP drops to or below this percent of max."), UsedImplicitly] public string ThresholdHpPercent { get; set; } = "";
    }
    public class LastStandSection : PowerProgressionSection
    {
        public override string GetTargetType() => "LastStandPower";
        [DisplayName("Trigger HP (%)"), Description("Triggers once HP drops to or below this percent of max."), UsedImplicitly] public string TriggerHpPercent { get; set; } = "";
        [DisplayName("Damage Bonus (%)"), Description("Percentage bonus added to damage dealt while this effect is active."), UsedImplicitly] public string DamageBonusPercent { get; set; } = "";
        [DisplayName("Damage Reduction (%)"), Description("Percentage reduction applied to incoming damage while this effect is active."), UsedImplicitly] public string DamageReductionPercent { get; set; } = "";
        [DisplayName("Duration (seconds)"), Description("How long (in seconds) this effect lasts once triggered."), UsedImplicitly] public string DurationSeconds { get; set; } = "";
    }
    public class TauntSection : PowerProgressionSection
    {
        public override string GetTargetType() => "TauntPower";
        [DisplayName("Taunt Range"), Description("How far (in meters) enemies can be forced to target this hero."), UsedImplicitly] public string TauntRange { get; set; } = "";
        [DisplayName("Max Enemies"), Description("Maximum number of enemies this effect can affect at once."), UsedImplicitly] public string MaxEnemies { get; set; } = "";
    }
    public class HealAuraSection : PowerProgressionSection
    {
        public override string GetTargetType() => "HealAuraPower";
        [DisplayName("Heal Per Tick"), Description("HP restored each tick."), UsedImplicitly] public string HealPerTick { get; set; } = "";
        [DisplayName("Heal Range"), Description("How far from the hero (in meters) allies are healed."), UsedImplicitly] public string HealRange { get; set; } = "";
    }
    public class DamageAuraSection : PowerProgressionSection
    {
        public override string GetTargetType() => "DamageAuraPower";
        [DisplayName("Damage Per Tick"), Description("Damage dealt on each tick of this effect."), UsedImplicitly] public string DamagePerTick { get; set; } = "";
        [DisplayName("Damage Range"), Description("How far from the hero this damage effect reaches."), UsedImplicitly] public string DamageRange { get; set; } = "";
        [DisplayName("Max Agents Per Tick"), Description("Caps how many nearby agents this effect can hit per tick, closest first - keeps performance in check in large battles."), UsedImplicitly] public string MaxAgentsPerTick { get; set; } = "";
    }
    public class CurseAuraSection : PowerProgressionSection
    {
        public override string GetTargetType() => "CurseAuraPower";
        [DisplayName("Aura Range"), Description("How far from the hero (in meters) this aura reaches."), UsedImplicitly] public string AuraRange { get; set; } = "";
        [DisplayName("Speed Slow (%)"), Description("Percentage reduction to movement speed."), UsedImplicitly] public string SpeedSlowPercent { get; set; } = "";
        [DisplayName("Attack Slow (%)"), Description("Percentage reduction to attack/swing speed."), UsedImplicitly] public string AttackSlowPercent { get; set; } = "";
        [DisplayName("Dismount Chance (%)"), Description("Chance the target is knocked off their mount."), UsedImplicitly] public string DismountChancePercent { get; set; } = "";
        [DisplayName("Knockdown Chance (%)"), Description("Chance the target is knocked down by this hit."), UsedImplicitly] public string KnockdownChancePercent { get; set; } = "";
        [DisplayName("Weapon Drop Chance (%)"), Description("Chance that the target drops their weapon."), UsedImplicitly] public string WeaponDropChancePercent { get; set; } = "";
    }
    public class BuffAuraSection : PowerProgressionSection
    {
        public override string GetTargetType() => "BuffAuraPower";
        [DisplayName("Aura Range"), Description("How far from the hero (in meters) this aura reaches."), UsedImplicitly] public string AuraRange { get; set; } = "";
        [DisplayName("Damage Bonus (%)"), Description("Percentage bonus added to damage dealt while this effect is active."), UsedImplicitly] public string DamageBonusPercent { get; set; } = "";
        [DisplayName("Armor Bonus (%)"), Description("Percentage bonus added to armor while this effect is active."), UsedImplicitly] public string ArmorBonusPercent { get; set; } = "";
        [DisplayName("Move Speed Bonus (%)"), Description("Percentage bonus added to movement speed."), UsedImplicitly] public string MoveSpeedBonusPercent { get; set; } = "";
        [DisplayName("Attack Speed Bonus (%)"), Description("Percentage bonus added to attack/swing speed."), UsedImplicitly] public string AttackSpeedBonusPercent { get; set; } = "";
    }
    public class TeleportSection : PowerProgressionSection
    {
        public override string GetTargetType() => "TeleportPower";
        [DisplayName("Search Range"), Description("How far to look for a valid target."), UsedImplicitly] public string SearchRange { get; set; } = "";
        [DisplayName("Splash Damage"), Description("Extra damage dealt in a small radius around the impact point."), UsedImplicitly] public string SplashDamage { get; set; } = "";
        [DisplayName("Splash Radius"), Description("Radius in meters of the splash/area effect around the impact point."), UsedImplicitly] public string SplashRadius { get; set; } = "";
    }
    public class JumpAttackSection : PowerProgressionSection
    {
        public override string GetTargetType() => "JumpAttackPower";
        [DisplayName("Cooldown (seconds)"), Description("Minimum time (in seconds) between activations of this effect."), UsedImplicitly] public string CooldownSeconds { get; set; } = "";
        [DisplayName("Detection Range"), Description("How far this effect can detect valid targets."), UsedImplicitly] public string DetectionRange { get; set; } = "";
        [DisplayName("Base Damage"), Description("Base damage dealt before any modifiers."), UsedImplicitly] public string BaseDamage { get; set; } = "";
        [DisplayName("Damage Bonus (%)"), Description("Percentage bonus added to damage dealt while this effect is active."), UsedImplicitly] public string DamageBonusPercent { get; set; } = "";
        [DisplayName("Knockback Distance"), Description("How far hit targets get pushed back."), UsedImplicitly] public string KnockbackDistance { get; set; } = "";
    }
    public class KickSection : PowerProgressionSection
    {
        public override string GetTargetType() => "KickPower";
        [DisplayName("Cooldown (seconds)"), Description("Minimum time (in seconds) between activations of this effect."), UsedImplicitly] public string CooldownSeconds { get; set; } = "";
        [DisplayName("Kick Range"), Description("Maximum range for the kick to connect."), UsedImplicitly] public string KickRange { get; set; } = "";
    }

    // ── Sekcje defaultowych mocy BLT ──
    public class AddDamageSection : PowerProgressionSection
    {
        public override string GetTargetType() => "AddDamagePower";
        [DisplayName("Damage Modifier (%)"), Description("Percentage multiplier applied to damage dealt."), UsedImplicitly] public string DamageModifierPercent { get; set; } = "";
        [DisplayName("Damage To Add"), Description("Flat damage added on top of the normal hit."), UsedImplicitly] public string DamageToAdd { get; set; } = "";
        [DisplayName("Armor To Ignore (%)"), Description("Percentage of the target's armor ignored by this hit."), UsedImplicitly] public string ArmorToIgnorePercent { get; set; } = "";
        [DisplayName("Unblockable Chance (%)"), Description("Chance this hit bypasses blocking entirely."), UsedImplicitly] public string UnblockableChancePercent { get; set; } = "";
        [DisplayName("Shatter Shield Chance (%)"), Description("Chance this hit destroys the target's shield."), UsedImplicitly] public string ShatterShieldChancePercent { get; set; } = "";
        [DisplayName("Cut Through Chance (%)"), Description("Chance this hit cuts through and also damages whatever is behind the target."), UsedImplicitly] public string CutThroughChancePercent { get; set; } = "";
        [DisplayName("Stagger Chance (%)"), Description("Chance this hit staggers the target."), UsedImplicitly] public string StaggerChancePercent { get; set; } = "";
    }
    public class AddHealthSection : PowerProgressionSection
    {
        public override string GetTargetType() => "AddHealthPower";
        [DisplayName("Health Modifier (%)"), Description("Percentage modifier applied to maximum HP."), UsedImplicitly] public string HealthModifierPercent { get; set; } = "";
        [DisplayName("Health To Add"), Description("Flat maximum HP added."), UsedImplicitly] public string HealthToAdd { get; set; } = "";
    }
    public class AbsorbHealthSection : PowerProgressionSection
    {
        public override string GetTargetType() => "AbsorbHealthPower";
        [DisplayName("Damage To Absorb (%)"), Description("Percentage of incoming damage absorbed instead of applied."), UsedImplicitly] public string DamageToAbsorbPercent { get; set; } = "";
        [DisplayName("Max Absorb"), Description("Maximum amount of damage this effect can absorb."), UsedImplicitly] public string MaxAbsorb { get; set; } = "";
    }
    public class ReflectDamageSection : PowerProgressionSection
    {
        public override string GetTargetType() => "ReflectDamagePower";
        [DisplayName("Reflect (%)"), Description("Percentage of incoming damage reflected back at the attacker."), UsedImplicitly] public string ReflectPercent { get; set; } = "";
        [DisplayName("Damage To Add"), Description("Flat damage added on top of the normal hit."), UsedImplicitly] public string DamageToAdd { get; set; } = "";
    }
    public class TakeDamageSection : PowerProgressionSection
    {
        public override string GetTargetType() => "TakeDamagePower";
        [DisplayName("Damage Modifier (%)"), Description("Percentage multiplier applied to damage dealt."), UsedImplicitly] public string DamageModifierPercent { get; set; } = "";
        [DisplayName("Damage To Add"), Description("Flat damage added on top of the normal hit."), UsedImplicitly] public string DamageToAdd { get; set; } = "";
        [DisplayName("Armor To Ignore (%)"), Description("Percentage of the target's armor ignored by this hit."), UsedImplicitly] public string ArmorToIgnorePercent { get; set; } = "";
    }

    public class BurningStrikeSection : PowerProgressionSection
    {
        public override string GetTargetType() => "BurningStrikePower";
        [DisplayName("Fire Damage Per Tick"), Description("Burn damage dealt each tick."), UsedImplicitly] public string FireDamagePerTick { get; set; } = "";
        [DisplayName("Burn Duration (ticks)"), Description("How many ticks the burn effect lasts."), UsedImplicitly] public string BurnDurationTicks { get; set; } = "";
    }

    public class FrostStrikeSection : PowerProgressionSection
    {
        public override string GetTargetType() => "FrostStrikePower";
        [DisplayName("Slow Speed Limit"), Description("Maximum movement speed fraction (0-1) allowed while slowed - lower is a stronger slow."), UsedImplicitly] public string SlowSpeedLimit { get; set; } = "";
        [DisplayName("Frost Duration (ticks)"), Description("How many ticks the frost/slow effect lasts."), UsedImplicitly] public string FrostDurationTicks { get; set; } = "";
    }
    public class VampirismStrikeSection : PowerProgressionSection
    {
        public override string GetTargetType() => "VampirismStrikePower";
        [DisplayName("Lifesteal (%)"), Description("Percentage of damage dealt returned to the attacker as healing."), UsedImplicitly] public string LifestealPercent { get; set; } = "";
    }
    public class ChainLightningSection : PowerProgressionSection
    {
        public override string GetTargetType() => "ChainLightningPower";
        [DisplayName("Chain Damage"), Description("Damage dealt to each additional target the effect chains to."), UsedImplicitly] public string ChainDamage { get; set; } = "";
        [DisplayName("Chain Radius"), Description("How far (in meters) the effect can jump/chain from one target to the next."), UsedImplicitly] public string ChainRadius { get; set; } = "";
        [DisplayName("Max Targets"), Description("Maximum number of targets this effect can hit at once."), UsedImplicitly] public string MaxTargets { get; set; } = "";
    }
    public class FearAuraSection : PowerProgressionSection
    {
        public override string GetTargetType() => "FearAuraPower";
        [DisplayName("Aura Radius"), Description("How far from the hero (in meters) this aura reaches."), UsedImplicitly] public string Radius { get; set; } = "";
        [DisplayName("Fear Chance Per Tick (%)"), Description("Chance per tick that a nearby enemy becomes feared."), UsedImplicitly] public string FearChancePercent { get; set; } = "";
    }
    public class SlowAuraSection : PowerProgressionSection
    {
        public override string GetTargetType() => "SlowAuraPower";
        [DisplayName("Aura Radius"), Description("How far from the hero (in meters) this aura reaches."), UsedImplicitly] public string Radius { get; set; } = "";
    }
    public class WeaknessAuraSection : PowerProgressionSection
    {
        public override string GetTargetType() => "WeaknessAuraPower";
        [DisplayName("Aura Radius"), Description("How far from the hero (in meters) this aura reaches."), UsedImplicitly] public string Radius { get; set; } = "";
        [DisplayName("Damage Reduction (%)"), Description("Percentage reduction applied to incoming damage while this effect is active."), UsedImplicitly] public string DamageReductionPercent { get; set; } = "";
    }
    public class BattleCryAuraSection : PowerProgressionSection
    {
        public override string GetTargetType() => "BattleCryAuraPower";
        [DisplayName("Aura Radius"), Description("How far from the hero (in meters) this aura reaches."), UsedImplicitly] public string Radius { get; set; } = "";
        [DisplayName("Speed Boost Multiplier"), Description("Multiplier applied to movement speed while boosted (1.0 = no change)."), UsedImplicitly] public string SpeedBoostMultiplier { get; set; } = "";
    }
    public class BloodRageSection : PowerProgressionSection
    {
        public override string GetTargetType() => "BloodRagePower";
        [DisplayName("Damage Bonus Per Stack (%)"), Description("Extra damage percentage added per stack of this effect."), UsedImplicitly] public string DamageBonusPerStack { get; set; } = "";
        [DisplayName("Max Stacks"), Description("Maximum number of times this effect can stack on the same target."), UsedImplicitly] public string MaxStacks { get; set; } = "";
    }
    public class ShadowstepSection : PowerProgressionSection
    {
        public override string GetTargetType() => "ShadowstepPower";
        [DisplayName("Max Range"), Description("Maximum range in meters this effect can reach."), UsedImplicitly] public string MaxRange { get; set; } = "";
    }
    public class IronSkinSection : PowerProgressionSection
    {
        public override string GetTargetType() => "IronSkinPower";
        [DisplayName("Damage Reduction (%)"), Description("Percentage reduction applied to incoming damage while this effect is active."), UsedImplicitly] public string DamageReductionPercent { get; set; } = "";
        [DisplayName("Active Duration (seconds)"), Description("How long (in seconds) this effect stays active once triggered."), UsedImplicitly] public string ActiveDurationSeconds { get; set; } = "";
    }
    public class SecondWindSection : PowerProgressionSection
    {
        public override string GetTargetType() => "SecondWindPower";
        [DisplayName("Heal Amount (%)"), Description("Amount healed, as a percentage of max HP."), UsedImplicitly] public string HealPercent { get; set; } = "";
    }
    public class DodgeSection : PowerProgressionSection
    {
        public override string GetTargetType() => "DodgePower";
        [DisplayName("Dodge Chance (%)"), Description("Chance to avoid an incoming hit entirely."), UsedImplicitly] public string DodgeChancePercent { get; set; } = "";
    }
    public class BackstabSection : PowerProgressionSection
    {
        public override string GetTargetType() => "BackstabPower";
        [DisplayName("Bonus Damage Multiplier (%)"), Description("Multiplier applied to damage dealt while this effect's condition is met."), UsedImplicitly] public string BonusPercent { get; set; } = "";
    }
    public class ShockwaveSection : PowerProgressionSection
    {
        public override string GetTargetType() => "ShockwavePower";
        [DisplayName("Radius (meters)"), Description("Effect radius in meters."), UsedImplicitly] public string Radius { get; set; } = "";
    }
    public class WarBannerSection : PowerProgressionSection
    {
        public override string GetTargetType() => "WarBannerPower";
        [DisplayName("Aura Radius (meters)"), Description("How far from the hero (in meters) this aura reaches."), UsedImplicitly] public string Radius { get; set; } = "";
        [DisplayName("Speed Multiplier"), Description("Multiplier applied to movement speed (1.0 = no change)."), UsedImplicitly] public string SpeedMult { get; set; } = "";
    }
    public class MarkTargetSection : PowerProgressionSection
    {
        public override string GetTargetType() => "MarkTargetPower";
        [DisplayName("Mark Radius (meters)"), Description("How far from the hero (in meters) enemies get marked by this effect."), UsedImplicitly] public string Radius { get; set; } = "";
        [DisplayName("Bonus Damage Multiplier (%)"), Description("Multiplier applied to damage dealt while this effect's condition is met."), UsedImplicitly] public string BonusPercent { get; set; } = "";
    }
    public class RallyingCrySection : PowerProgressionSection
    {
        public override string GetTargetType() => "RallyingCryPower";
        [DisplayName("Heal Amount"), Description("Flat HP restored."), UsedImplicitly] public string HealAmount { get; set; } = "";
        [DisplayName("Radius (meters)"), Description("Effect radius in meters."), UsedImplicitly] public string Radius { get; set; } = "";
    }
    public class StealthSection : PowerProgressionSection
    {
        public override string GetTargetType() => "StealthPower";
        [DisplayName("Stealth Duration (seconds)"), Description("How long the hero stays hidden/untargetable."), UsedImplicitly] public string StealthDuration { get; set; } = "";
    }

    [DisplayName("MBGA - Power Progression")]
    public class PowerProgressionGlobalConfig
    {
        private const string ID = "MBGA - Power Progression";
        internal static void Register() => ActionManager.RegisterGlobalConfigType(ID, typeof(PowerProgressionGlobalConfig));
        internal static PowerProgressionGlobalConfig Get() => ActionManager.GetGlobalConfig<PowerProgressionGlobalConfig>(ID);

        [DisplayName("Enabled"), Category("1 - General"), PropertyOrder(1),
         Description("Enables/disables the entire power progression system. When disabled, powers work normally (base values)."),
         UsedImplicitly]
        public bool Enabled { get; set; } = false;

        [DisplayName("Allow Infinite Tiers"), Category("1 - General"), PropertyOrder(2),
         Description("When enabled: past the last defined tier, powers keep scaling infinitely (linearly, by the difference of the last two values). When disabled: the last tier is the maximum."),
         UsedImplicitly]
        public bool AllowInfinite { get; set; } = false;

        [DisplayName("Infinite Step (Kills)"), Category("1 - General"), PropertyOrder(3),
         Description("Tylko gdy Allow Infinite: co tyle killi (w klasie) doliczany jest kolejny wirtualny tier. Np. 300."),
         UsedImplicitly]
        public int InfiniteStepKills { get; set; } = 300;

        [DisplayName("Infinite Step (Battles)"), Category("1 - General"), PropertyOrder(4),
         Description("Tylko gdy Allow Infinite: co tyle bitew (w klasie) doliczany jest kolejny wirtualny tier. Np. 150."),
         UsedImplicitly]
        public int InfiniteStepBattles { get; set; } = 150;

        [DisplayName("Tiers"), Category("1 - General"), PropertyOrder(5),
         Description("Promotion thresholds. Each tier: how many kills OR battles (whichever comes first) are needed in that class. The first tier should be 0/0 (start). Number of tiers = number of values you enter in the power sections."),
         UsedImplicitly]
        public List<PowerTierDef> Tiers { get; set; } = new List<PowerTierDef>();

        // ── Sekcje per moc (każda pokazuje tylko swoje właściwości) ──
        // W każdym polu wpisz wartości PO PRZECINKU — jedna liczba na tier (np. 8,12,18). Puste pole = wartość bazowa (bez skalowania).
        [DisplayName("Poison Strike"), Category("2 - Powers"), PropertyOrder(10), Description("Expand and enter comma-separated values — one per tier (e.g. 8,12,18). Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public PoisonStrikeSection PoisonStrike { get; set; } = new PoisonStrikeSection();
        [DisplayName("Berserk"), Category("2 - Powers"), PropertyOrder(11), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public BerserkSection Berserk { get; set; } = new BerserkSection();
        [DisplayName("Last Stand"), Category("2 - Powers"), PropertyOrder(12), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public LastStandSection LastStand { get; set; } = new LastStandSection();
        [DisplayName("Taunt"), Category("2 - Powers"), PropertyOrder(13), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public TauntSection Taunt { get; set; } = new TauntSection();
        [DisplayName("Heal Aura"), Category("2 - Powers"), PropertyOrder(14), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public HealAuraSection HealAura { get; set; } = new HealAuraSection();
        [DisplayName("Damage Aura"), Category("2 - Powers"), PropertyOrder(15), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public DamageAuraSection DamageAura { get; set; } = new DamageAuraSection();
        [DisplayName("Curse Aura"), Category("2 - Powers"), PropertyOrder(16), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public CurseAuraSection CurseAura { get; set; } = new CurseAuraSection();
        [DisplayName("Buff Aura"), Category("2 - Powers"), PropertyOrder(17), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public BuffAuraSection BuffAura { get; set; } = new BuffAuraSection();
        [DisplayName("Teleport (Passive)"), Category("2 - Powers"), PropertyOrder(18), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public TeleportSection Teleport { get; set; } = new TeleportSection();
        [DisplayName("Jump Attack"), Category("2 - Powers"), PropertyOrder(19), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public JumpAttackSection JumpAttack { get; set; } = new JumpAttackSection();
        [DisplayName("Kick"), Category("2 - Powers"), PropertyOrder(20), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public KickSection Kick { get; set; } = new KickSection();
        [DisplayName("Add Damage Power"), Category("2 - Powers"), PropertyOrder(21), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public AddDamageSection AddDamage { get; set; } = new AddDamageSection();
        [DisplayName("Add Health Power"), Category("2 - Powers"), PropertyOrder(22), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public AddHealthSection AddHealth { get; set; } = new AddHealthSection();
        [DisplayName("Absorb Health Power"), Category("2 - Powers"), PropertyOrder(23), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public AbsorbHealthSection AbsorbHealth { get; set; } = new AbsorbHealthSection();
        [DisplayName("Reflect Damage Power"), Category("2 - Powers"), PropertyOrder(24), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public ReflectDamageSection ReflectDamage { get; set; } = new ReflectDamageSection();
        [DisplayName("Take Damage Power"), Category("2 - Powers"), PropertyOrder(25), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public TakeDamageSection TakeDamage { get; set; } = new TakeDamageSection();
        [DisplayName("Burning Strike"), Category("2 - Powers"), PropertyOrder(30), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public BurningStrikeSection BurningStrike { get; set; } = new BurningStrikeSection();
        [DisplayName("Frost Strike"), Category("2 - Powers"), PropertyOrder(31), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public FrostStrikeSection FrostStrike { get; set; } = new FrostStrikeSection();
        [DisplayName("Vampirism Strike"), Category("2 - Powers"), PropertyOrder(32), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public VampirismStrikeSection VampirismStrike { get; set; } = new VampirismStrikeSection();
        [DisplayName("Chain Lightning"), Category("2 - Powers"), PropertyOrder(33), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public ChainLightningSection ChainLightning { get; set; } = new ChainLightningSection();
        [DisplayName("Fear Aura"), Category("2 - Powers"), PropertyOrder(34), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public FearAuraSection FearAura { get; set; } = new FearAuraSection();
        [DisplayName("Slow Aura"), Category("2 - Powers"), PropertyOrder(35), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public SlowAuraSection SlowAura { get; set; } = new SlowAuraSection();
        [DisplayName("Weakness Aura"), Category("2 - Powers"), PropertyOrder(36), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public WeaknessAuraSection WeaknessAura { get; set; } = new WeaknessAuraSection();
        [DisplayName("Battle Cry Aura"), Category("2 - Powers"), PropertyOrder(37), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public BattleCryAuraSection BattleCryAura { get; set; } = new BattleCryAuraSection();
        [DisplayName("Blood Rage"), Category("2 - Powers"), PropertyOrder(38), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public BloodRageSection BloodRage { get; set; } = new BloodRageSection();
        [DisplayName("Shadowstep"), Category("2 - Powers"), PropertyOrder(39), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public ShadowstepSection Shadowstep { get; set; } = new ShadowstepSection();
        [DisplayName("Iron Skin"), Category("2 - Powers"), PropertyOrder(40), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public IronSkinSection IronSkin { get; set; } = new IronSkinSection();
        [DisplayName("Second Wind"), Category("2 - Powers"), PropertyOrder(41), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public SecondWindSection SecondWind { get; set; } = new SecondWindSection();
        [DisplayName("Dodge"), Category("2 - Powers"), PropertyOrder(42), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public DodgeSection Dodge { get; set; } = new DodgeSection();
        [DisplayName("Backstab"), Category("2 - Powers"), PropertyOrder(43), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public BackstabSection Backstab { get; set; } = new BackstabSection();
        [DisplayName("Shockwave"), Category("2 - Powers"), PropertyOrder(44), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public ShockwaveSection Shockwave { get; set; } = new ShockwaveSection();
        [DisplayName("War Banner"), Category("2 - Powers"), PropertyOrder(45), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public WarBannerSection WarBanner { get; set; } = new WarBannerSection();
        [DisplayName("Mark Target"), Category("2 - Powers"), PropertyOrder(46), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public MarkTargetSection MarkTarget { get; set; } = new MarkTargetSection();
        [DisplayName("Rallying Cry"), Category("2 - Powers"), PropertyOrder(47), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public RallyingCrySection RallyingCry { get; set; } = new RallyingCrySection();
        [DisplayName("Stealth"), Category("2 - Powers"), PropertyOrder(48), Description("Comma-separated values, one per tier. Empty = no change."), ExpandableObject, Expand, UsedImplicitly] public StealthSection Stealth { get; set; } = new StealthSection();

        public PowerProgressionSection GetSection(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            foreach (var p in GetType().GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!typeof(PowerProgressionSection).IsAssignableFrom(p.PropertyType)) continue;
                var s = p.GetValue(this) as PowerProgressionSection;
                if (s != null && s.GetTargetType() == typeName) return s;
            }
            return null;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  POWER PROGRESSION — serwis
    // ════════════════════════════════════════════════════════════════════

    public static class PowerProgression
    {
        private static readonly System.Reflection.Assembly MbgaAssembly = typeof(PowerProgression).Assembly;

        public static int GetTier(Hero hero)
        {
            try
            {
                if (hero == null) return 0;
                var cfg = PowerProgressionGlobalConfig.Get();
                if (cfg == null || !cfg.Enabled || cfg.Tiers == null || cfg.Tiers.Count == 0) return 0;

                var beh = BLTAdoptAHeroCampaignBehavior.Current;
                if (beh == null) return 0;

                int kills   = beh.GetAchievementClassStat(hero, AchievementStatsData.Statistic.TotalKills);
                int battles = beh.GetAchievementClassStat(hero, AchievementStatsData.Statistic.Battles);

                int lastIndex = 0;
                for (int i = 0; i < cfg.Tiers.Count; i++)
                {
                    var t = cfg.Tiers[i];
                    if (kills >= t.KillsRequired || battles >= t.BattlesRequired)
                        lastIndex = i;
                }

                if (!cfg.AllowInfinite) return lastIndex;
                if (lastIndex < cfg.Tiers.Count - 1) return lastIndex;

                var top = cfg.Tiers[cfg.Tiers.Count - 1];
                int stepK = Math.Max(1, cfg.InfiniteStepKills);
                int stepB = Math.Max(1, cfg.InfiniteStepBattles);
                int extraK = Math.Max(0, (kills   - top.KillsRequired)   / stepK);
                int extraB = Math.Max(0, (battles - top.BattlesRequired) / stepB);
                return (cfg.Tiers.Count - 1) + Math.Max(extraK, extraB);
            }
            catch { return 0; }
        }

        // Wartość property dla danego tieru (z ekstrapolacją liniową ponad ostatni element).
        public static float GetValueForTier(List<float> values, int tier)
        {
            if (values == null || values.Count == 0) return 0f;
            if (tier < 0) tier = 0;
            if (tier < values.Count) return values[tier];

            float last = values[values.Count - 1];
            float step = values.Count >= 2 ? values[values.Count - 1] - values[values.Count - 2] : 0f;
            float val = last + step * (tier - (values.Count - 1));
            return val < 0f ? 0f : val;
        }

        private static bool TryGetScaled(HeroPowerDefBase power, string propName, Hero hero, out float scaled)
        {
            scaled = 0f;
            var cfg = PowerProgressionGlobalConfig.Get();
            if (cfg == null || !cfg.Enabled) return false;
            var section = cfg.GetSection(power.GetType().Name);
            if (section == null) return false;
            var values = section.Get(propName);
            if (values == null || values.Count == 0) return false;
            scaled = GetValueForTier(values, GetTier(hero));
            return true;
        }

        public static int ScaleInt(HeroPowerDefBase power, Hero hero, string propName, int baseValue)
        {
            try
            {
                if (power == null || hero == null) return baseValue;
                return TryGetScaled(power, propName, hero, out float v)
                    ? (int)Math.Round(v) : baseValue;
            }
            catch { return baseValue; }
        }

        public static float ScaleFloat(HeroPowerDefBase power, Hero hero, string propName, float baseValue)
        {
            try
            {
                if (power == null || hero == null) return baseValue;
                return TryGetScaled(power, propName, hero, out float v) ? v : baseValue;
            }
            catch { return baseValue; }
        }

        // Opis aktualnych (przeskalowanych) wartości mocy na tierze bohatera — do komendy info.
        // Zwraca np. "PoisonDamagePerTick=12, PoisonDurationTicks=6" albo null gdy brak skalowania.
        public static string DescribeCurrent(HeroPowerDefBase power, Hero hero)
        {
            try
            {
                if (power == null || hero == null) return null;
                var cfg = PowerProgressionGlobalConfig.Get();
                if (cfg == null || !cfg.Enabled) return null;
                var section = cfg.GetSection(power.GetType().Name);
                if (section == null) return null;

                int tier = GetTier(hero);
                var parts = new List<string>();
                foreach (var kv in section.AllEntries())
                {
                    float v = GetValueForTier(kv.Value, tier);
                    string num = (Math.Abs(v - Math.Round(v)) < 0.001f)
                        ? ((int)Math.Round(v)).ToString()
                        : v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                    parts.Add($"{kv.Key}={num}");
                }
                return parts.Count > 0 ? string.Join(", ", parts) : null;
            }
            catch { return null; }
        }

        // Dla defaultowych mocy BLT: zwraca przeskalowany KLON per bohater.
        // Moce MBGA lub brak reguł → oryginał (bez klonu).
        public static HeroPowerDefBase GetScaledClone(HeroPowerDefBase power, Hero hero)
        {
            try
            {
                if (power == null || hero == null) return power;

                var cfg = PowerProgressionGlobalConfig.Get();
                if (cfg == null || !cfg.Enabled) return power;

                if (power.GetType().Assembly == MbgaAssembly) return power;

                var section = cfg.GetSection(power.GetType().Name);
                if (section == null) return power;

                int tier = GetTier(hero);

                var clone = (HeroPowerDefBase)power.Clone();
                bool anyApplied = false;
                foreach (var entry in section.AllEntries())
                {
                    var prop = clone.GetType().GetProperty(entry.Key,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (prop == null || !prop.CanWrite) { Log.Info($"[PowerProg] prop '{entry.Key}' not found/writable on {power.GetType().Name}"); continue; }

                    float v = GetValueForTier(entry.Value, tier);
                    if (prop.PropertyType == typeof(int))
                        prop.SetValue(clone, (int)Math.Round(v));
                    else if (prop.PropertyType == typeof(float))
                        prop.SetValue(clone, v);
                    else if (prop.PropertyType == typeof(double))
                        prop.SetValue(clone, (double)v);
                    else continue;
                    anyApplied = true;
                }
                return anyApplied ? clone : power;
            }
            catch { return power; }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  POWER PROGRESSION — Harmony patche (ścieżka B: defaultowe moce BLT)
    // ════════════════════════════════════════════════════════════════════

    // Patch aplikowany RĘCZNIE w BLTAurasModule.OnSubModuleLoad (NIE przez [HarmonyPatch]/PatchAll —
    // inaczej 9 modułów MBGA zaaplikowałoby go 9×).
    internal static class PowerProgressionPatches
    {
        // Prefix na PassivePowerGroup.OnHeroJoinedBattle — podmienia moce na przeskalowane klony.
        internal static bool OnHeroJoinedBattle_Prefix(PassivePowerGroup __instance, Hero hero)
        {
            try
            {
                var cfg = PowerProgressionGlobalConfig.Get();
                if (cfg == null || !cfg.Enabled) return true; // brak zmian → oryginał

                // odtworzenie warunku turniejowego z oryginału (PassivePowerGroup.cs:67)
                // GlobalHeroPowerConfig.Get() jest internal — używamy ActionManager z ID z GlobalHeroPowerConfig.cs:19
                var powerConfig = ActionManager.GetGlobalConfig<GlobalHeroPowerConfig>("Adopt A Hero - Power Config");
                if (powerConfig != null && powerConfig.DisablePowersInTournaments
                    && BannerlordTwitch.Helpers.MissionHelpers.InTournament())
                {
                    return false;
                }

                foreach (var power in __instance.GetUnlockedPowers(hero))
                {
                    var basePower = power as HeroPowerDefBase;
                    var effective = PowerProgression.GetScaledClone(basePower, hero);
                    var effectivePassive = effective as IHeroPowerPassive;
                    if (effectivePassive == null) continue;

                    BLTHeroPowersMissionBehavior.PowerHandler.ConfigureHandlers(
                        hero, effective,
                        handlers => effectivePassive.OnHeroJoinedBattle(hero, handlers));
                }
                return false; // pomijamy oryginał — sami obsłużyliśmy
            }
            catch (Exception e)
            {
                Log.Exception("[PowerProg] OnHeroJoinedBattle_Prefix", e);
                return true; // w razie błędu — uruchom oryginał
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  POWER PROGRESSION — komenda bota
    // ════════════════════════════════════════════════════════════════════

    [DisplayName("Power Tier Info"),
     Description("Shows the viewer's current power progression tier"),
     LocDescription("Shows the viewer's current power progression tier and kills/battles needed for the next one."),
     UsedImplicitly]
    internal class PowerTierCommand : ICommandHandler
    {
        private class Settings { }
        public Type HandlerConfigType => typeof(Settings);

        public void Execute(ReplyContext context, object config)
        {
            try
            {
                var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
                if (hero == null)
                {
                    ActionManager.SendReply(context, "You haven't adopted a hero yet.");
                    return;
                }

                var cfg = PowerProgressionGlobalConfig.Get();
                if (cfg == null || !cfg.Enabled || cfg.Tiers == null || cfg.Tiers.Count == 0)
                {
                    ActionManager.SendReply(context, "Power progression is disabled.");
                    return;
                }

                var heroClass = hero.GetClass();
                string className = heroClass?.Name?.ToString() ?? "(no class)";

                int tier = PowerProgression.GetTier(hero);
                int defined = cfg.Tiers.Count;

                var beh = BLTAdoptAHeroCampaignBehavior.Current;
                int kills   = beh.GetAchievementClassStat(hero, AchievementStatsData.Statistic.TotalKills);
                int battles = beh.GetAchievementClassStat(hero, AchievementStatsData.Statistic.Battles);

                string tierLabel;
                string nextInfo;

                if (tier < defined - 1)
                {
                    tierLabel = $"Tier {tier + 1} \"{cfg.Tiers[tier].Name}\"";
                    var next = cfg.Tiers[tier + 1];
                    int needK = Math.Max(0, next.KillsRequired - kills);
                    int needB = Math.Max(0, next.BattlesRequired - battles);
                    nextInfo = $"To next: {needK} kills or {needB} battles";
                }
                else if (cfg.AllowInfinite)
                {
                    int extra = tier - (defined - 1);
                    tierLabel = extra == 0
                        ? $"Tier {tier + 1} \"{cfg.Tiers[defined - 1].Name}\" (max defined)"
                        : $"Tier ∞+{extra}";
                    var top = cfg.Tiers[defined - 1];
                    int stepK = Math.Max(1, cfg.InfiniteStepKills);
                    int stepB = Math.Max(1, cfg.InfiniteStepBattles);
                    int nextVirtual = extra + 1;
                    int needK = Math.Max(0, top.KillsRequired + nextVirtual * stepK - kills);
                    int needB = Math.Max(0, top.BattlesRequired + nextVirtual * stepB - battles);
                    nextInfo = $"To next: {needK} kills or {needB} battles";
                }
                else
                {
                    tierLabel = $"Tier {tier + 1} \"{cfg.Tiers[defined - 1].Name}\" (MAX)";
                    nextInfo = "Max tier reached";
                }

                var lines = new List<string> { $"Class: {className} | {tierLabel} | {nextInfo}" };

                // Wypisz WSZYSTKIE moce bohatera; przy tych ze skalowaniem dokłada aktualne wartości
                try
                {
                    foreach (var p in heroClass?.PassivePower?.GetUnlockedPowers(hero)
                        ?? Enumerable.Empty<IHeroPowerPassive>())
                    {
                        if (!(p is HeroPowerDefBase pdef)) continue;
                        var desc = PowerProgression.DescribeCurrent(pdef, hero);
                        string head = $"[P] {pdef.Name} ({pdef.GetType().Name})";
                        lines.Add(desc != null ? $"{head}: {desc}" : head);
                    }
                    foreach (var p in heroClass?.ActivePower?.GetUnlockedPowers(hero)
                        ?? Enumerable.Empty<IHeroPowerActive>())
                    {
                        if (!(p is HeroPowerDefBase pdef)) continue;
                        var desc = PowerProgression.DescribeCurrent(pdef, hero);
                        string head = $"[A] {pdef.Name} ({pdef.GetType().Name})";
                        lines.Add(desc != null ? $"{head}: {desc}" : head);
                    }
                }
                catch { }

                ActionManager.SendReply(context, lines.ToArray());
            }
            catch (Exception e)
            {
                Log.Exception("[PowerProg] PowerTierCommand", e);
                ActionManager.SendReply(context, "Error reading power tier.");
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  ADOPT CULTURE RESTRICTION — config
    // ════════════════════════════════════════════════════════════════════

    [DisplayName("MBGA - Culture Restriction")]
    public class AdoptCultureRestrictionGlobalConfig
    {
        private const string ID = "MBGA - Culture Restriction";
        internal static void Register() => ActionManager.RegisterGlobalConfigType(ID, typeof(AdoptCultureRestrictionGlobalConfig));
        internal static AdoptCultureRestrictionGlobalConfig Get() => ActionManager.GetGlobalConfig<AdoptCultureRestrictionGlobalConfig>(ID);

        [DisplayName("Enabled"),
         Description("Restrict which cultures can be adopted via the adopt-by-culture command. When off, all cultures are available (default BLT behavior)."),
         UsedImplicitly]
        public bool Enabled { get; set; } = false;

        [DisplayName("Allowed Cultures"),
         Description("List of culture names or string ids that viewers are allowed to adopt (e.g. Empire, Vlandia). Case-insensitive."),
         UsedImplicitly]
        public List<string> AllowedCultures { get; set; } = new List<string>();

        public bool IsAllowed(CultureObject culture)
        {
            if (culture == null || AllowedCultures == null) return false;
            string name = culture.Name?.ToString()?.Trim();
            string sid  = culture.StringId?.Trim();
            return AllowedCultures.Any(a =>
            {
                var t = a?.Trim();
                return !string.IsNullOrEmpty(t) &&
                    (string.Equals(t, name, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(t, sid, StringComparison.OrdinalIgnoreCase));
            });
        }

        public string AllowedDisplay()
            => string.Join(", ", CampaignHelpers.MainCultures.Where(IsAllowed).Select(c => c.Name.ToString()));
    }

    // ════════════════════════════════════════════════════════════════════
    //  BANNER SANITIZER — fix Warsails banner crash (no-Warsails setups)
    // ════════════════════════════════════════════════════════════════════

    public class BLTBannerSanitizerBehavior : CampaignBehaviorBase
    {
        public override void RegisterEvents()
        {
            CampaignEvents.OnGameLoadFinishedEvent.AddNonSerializedListener(this, Sanitize);
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, _ => Sanitize());
            // Also re-sanitize hourly: banners can appear/change mid-play (new clans, mod-spawned
            // heroes) after load/session-launch already ran, and a malformed one causes a native NRE
            // in ApplyBannerTextureToMesh (the colleague's !summon banner crash). Cheap periodic sweep.
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, Sanitize);
        }

        public override void SyncData(IDataStore dataStore) { }

        private static void Sanitize()
        {
            try
            {
                var mgr = BannerManager.Instance;
                if (mgr == null) return;

                var validIcons = new HashSet<int>();
                var validBg = new HashSet<int>();
                foreach (var g in mgr.BannerIconGroups)
                {
                    if (g?.AllIcons != null) foreach (var k in g.AllIcons.Keys) validIcons.Add(k);
                    if (g?.AllBackgrounds != null) foreach (var k in g.AllBackgrounds.Keys) validBg.Add(k);
                }

                int fixedCount = 0;
                foreach (var hero in CampaignHelpers.AllHeroes.Where(h => h.IsAdopted()).ToList())
                {
                    try
                    {
                        var clan = hero.Clan;
                        if (clan?.Banner == null) continue;
                        if (!BannerHasInvalidIcon(clan.Banner, validIcons, validBg)) continue;

                        var safe = Banner.CreateRandomBanner();
                        clan.Banner = safe;
                        if (hero.IsKingdomLeader && clan.Kingdom != null)
                            clan.Kingdom.Banner = safe;
                        fixedCount++;
                        Log.Info($"[BannerFix] Sanitized banner for {hero.Name}");
                    }
                    catch (Exception exHero)
                    {
                        try
                        {
                            if (hero.Clan != null) hero.Clan.Banner = Banner.CreateRandomBanner();
                            fixedCount++;
                        }
                        catch { }
                        Log.Exception($"[BannerFix] Error on {hero?.Name}, replaced banner", exHero);
                    }
                }
                if (fixedCount > 0) Log.Info($"[BannerFix] Sanitized {fixedCount} clan banner(s).");
            }
            catch (Exception ex) { Log.Exception("[BannerFix] Sanitize failed", ex); }
        }

        private static bool BannerHasInvalidIcon(Banner b, HashSet<int> validIcons, HashSet<int> validBg)
        {
            var list = b.BannerDataList;
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                int mesh = list[i].MeshId;
                bool ok = (i == 0) ? validBg.Contains(mesh) : validIcons.Contains(mesh);
                if (!ok) return true;
            }
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  ADOPT CULTURE RESTRICTION — Harmony prefix (aplikowany ręcznie raz)
    // ════════════════════════════════════════════════════════════════════

    internal static class AdoptCultureRestrictionPatch
    {
        internal static System.Reflection.MethodBase TargetMethod()
        {
            var type = typeof(BLTAdoptAHeroCampaignBehavior).Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == "AdoptAHero");
            return type?.GetMethod("ExecuteInternal",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        }

        internal static bool Prefix(object settings, string contextArgs, ref ValueTuple<bool, string> __result)
        {
            try
            {
                var cfg = AdoptCultureRestrictionGlobalConfig.Get();
                if (cfg == null || !cfg.Enabled || cfg.AllowedCultures == null || cfg.AllowedCultures.Count == 0)
                    return true;

                var vsProp = settings?.GetType().GetProperty("ViewerSelects");
                var vsVal = vsProp?.GetValue(settings);
                if (vsVal == null || vsVal.ToString() != "Culture")
                    return true;

                string allowedDisplay = cfg.AllowedDisplay();
                string arg = (contextArgs ?? "").Trim();

                if (arg.Length <= 1 || string.Equals(arg, "list", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "a", StringComparison.OrdinalIgnoreCase))
                {
                    __result = (false, $"Available cultures: {allowedDisplay}");
                    return false;
                }

                var match = CampaignHelpers.MainCultures
                    .Where(cfg.IsAllowed)
                    .FirstOrDefault(c => c.Name.ToString().StartsWith(arg, StringComparison.CurrentCultureIgnoreCase));
                if (match != null)
                    return true;

                var exists = CampaignHelpers.MainCultures
                    .FirstOrDefault(c => c.Name.ToString().StartsWith(arg, StringComparison.CurrentCultureIgnoreCase));
                __result = exists != null
                    ? (false, $"Culture '{exists.Name}' is not allowed. Available cultures: {allowedDisplay}")
                    : (false, $"No culture starting with '{arg}' found. Available cultures: {allowedDisplay}");
                return false;
            }
            catch (Exception e)
            {
                Log.Exception("[CultureRestrict] Prefix", e);
                return true;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  HUMAN CHILDREN — anti-crash dla nie-ludzkich ras (The Old Realm)
    //  Każde nowonarodzone dziecko wymuszane na rasę ludzką (model dziecka).
    // ════════════════════════════════════════════════════════════════════

    internal static class HumanChildPatch
    {
        internal static System.Reflection.MethodBase TargetMethod()
            => typeof(HeroCreator).GetMethod("DeliverOffSpring",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        internal static void Postfix(Hero __result)
        {
            try
            {
                if (__result?.CharacterObject == null) return;
                int human = TaleWorlds.Core.FaceGen.GetRaceOrDefault("human");
                if (__result.CharacterObject.Race != human)
                    __result.CharacterObject.UpdatePlayerCharacterBodyProperties(__result.BodyProperties, human, __result.IsFemale);
            }
            catch (Exception e) { Log.Exception("[HumanChild] Postfix", e); }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  EQUIP FROM HERO CULTURE (TOR) — config + Harmony prefix
    //  Bohater dostaje sprzet TYLKO z wlasnej kultury (nigdy obcej).
    // ════════════════════════════════════════════════════════════════════

    [DisplayName("MBGA - Equip From Culture")]
    public class EquipCultureGlobalConfig
    {
        private const string ID = "MBGA - Equip From Culture";
        internal static void Register() => ActionManager.RegisterGlobalConfigType(ID, typeof(EquipCultureGlobalConfig));
        internal static EquipCultureGlobalConfig Get() => ActionManager.GetGlobalConfig<EquipCultureGlobalConfig>(ID);

        [DisplayName("Enabled"),
         Description("Adopted heroes only get equipment from their own culture (for total-conversion mods like The Old Realms where every item is culture-tagged). If the culture has no item for a slot, the nearest tier of the SAME culture is used, or the slot stays empty. Leave OFF for vanilla (most vanilla items have no culture and heroes would end up unarmed)."),
         UsedImplicitly]
        public bool Enabled { get; set; } = false;
    }

    public class WandererRecord
    {
        public string OwnerName { get; set; }     // owner viewer name (hero.FirstName.Raw())
        public string HeroStringId { get; set; }  // real Hero backing this wanderer - resolved live, never serialized as an object
        public string Power { get; set; }         // WandererPowerCatalog.Def.Id - rolled once at hire time, kept for the wanderer's lifetime
        public int Kills { get; set; }            // lifetime kills by this wanderer (self-progression, independent of owner)
        public int Battles { get; set; }          // lifetime battles this wanderer has fought in
        public int Tier { get; set; } = 1;        // 1-8, recomputed from Kills/Battles after each battle; drives own equipment + power scaling
    }

    // Small hand-rolled power set for wanderers. Deliberately NOT the same 34-power system used by
    // adopted heroes (that one is gated on Hero.IsAdopted(), which wanderers never satisfy, and its
    // dispatch runs on every hit/kill in the whole mission - too risky to touch for this). Instead
    // these reuse AgentModifierConfig/BLTAgentModifierBehavior/BLTTimedBuffBehavior directly, which
    // are already Agent-keyed (see CommanderAura/BuffReward above), so no core system changes needed.
    public static class WandererPowerCatalog
    {
        public struct Def { public string Id; public string Display; public string Desc; }

        public static readonly Def[] All =
        {
            new Def { Id = "IronSkin", Display = "Iron Skin", Desc = "+25% head armor (permanent)" },
            new Def { Id = "SwiftHunter", Display = "Swift Hunter", Desc = "+20% movement speed (permanent)" },
            new Def { Id = "Vampirism", Display = "Vampirism", Desc = "heals 20% max HP on each kill" },
            new Def { Id = "Bloodrage", Display = "Bloodrage", Desc = "+30% attack speed for 8s after each kill" },
            new Def { Id = "SecondWind", Display = "Second Wind", Desc = "once per battle: heals to 60% HP + speed burst when HP first drops to 25%" },
            new Def { Id = "Berserk", Display = "Berserk", Desc = "+40% attack & move speed while HP is at or below 50%" },
            new Def { Id = "BattleCry", Display = "Battle Cry", Desc = "every 25s: +25% attack & move speed for 6s" },
            new Def { Id = "Juggernaut", Display = "Juggernaut", Desc = "regenerates 0.5 HP/s (permanent)" },
        };

        public static Def Pick() => All[MBRandom.RandomInt(All.Length)];

        public static Def? Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var d in All) if (d.Id == id) return d;
            return null;
        }
    }

    public class BLTWandererBehavior : CampaignBehaviorBase
    {
        public static BLTWandererBehavior Current => Campaign.Current?.GetCampaignBehavior<BLTWandererBehavior>();

        private Dictionary<string, List<WandererRecord>> wanderers = new Dictionary<string, List<WandererRecord>>();

        public override void RegisterEvents() { }

        public override void SyncData(IDataStore dataStore)
        {
            // Only lightweight metadata (owner name + Hero StringId) is persisted here - the actual
            // Hero object (stats/skills/equipment) is saved natively by the campaign itself, since
            // it's a real registered Hero, not a fake mirror.
            string json = dataStore.IsSaving
                ? Newtonsoft.Json.JsonConvert.SerializeObject(wanderers ?? new Dictionary<string, List<WandererRecord>>())
                : null;
            dataStore.SyncData("BLTWanderersJson", ref json);
            if (!dataStore.IsSaving)
            {
                wanderers = string.IsNullOrEmpty(json)
                    ? new Dictionary<string, List<WandererRecord>>()
                    : (Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, List<WandererRecord>>>(json)
                       ?? new Dictionary<string, List<WandererRecord>>());
            }
            if (wanderers == null) wanderers = new Dictionary<string, List<WandererRecord>>();
        }

        public List<WandererRecord> GetRecords(string ownerName)
        {
            if (string.IsNullOrEmpty(ownerName)) return new List<WandererRecord>();
            if (!wanderers.TryGetValue(ownerName, out var list)) { list = new List<WandererRecord>(); wanderers[ownerName] = list; }
            return list;
        }

        public static Hero ResolveHero(WandererRecord rec)
            => string.IsNullOrEmpty(rec?.HeroStringId) ? null : CampaignHelpers.AllHeroes.FirstOrDefault(h => h.StringId == rec.HeroStringId);

        public List<(WandererRecord Record, Hero Hero)> GetWanderers(string ownerName)
            => GetRecords(ownerName).Select(r => (r, ResolveHero(r))).Where(x => x.Item2 != null).ToList();

        // Finds a wanderer's record by its backing Hero's StringId, regardless of which owner it
        // belongs to - used by power-magnitude scaling, which only has the wanderer's own Agent/Hero
        // to work with, not the owner key.
        public WandererRecord FindRecordByHeroStringId(string heroStringId)
        {
            if (string.IsNullOrEmpty(heroStringId)) return null;
            foreach (var list in wanderers.Values)
            {
                var match = list?.FirstOrDefault(r => r.HeroStringId == heroStringId);
                if (match != null) return match;
            }
            return null;
        }

        public WandererRecord Add(string ownerName, Hero hero)
        {
            var rec = new WandererRecord { OwnerName = ownerName, HeroStringId = hero.StringId };
            GetRecords(ownerName).Add(rec);
            return rec;
        }

        public bool Remove(string ownerName, WandererRecord rec) => GetRecords(ownerName).Remove(rec);

        public int WandererCountFor(Hero hero)
        {
            try
            {
                if (hero == null) return 0;
                string key = hero.FirstName != null ? hero.FirstName.Raw() : null;
                if (string.IsNullOrEmpty(key)) return 0;
                return GetWanderers(key).Count;
            }
            catch { return 0; }
        }

        public static int CountForHero(Hero hero) => Current?.WandererCountFor(hero) ?? 0;

        public static bool IsWandererStringId(string stringId)
        {
            if (string.IsNullOrEmpty(stringId) || Current == null) return false;
            try { return Current.wanderers.Values.Any(list => list != null && list.Any(r => r.HeroStringId == stringId)); }
            catch { return false; }
        }
    }

    // ── !reborn — bring back your fallen hero as a brand new character, inheriting everything
    // (gold, retinue, class, custom items, kill/battle stats) except the death itself. The old
    // Resurrect feature revived the SAME Hero object and it kept getting re-executed on sight
    // (some lingering native death state); spawning a fresh Hero and transplanting the BLT data
    // onto it sidesteps that entirely. Usable as a chat command or a channel points reward.
    [DisplayName("Reborn")]
    [LocDescription("Brings back your fallen hero as a brand new character, inheriting gold, retinue, class, custom items, and kill/battle stats - everything except the death itself. Usable as a chat command or a channel points reward.")]
    public class RebornCommand : ICommandHandler, IRewardHandler
    {
        public class Settings
        {
            [DisplayName("Starting Gold Bonus"), Description("Extra gold given on top of what's inherited."), UsedImplicitly]
            public int StartingGoldBonus { get; set; } = 0;

            [DisplayName("Show Notifications"), Description("Display feed notifications when reborn."), UsedImplicitly]
            public bool Notifications { get; set; } = true;
        }

        Type IRewardHandler.RewardConfigType => typeof(Settings);
        Type ICommandHandler.HandlerConfigType => typeof(Settings);

        void IRewardHandler.Enqueue(ReplyContext context, object config)
        {
            var (_, message) = Execute(context, (Settings)config ?? new Settings());
            if (message != null) ActionManager.NotifyComplete(context, message);
        }

        void ICommandHandler.Execute(ReplyContext context, object config)
        {
            var (_, message) = Execute(context, (Settings)config ?? new Settings());
            if (message != null) ActionManager.SendReply(context, message);
        }

        private static (bool, string) Execute(ReplyContext context, Settings settings)
        {
            var beh = BLTAdoptAHeroCampaignBehavior.Current;
            if (beh == null) return (false, "BLT not ready.");

            var alive = beh.GetAdoptedHero(context.UserName);
            if (alive != null) return (false, "Your hero is still alive - !reborn is only for fallen heroes.");

            var fallen = beh.GetRetiredHero(context.UserName);
            if (fallen == null || !fallen.IsDead) return (false, "No fallen hero found to bring back.");

            try
            {
                var culture = fallen.Culture;
                var template = CampaignHelpers.GetWandererTemplates(culture).SelectRandom()
                            ?? CampaignHelpers.AllWandererTemplates.SelectRandom();
                if (template == null) return (false, "Couldn't find a template to reincarnate into.");

                var newHero = HeroCreator.CreateSpecialHero(template);
                newHero.ChangeState(Hero.CharacterStates.Active);
                if (newHero.GetSkillValue(CampaignHelpers.AllSkillObjects.First()) == 0)
                    newHero.HeroDeveloper.SetInitialSkillLevel(CampaignHelpers.AllSkillObjects.First(), 1);

                var targetSettlement = Settlement.All.Where(s => s.IsTown).SelectRandom();
                if (targetSettlement != null) EnterSettlementAction.ApplyForCharacterOnly(newHero, targetSettlement);

                string oldName = fallen.Name.ToString();
                beh.InitAdoptedHero(newHero, context.UserName);

                // CloneHeroData jest dodatkiem WYLACZNIE forka (kopiuje skille/staty/custom itemy
                // miedzy bohaterami) - nie istnieje w oryginalnym DLL Randomchair22, wiec wolamy przez
                // refleksje i po prostu pomijamy jesli metoda nie istnieje (nowy bohater zostaje bez
                // przekopiowanych statow zamiast crashowac cala komende).
                var cloneMethod = typeof(BLTAdoptAHeroCampaignBehavior).GetMethod("CloneHeroData",
                    BindingFlags.Public | BindingFlags.Instance);
                cloneMethod?.Invoke(beh, new object[] { fallen, newHero });

                // Rejoin the old clan, taking over leadership if the fallen hero led it
                if (fallen.Clan != null)
                {
                    bool wasLeader = fallen.Clan.Leader == fallen;
                    newHero.Clan = fallen.Clan;
                    if (wasLeader) ChangeClanLeaderAction.ApplyWithSelectedNewLeader(fallen.Clan, newHero);
                }

                if (settings.StartingGoldBonus > 0)
                    beh.ChangeHeroGold(newHero, settings.StartingGoldBonus);

                // EquipHero is internal to BLTAdoptAHero, so call its static UpgradeEquipment via reflection.
                var classDef = beh.GetClass(newHero);
                var equipHeroType = typeof(BLTAdoptAHeroCampaignBehavior).Assembly.GetTypes()
                    .FirstOrDefault(t => t.Name == "EquipHero");
                var upgradeMethod = equipHeroType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "UpgradeEquipment");
                if (upgradeMethod != null)
                {
                    var parameters = upgradeMethod.GetParameters();
                    var args = new object[parameters.Length];
                    args[0] = newHero;
                    args[1] = beh.GetEquipmentTier(newHero);
                    args[2] = classDef;
                    args[3] = true; // replaceSameTier
                    for (int i = 4; i < parameters.Length; i++) args[i] = Type.Missing;
                    upgradeMethod.Invoke(null, args);
                }

                string msg = $"{oldName} has been reborn as {newHero.Name}, remembering everything!";
                if (settings.Notifications)
                    Log.ShowInformation(msg, newHero.CharacterObject, Log.Sound.Horns2);
                else
                    Log.Info(msg);

                return (true, msg);
            }
            catch (Exception ex)
            {
                Log.Exception("RebornCommand", ex);
                return (false, "Something went wrong bringing your hero back.");
            }
        }
    }

    [LocDescription("Manages hired Wanderer companions for your hero: hire a random one, list yours, or check status. Wanderers roll a random passive power on hire and grow their own Tier from kills/battles. Usage: !wanderer hire | list | info")]
    public class WandererCommand : ICommandHandler
    {
        public class Settings { }
        public Type HandlerConfigType => typeof(Settings);

        public void Execute(ReplyContext context, object config)
        {
            var cfg = WandererGlobalConfig.Get();
            if (cfg == null || !cfg.Enabled) { ActionManager.SendReply(context, "Wanderers are disabled."); return; }

            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (hero == null) { ActionManager.SendReply(context, "You don't have an adopted hero."); return; }

            var args = (context.Args ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
            var behavior = BLTWandererBehavior.Current;
            if (behavior == null) { ActionManager.SendReply(context, "Wanderer system not ready."); return; }
            // Storage key = hero.FirstName (must match the key used at battle spawn, where only the Hero is available).
            string ownerKey = hero.FirstName?.Raw() ?? context.UserName;
            var entries = behavior.GetWanderers(ownerKey);

            switch (sub)
            {
                case "hire":
                {
                    if (entries.Count >= cfg.MaxWanderers) { ActionManager.SendReply(context, $"You already have the maximum of {cfg.MaxWanderers} wanderers."); return; }
                    int gold = BLTAdoptAHeroCampaignBehavior.Current?.GetHeroGold(hero) ?? 0;
                    if (gold < cfg.HireGoldCost) { ActionManager.SendReply(context, $"Not enough BLT gold (need {cfg.HireGoldCost}, have {gold})."); return; }

                    var template = CampaignHelpers.GetWandererTemplates(hero.Culture)?.SelectRandom()
                        ?? CampaignHelpers.AllWandererTemplates.SelectRandom();
                    if (template == null) { ActionManager.SendReply(context, "No wanderer template available."); return; }

                    var newWanderer = HeroCreator.CreateSpecialHero(template);
                    newWanderer.ChangeState(Hero.CharacterStates.Active);
                    var targetSettlement = TaleWorlds.CampaignSystem.Settlements.Settlement.All.Where(s => s.IsTown).SelectRandom();
                    if (targetSettlement != null) EnterSettlementAction.ApplyForCharacterOnly(newWanderer, targetSettlement);

                    // A wanderer must have at least 1 skill point, or it can get killed on load (same rule as the main adopt system).
                    if (newWanderer.GetSkillValue(CampaignHelpers.AllSkillObjects.First()) == 0)
                        newWanderer.HeroDeveloper.SetInitialSkillLevel(CampaignHelpers.AllSkillObjects.First(), 1);

                    BLTAdoptAHeroCampaignBehavior.Current?.ChangeHeroGold(hero, -cfg.HireGoldCost, true);
                    var rec = behavior.Add(ownerKey, newWanderer);
                    var power = WandererPowerCatalog.Pick();
                    rec.Power = power.Id;
                    ActionManager.SendReply(context, $"Recruited wanderer '{newWanderer.Name}' with power [{power.Display}: {power.Desc}] (you now have {entries.Count + 1}/{cfg.MaxWanderers}).");
                    return;
                }
                case "list":
                {
                    if (entries.Count == 0) { ActionManager.SendReply(context, "You have no wanderers. Use '!wanderer hire'."); return; }
                    ActionManager.SendReply(context, string.Join(" | ", entries.Select((e, i) =>
                    {
                        var pd = WandererPowerCatalog.Get(e.Record.Power);
                        return $"{i + 1}:{e.Hero.Name}{(e.Hero.IsDead ? " (dead)" : "")} T{e.Record.Tier}{(pd.HasValue ? $" [{pd.Value.Display}]" : "")}";
                    })));
                    return;
                }
                case "fire":
                {
                    if (!TryIndex(args, entries, out int idx, out string err)) { ActionManager.SendReply(context, err); return; }
                    var name = entries[idx].Hero.Name;
                    behavior.Remove(ownerKey, entries[idx].Record);
                    ActionManager.SendReply(context, $"Dismissed wanderer '{name}'.");
                    return;
                }
                case "name":
                {
                    if (!TryIndex(args, entries, out int idx, out string err)) { ActionManager.SendReply(context, err); return; }
                    if (args.Length < 3) { ActionManager.SendReply(context, "Usage: !wanderer name <number> <new name>"); return; }
                    string newName = string.Join(" ", args.Skip(2));
                    var wandererHero = entries[idx].Hero;
                    wandererHero.SetName(new TaleWorlds.Localization.TextObject(newName), new TaleWorlds.Localization.TextObject(newName));
                    ActionManager.SendReply(context, $"Wanderer {idx + 1} renamed to '{newName}'.");
                    return;
                }
                case "equip":
                {
                    if (!TryIndex(args, entries, out int idx, out string err)) { ActionManager.SendReply(context, err); return; }
                    if (args.Length < 3) { ActionManager.SendReply(context, "Usage: !wanderer equip <number> <item name>"); return; }
                    string itemQuery = string.Join(" ", args.Skip(2));
                    var custom = BLTAdoptAHeroCampaignBehavior.Current.GetCustomItems(hero);
                    var match = custom.FirstOrDefault(e => e.Item != null
                        && e.GetModifiedItemName().ToString().IndexOf(itemQuery, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (match.Item == null) { ActionManager.SendReply(context, $"No custom item of yours matches '{itemQuery}'. Use it in your loadout first."); return; }
                    var wandererHero = entries[idx].Hero;
                    var slot = SlotForItem(match.Item);
                    // Real Hero -> real equipment, applied directly (no override abstraction needed).
                    wandererHero.BattleEquipment[slot] = match;
                    wandererHero.CivilianEquipment[slot] = match;
                    ActionManager.SendReply(context, $"Wanderer {idx + 1} will use '{match.GetModifiedItemName()}' in slot {slot}.");
                    return;
                }
                case "info":
                {
                    if (!TryIndex(args, entries, out int idx, out string err)) { ActionManager.SendReply(context, err); return; }
                    var wandererHero = entries[idx].Hero;
                    var slots = new[]
                    {
                        EquipmentIndex.Weapon0, EquipmentIndex.Weapon1, EquipmentIndex.Weapon2, EquipmentIndex.Weapon3,
                        EquipmentIndex.Head, EquipmentIndex.Body, EquipmentIndex.Leg, EquipmentIndex.Gloves, EquipmentIndex.Cape,
                        EquipmentIndex.Horse, EquipmentIndex.HorseHarness,
                    };
                    var parts = slots
                        .Select(slot => (slot, el: wandererHero.BattleEquipment[slot]))
                        .Where(t => !t.el.IsEmpty && t.el.Item != null)
                        .Select(t => $"{t.slot}: {t.el.GetModifiedItemName()}");
                    string list = string.Join(" | ", parts);
                    var powerDef = WandererPowerCatalog.Get(entries[idx].Record.Power);
                    string powerStr = powerDef.HasValue ? $" | Power: {powerDef.Value.Display} ({powerDef.Value.Desc})" : "";
                    string tierStr = $" | Tier {entries[idx].Record.Tier} ({entries[idx].Record.Kills} kills, {entries[idx].Record.Battles} battles)";
                    ActionManager.SendReply(context, string.IsNullOrEmpty(list)
                        ? $"Wanderer {idx + 1} ({wandererHero.Name}) has no equipment.{powerStr}{tierStr}"
                        : $"Wanderer {idx + 1} ({wandererHero.Name}): {list}{powerStr}{tierStr}");
                    return;
                }
                case "skills":
                {
                    if (!TryIndex(args, entries, out int idx, out string err)) { ActionManager.SendReply(context, err); return; }
                    var wandererHero = entries[idx].Hero;
                    var combatSkills = new[]
                    {
                        DefaultSkills.OneHanded, DefaultSkills.TwoHanded, DefaultSkills.Polearm,
                        DefaultSkills.Bow, DefaultSkills.Crossbow, DefaultSkills.Throwing,
                        DefaultSkills.Riding, DefaultSkills.Athletics,
                    };
                    string skillList = string.Join(" | ", combatSkills
                        .Select(s => $"{s.Name}: {wandererHero.GetSkillValue(s)}"));
                    ActionManager.SendReply(context, $"Wanderer {idx + 1} ({wandererHero.Name}) skills: {skillList}");
                    return;
                }
                default:
                    ActionManager.SendReply(context, "Usage: !wanderer hire | list | fire <n> | name <n> <name> | equip <n> <item> | info <n> | skills <n>");
                    return;
            }
        }

        internal static bool TryIndex(string[] args, List<(WandererRecord Record, Hero Hero)> list, out int idx, out string err)
        {
            idx = -1; err = null;
            if (args.Length < 2 || !int.TryParse(args[1], out int n) || n < 1 || n > list.Count)
            { err = $"Specify a wanderer number 1-{list.Count}."; return false; }
            idx = n - 1; return true;
        }

        internal static EquipmentIndex SlotForItem(ItemObject item)
        {
            foreach (var (slot, itemType) in SkillGroup.ArmorIndexType)
                if (itemType == item.ItemType) return slot;
            return EquipmentIndex.Weapon0; // weapons & shields default to primary weapon slot
        }
    }

    public class WandererSpawnMissionBehavior : MissionBehavior
    {
        public WandererSpawnMissionBehavior() { BLTWandererKillTracker.Reset(); }

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;
        private readonly HashSet<string> spawnedFor = new HashSet<string>();

        // Maps a spawned wanderer's own agent to its owner's Hero, so kills by the wanderer
        // can reward the owner with gold (BLTAdoptAHeroCommonMissionBehavior.ApplyKillEffects
        // is internal to BLTAdoptAHero and not reachable from this assembly, so we track kills
        // ourselves via OnAgentRemoved instead).
        private readonly Dictionary<Agent, Hero> wandererAgentToOwner = new Dictionary<Agent, Hero>();

        // Maps a spawned wanderer's own agent to the WANDERER's own Hero (as opposed to
        // wandererAgentToOwner, which maps to the OWNER's Hero) - needed for self-progression
        // (crediting kills to the correct wanderer's own Kills/Battles/Tier) and for the
        // battle/permanent death-chance system, both keyed off the wanderer itself.
        private readonly Dictionary<Agent, Hero> wandererAgentToHero = new Dictionary<Agent, Hero>();

        // Wanderers that actually spawned into THIS mission, tracked so we can credit a Battle
        // (and roll Tier) for them at mission end, keyed by (ownerKey, wanderer StringId).
        private readonly HashSet<(string ownerKey, string wandererStringId)> spawnedThisMission = new HashSet<(string, string)>();

        // Kills THIS mission per wanderer, keyed by the wanderer Hero's StringId (survives even if
        // the wanderer's agent dies, unlike keying off Agent). Reset per mission implicitly since
        // this whole behavior is recreated each mission.
        private readonly Dictionary<string, int> wandererKillCounts = new Dictionary<string, int>();

        // Power bookkeeping - keyed off the wanderer's OWN spawned agent (not the owner's).
        private readonly Dictionary<Agent, string> wandererAgentPower = new Dictionary<Agent, string>();
        private readonly HashSet<Agent> secondWindUsed = new HashSet<Agent>();
        private readonly HashSet<Agent> berserkActive = new HashSet<Agent>();
        private readonly Dictionary<Agent, float> battleCryNextPulse = new Dictionary<Agent, float>();

        // Deferred-spawn queue: OnAgentCreated only QUEUES; the actual spawn happens on a later
        // OnMissionTick once the owner agent is fully built (team/formation/observer wiring ready).
        private class PendingSpawn
        {
            public Agent OwnerAgent;
            public Hero Owner;
            public Hero Wanderer;
            public WandererRecord Record;
        }
        private readonly List<PendingSpawn> pending = new List<PendingSpawn>();

        // Permanent Death Chance revival queue: spawning a fresh Hero must never happen mid-dispatch
        // (see the Necromancy crash fix - SpawnTroop-adjacent engine calls inside OnAgentRemoved
        // corrupt the engine's own agent-list enumeration), so a "wanderer comes back" decision made
        // in OnAgentRemoved is only QUEUED here; the actual HeroCreator.CreateSpecialHero call happens
        // on the next OnMissionTick.
        private class PendingRevive
        {
            public WandererRecord Record;
            public Hero DeadHero;
        }
        private readonly List<PendingRevive> pendingRevives = new List<PendingRevive>();

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            try
            {
                if (wandererAgentToOwner.Count > 0 && affectedAgent != null)
                {
                    Log.Info($"[Wanderer] OnAgentRemoved: affected={affectedAgent.Name} state={agentState} affector={affectorAgent?.Name ?? "null"} isTrackedWanderer={(affectorAgent != null && wandererAgentToOwner.ContainsKey(affectorAgent))}");
                }

                // Credit a kill to the wanderer's OWN kill counter (self-progression), independent
                // of the owner-gold-reward logic further down.
                if (agentState == AgentState.Killed && affectorAgent != null
                    && wandererAgentToHero.TryGetValue(affectorAgent, out var killerWandererHero) && killerWandererHero != null)
                {
                    wandererKillCounts.TryGetValue(killerWandererHero.StringId, out int killCur);
                    wandererKillCounts[killerWandererHero.StringId] = killCur + 1;
                }

                // Permanent Death Chance: only relevant when the REMOVED agent is itself a tracked
                // wanderer that actually died (Battle Death Chance in OnAgentHit already gave it a
                // chance to survive the hit entirely - reaching here means that roll failed).
                if (affectedAgent != null && agentState == AgentState.Killed
                    && wandererAgentToHero.TryGetValue(affectedAgent, out var deadWandererHero) && deadWandererHero != null)
                {
                    var record = BLTWandererBehavior.Current?.FindRecordByHeroStringId(deadWandererHero.StringId);
                    if (record != null)
                    {
                        var deathCfg = WandererGlobalConfig.Get();
                        float permanentChance = deathCfg?.PermanentDeathChancePercent ?? 100f;
                        if (MBRandom.RandomFloat * 100f < permanentChance)
                        {
                            // Permanent: the Hero itself still dies normally through the campaign's
                            // own death resolution - we only reset OUR tracked progress.
                            record.Kills = 0;
                            record.Battles = 0;
                            record.Tier = 1;
                        }
                        else
                        {
                            pendingRevives.Add(new PendingRevive { Record = record, DeadHero = deadWandererHero });
                        }
                    }
                }

                if (affectorAgent == null || agentState != AgentState.Killed) return;
                if (!wandererAgentToOwner.TryGetValue(affectorAgent, out var ownerHero) || ownerHero == null) return;
                BLTWandererKillTracker.Add(ownerHero);
                var cfg = WandererGlobalConfig.Get();
                Log.Info($"[Wanderer] Kill by wanderer detected! owner={ownerHero.Name} GoldPerKill={cfg?.GoldPerKill}");

                if (wandererAgentPower.TryGetValue(affectorAgent, out var power) && affectorAgent.IsActive())
                {
                    try
                    {
                        switch (power)
                        {
                            case "Vampirism":
                                if (affectorAgent.HealthLimit > 0f)
                                    affectorAgent.Health = Math.Min(affectorAgent.HealthLimit, affectorAgent.Health + affectorAgent.HealthLimit * 0.20f * GetWandererPowerScale(affectorAgent));
                                break;
                            case "Bloodrage":
                            {
                                float bloodrageBonus = 30f * GetWandererPowerScale(affectorAgent);
                                var buffCfg = new AgentModifierConfig();
                                buffCfg.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.SwingSpeedMultiplier, ModifierPercent = 100f + bloodrageBonus });
                                BLTTimedBuffBehavior.AddTimedBuff(affectorAgent, buffCfg, 8f);
                                break;
                            }
                        }
                    }
                    catch (Exception ex) { Log.Exception("WandererSpawnMissionBehavior.OnAgentRemoved.PowerKill", ex); }
                }

                if (cfg == null || cfg.GoldPerKill <= 0) return;
                BLTAdoptAHeroCampaignBehavior.Current?.ChangeHeroGold(ownerHero, cfg.GoldPerKill, true);
                Log.ShowInformation($"+{cfg.GoldPerKill} gold ({ownerHero.FirstName}'s wanderer got a kill)", ownerHero.CharacterObject);
            }
            catch (Exception ex) { Log.Exception("WandererSpawnMissionBehavior.OnAgentRemoved", ex); }
        }

        public override void OnAgentHit(Agent affectedAgent, Agent affectorAgent, in MissionWeapon affectorWeapon,
            in Blow blow, in AttackCollisionData attackCollisionData)
        {
            try
            {
                if (affectedAgent == null || !affectedAgent.IsActive()) return;

                // Battle Death Chance: applies to ANY tracked wanderer (independent of its rolled
                // power). When a hit would drop its HP to/below 0, roll BattleDeathChancePercent.
                // On failure (the common case at the low default), clamp HP to a small positive
                // value instead of letting the hit be lethal - same "survive at low HP" pattern
                // used by Second Wind just below, but unconditional on power.
                if (wandererAgentToHero.ContainsKey(affectedAgent) && affectedAgent.Health <= 0f)
                {
                    var deathCfg = WandererGlobalConfig.Get();
                    float battleDeathChance = deathCfg?.BattleDeathChancePercent ?? 3f;
                    if (MBRandom.RandomFloat * 100f >= battleDeathChance)
                    {
                        affectedAgent.Health = Math.Max(1f, affectedAgent.HealthLimit * 0.01f);
                    }
                }

                if (!wandererAgentPower.TryGetValue(affectedAgent, out var power) || power != "SecondWind") return;
                if (secondWindUsed.Contains(affectedAgent)) return;
                if (affectedAgent.HealthLimit <= 0f) return;
                float hpPct = affectedAgent.Health / affectedAgent.HealthLimit * 100f;
                if (hpPct > 25f) return;

                secondWindUsed.Add(affectedAgent);
                float healFraction = Math.Min(1f, 0.6f * GetWandererPowerScale(affectedAgent));
                affectedAgent.Health = Math.Min(affectedAgent.HealthLimit, affectedAgent.HealthLimit * healFraction);
                var buffCfg = new AgentModifierConfig();
                buffCfg.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.MaxSpeedMultiplier, ModifierPercent = 130f });
                buffCfg.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.SwingSpeedMultiplier, ModifierPercent = 120f });
                BLTTimedBuffBehavior.AddTimedBuff(affectedAgent, buffCfg, 5f);
            }
            catch (Exception ex) { Log.Exception("WandererSpawnMissionBehavior.OnAgentHit", ex); }
        }

        public override void OnAgentCreated(Agent agent)
        {
            try
            {
                var cfg = WandererGlobalConfig.Get();
                if (cfg == null || !cfg.Enabled) { return; }
                // Only spawn in actual combat missions (skip conversations, menus, character viewer, town walk-arounds).
                var mission = Mission.Current;
                if (mission == null || mission.CombatType == Mission.MissionCombatType.NoCombat) { return; }
                var hero = agent.GetAdoptedHero();
                if (hero == null) return; // fires for many non-hero agents
                var behavior = BLTWandererBehavior.Current;
                if (behavior == null) { return; }

                // Storage key resolved via hero.FirstName (matches the key used at hire time in WandererCommand).
                string ownerKey = hero.FirstName?.Raw();
                if (string.IsNullOrEmpty(ownerKey)) { return; }
                if (!spawnedFor.Add(ownerKey)) { return; }

                foreach (var (record, wandererHero) in behavior.GetWanderers(ownerKey))
                {
                    if (wandererHero == null || wandererHero.IsDead) continue;
                    UpgradeWandererEquipment(wandererHero, record?.Tier ?? 1);
                    spawnedThisMission.Add((ownerKey, wandererHero.StringId));
                    pending.Add(new PendingSpawn { OwnerAgent = agent, Owner = hero, Wanderer = wandererHero, Record = record });
                }
            }
            catch (Exception ex) { Log.Exception("WandererSpawnMissionBehavior.OnAgentCreated", ex); }
        }

        // Applies this mission's accumulated kills/battle-count to every wanderer that spawned into
        // it, and recomputes their Tier. Dead wanderers are skipped here - their progress reset (or
        // preservation via revival) is handled separately by the death-chance logic in OnAgentRemoved.
        protected override void OnEndMission()
        {
            try
            {
                var cfg = WandererGlobalConfig.Get();
                var behavior = BLTWandererBehavior.Current;
                if (cfg == null || behavior == null) return;

                foreach (var (ownerKey, wandererStringId) in spawnedThisMission)
                {
                    var entries = behavior.GetWanderers(ownerKey);
                    var match = entries.FirstOrDefault(e => e.Hero?.StringId == wandererStringId);
                    if (match.Record == null || match.Hero == null || match.Hero.IsDead) continue;

                    int wandererKillsThisMission = wandererKillCounts.TryGetValue(match.Hero.StringId, out int k) ? k : 0;
                    match.Record.Kills += wandererKillsThisMission;
                    match.Record.Battles += 1;
                    match.Record.Tier = WandererTierCalculator.ComputeTier(match.Record.Kills, match.Record.Battles, cfg);
                }
            }
            catch (Exception ex) { Log.Exception("WandererSpawnMissionBehavior.OnEndMission", ex); }
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            if (pending.Count > 0)
            {
                try
                {
                    // Spawn only owners that are fully built this tick. Re-queue (leave) the rest.
                    for (int i = pending.Count - 1; i >= 0; i--)
                    {
                        var p = pending[i];
                        var owner = p.OwnerAgent;
                        // Owner must be live and wired (team + formation) before we spawn next to it.
                        bool ready = owner != null && owner.IsActive() && owner.Team != null && owner.Formation != null;
                        if (!ready)
                        {
                            // If the owner agent died/was removed before becoming ready, drop it.
                            if (owner == null || (!owner.IsActive() && owner.Health <= 0f))
                            {
                                pending.RemoveAt(i);
                            }
                            continue; // wait another tick
                        }
                        pending.RemoveAt(i);
                        SpawnWanderer(owner, p.Owner, p.Wanderer, p.Record);
                    }
                }
                catch (Exception ex) { Log.Exception("WandererSpawnMissionBehavior.OnMissionTick", ex); }
            }

            if (pendingRevives.Count > 0)
            {
                try
                {
                    foreach (var r in pendingRevives)
                    {
                        try
                        {
                            var template = CampaignHelpers.GetWandererTemplates(r.DeadHero.Culture)?.SelectRandom()
                                        ?? CampaignHelpers.AllWandererTemplates.SelectRandom();
                            if (template == null) continue;

                            var revived = HeroCreator.CreateSpecialHero(template);
                            revived.ChangeState(Hero.CharacterStates.Active);
                            var targetSettlement = TaleWorlds.CampaignSystem.Settlements.Settlement.All.Where(s => s.IsTown).SelectRandom();
                            if (targetSettlement != null) EnterSettlementAction.ApplyForCharacterOnly(revived, targetSettlement);
                            if (revived.GetSkillValue(CampaignHelpers.AllSkillObjects.First()) == 0)
                                revived.HeroDeveloper.SetInitialSkillLevel(CampaignHelpers.AllSkillObjects.First(), 1);

                            // Copy equipment directly (Kills/Battles/Tier already live on the Record,
                            // which we keep unchanged - only HeroStringId needs to point at the new Hero).
                            for (var idx = EquipmentIndex.Weapon0; idx < EquipmentIndex.NumEquipmentSetSlots; idx++)
                            {
                                revived.BattleEquipment[idx] = r.DeadHero.BattleEquipment[idx];
                                revived.CivilianEquipment[idx] = r.DeadHero.CivilianEquipment[idx];
                            }

                            r.Record.HeroStringId = revived.StringId;
                            Log.ShowInformation($"A wanderer cheats death and returns, remembering everything!", revived.CharacterObject);
                        }
                        catch (Exception ex) { Log.Exception("WandererSpawnMissionBehavior.ProcessPendingRevive", ex); }
                    }
                    pendingRevives.Clear();
                }
                catch (Exception ex) { Log.Exception("WandererSpawnMissionBehavior.OnMissionTick.Revives", ex); }
            }

            if (wandererAgentPower.Count > 0)
            {
                try
                {
                    float now = Mission.Current?.CurrentTime ?? 0f;
                    foreach (var kv in wandererAgentPower.ToList())
                    {
                        var agent = kv.Key;
                        var power = kv.Value;
                        if (agent == null || !agent.IsActive()) { wandererAgentPower.Remove(agent); continue; }
                        switch (power)
                        {
                            case "Juggernaut":
                                if (agent.HealthLimit > 0f)
                                    agent.Health = Math.Min(agent.HealthLimit, agent.Health + 0.5f * GetWandererPowerScale(agent) * dt);
                                break;
                            case "Berserk":
                            {
                                if (agent.HealthLimit <= 0f) break;
                                float hpPct = agent.Health / agent.HealthLimit * 100f;
                                bool shouldBeActive = hpPct <= 50f;
                                bool isActive = berserkActive.Contains(agent);
                                float berserkBonus = 40f * GetWandererPowerScale(agent);
                                if (shouldBeActive && !isActive)
                                {
                                    var buffCfg = new AgentModifierConfig();
                                    buffCfg.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.SwingSpeedMultiplier, ModifierPercent = 100f + berserkBonus });
                                    buffCfg.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.MaxSpeedMultiplier, ModifierPercent = 100f + berserkBonus });
                                    BLTAgentModifierBehavior.Current?.Add(agent, buffCfg);
                                    berserkActive.Add(agent);
                                }
                                else if (!shouldBeActive && isActive)
                                {
                                    var negCfg = new AgentModifierConfig();
                                    negCfg.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.SwingSpeedMultiplier, ModifierPercent = 10000f / (100f + berserkBonus) });
                                    negCfg.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.MaxSpeedMultiplier, ModifierPercent = 10000f / (100f + berserkBonus) });
                                    BLTAgentModifierBehavior.Current?.Add(agent, negCfg);
                                    berserkActive.Remove(agent);
                                }
                                break;
                            }
                            case "BattleCry":
                            {
                                if (!battleCryNextPulse.TryGetValue(agent, out var next))
                                {
                                    battleCryNextPulse[agent] = now + 25f;
                                    break;
                                }
                                if (now >= next)
                                {
                                    battleCryNextPulse[agent] = now + 25f;
                                    float battleCryBonus = 25f * GetWandererPowerScale(agent);
                                    var buffCfg = new AgentModifierConfig();
                                    buffCfg.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.SwingSpeedMultiplier, ModifierPercent = 100f + battleCryBonus });
                                    buffCfg.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.MaxSpeedMultiplier, ModifierPercent = 100f + battleCryBonus });
                                    BLTTimedBuffBehavior.AddTimedBuff(agent, buffCfg, 6f);
                                }
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex) { Log.Exception("WandererSpawnMissionBehavior.PowerTick", ex); }
            }
        }

        // Resolves the current Tier-based magnitude scale for a wanderer's own spawned agent.
        // Returns 1.0 (no scaling) if the agent/record can't be resolved, so this never throws
        // and never blocks a power from firing at its base strength.
        private float GetWandererPowerScale(Agent wandererAgent)
        {
            try
            {
                if (wandererAgent == null) return 1f;
                if (!wandererAgentToHero.TryGetValue(wandererAgent, out var wandererHero) || wandererHero == null) return 1f;
                var record = BLTWandererBehavior.Current?.FindRecordByHeroStringId(wandererHero.StringId);
                return record != null ? WandererPowerScaling.MagnitudeScale(record.Tier) : 1f;
            }
            catch { return 1f; }
        }

        // A wanderer is a REAL Hero, so its own CharacterObject already reflects its real
        // equipment/skills/appearance - no reflection-built mirror character needed at all.
        // Upgrades the wanderer's own equipment to match its own Tier (1-8, from self-progression),
        // independent of the owner's gear. For each slot the wanderer already has something equipped
        // in, looks up another item of the SAME ItemType at the wanderer's OWN Tier and equips that
        // instead (never downgrades - if the current item is already at or above the target tier,
        // that slot is left alone). Runs once at hire/spawn and again whenever Tier increases.
        private static void UpgradeWandererEquipment(Hero wanderer, int tier)
        {
            try
            {
                for (var idx = EquipmentIndex.Weapon0; idx < EquipmentIndex.NumEquipmentSetSlots; idx++)
                {
                    var current = wanderer.BattleEquipment[idx];
                    if (current.IsEmpty || current.Item == null) continue;
                    if ((int)current.Item.Tier >= tier) continue; // already at or above target (ItemTiers is an enum, compare as int)

                    var upgraded = TaleWorlds.ObjectSystem.MBObjectManager.Instance
                        .GetObjectTypeList<ItemObject>()
                        .Where(i => i.ItemType == current.Item.ItemType && (int)i.Tier == tier)
                        .ToList()
                        .SelectRandom();
                    if (upgraded == null) continue;

                    var element = new EquipmentElement(upgraded);
                    wanderer.BattleEquipment[idx] = element;
                    wanderer.CivilianEquipment[idx] = element;
                }
            }
            catch (Exception ex) { Log.Exception("UpgradeWandererEquipment", ex); }
        }

        private void SpawnWanderer(Agent ownerAgent, Hero owner, Hero wanderer, WandererRecord record)
        {
            try
            {
                bool mounted = !wanderer.BattleEquipment[EquipmentIndex.Horse].IsEmpty;
                var playerTeam = Mission.Current.PlayerTeam;
                bool onPlayerSide = ownerAgent.Team != null && playerTeam != null && ownerAgent.Team.Side == playerTeam.Side;

                // Resolve a non-null PartyBase for PartyAgentOrigin. Adopted/summoned BLT heroes
                // usually have NO PartyBelongedTo (they are reinforcements, not party leaders), so
                // mirror BLTSummonBehavior/SummonHero: reuse the owner agent's origin party, else
                // fall back to MainParty on the player side / an enemy combatant party.
                PartyBase party = ownerAgent.Origin is TaleWorlds.CampaignSystem.AgentOrigins.PartyAgentOrigin pao ? pao.Party : null;
                if (party == null && ownerAgent.Origin?.BattleCombatant is PartyBase pbc)
                    party = pbc;
                if (party == null && onPlayerSide)
                    party = PartyBase.MainParty;
                if (party == null && ownerAgent.Team != null)
                {
                    foreach (var a in ownerAgent.Team.TeamAgents)
                    {
                        if (a?.Origin?.BattleCombatant is PartyBase ep) { party = ep; break; }
                    }
                }
                if (party == null) { return; }

                Vec3? spawnPos = ownerAgent.Position;
                Vec2? spawnDir = ownerAgent.GetMovementDirection();
                Agent agent = Mission.Current.SpawnTroop(
                    new TaleWorlds.CampaignSystem.AgentOrigins.PartyAgentOrigin(party, wanderer.CharacterObject)
                    , isPlayerSide: onPlayerSide
                    , hasFormation: true
                    , spawnWithHorse: mounted
                    , isReinforcement: false
                    , formationTroopCount: 1
                    , formationTroopIndex: 0
                    , isAlarmed: true
                    , wieldInitialWeapons: true
                    , forceDismounted: false
                    , initialPosition: spawnPos
                    , initialDirection: spawnDir
                );
                if (agent == null) { return; }
                wandererAgentToOwner[agent] = owner;
                wandererAgentToHero[agent] = wanderer;
                Log.Info($"[Wanderer] Spawned {wanderer.Name} for owner {owner.Name}, tracking agent {agent.Name} for kill-gold.");

                if (record != null)
                {
                    if (string.IsNullOrEmpty(record.Power))
                        record.Power = WandererPowerCatalog.Pick().Id; // lazy-assign for wanderers hired before this feature existed
                    wandererAgentPower[agent] = record.Power;
                    try
                    {
                        float onSpawnScale = WandererPowerScaling.MagnitudeScale(record.Tier);
                        switch (record.Power)
                        {
                            case "IronSkin":
                            {
                                float ironSkinBonus = 25f * onSpawnScale;
                                var cfg = new AgentModifierConfig();
                                cfg.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.ArmorHead, ModifierPercent = 100f + ironSkinBonus });
                                BLTAgentModifierBehavior.Current?.Add(agent, cfg);
                                break;
                            }
                            case "SwiftHunter":
                            {
                                float swiftBonus = 20f * onSpawnScale;
                                var cfg = new AgentModifierConfig();
                                cfg.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.MaxSpeedMultiplier, ModifierPercent = 100f + swiftBonus });
                                cfg.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.CombatMaxSpeedMultiplier, ModifierPercent = 100f + swiftBonus });
                                BLTAgentModifierBehavior.Current?.Add(agent, cfg);
                                break;
                            }
                        }
                    }
                    catch (Exception ex) { Log.Exception("WandererSpawnMissionBehavior.ApplyOnSpawnPower", ex); }
                }

                if (ownerAgent.Team != null && agent.Team != ownerAgent.Team)
                {
                    agent.SetTeam(ownerAgent.Team, true);
                }
                if (ownerAgent.Formation != null)
                {
                    agent.Formation = ownerAgent.Formation;
                }

                // Nudge right next to the owner so it is clearly visible fighting alongside them.
                try
                {
                    if (ownerAgent.IsActive())
                        agent.TeleportToPosition(ownerAgent.Position);
                }
                catch (Exception ex) { Log.Exception("WandererSpawnMissionBehavior.Reposition", ex); }
            }
            catch (Exception ex) { Log.Exception("WandererSpawnMissionBehavior.SpawnWanderer", ex); }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // ADRENALINE
    // When a BLT hero is hit and drops to/below the HP threshold, it has a chance to
    // trigger an adrenaline rush: temporary attack/move/ranged speed buff + HP regen,
    // once per battle. Uses the proven ADP-mutation mechanism
    // (AgentDrivenProperties.SetStat/GetStat + UpdateCustomDrivenProperties, storing
    // originals to restore) and BerserkPower's contour technique.
    // ══════════════════════════════════════════════════════════════════════════════
    public class AdrenalineMissionBehavior : MissionBehavior
    {
        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        private static readonly DrivenProperty[] SpeedProperties =
        {
            DrivenProperty.MaxSpeedMultiplier,
            DrivenProperty.CombatMaxSpeedMultiplier
        };

        private static readonly DrivenProperty[] AttackProperties =
        {
            DrivenProperty.SwingSpeedMultiplier,
            DrivenProperty.HandlingMultiplier
        };

        private static readonly DrivenProperty[] RangedProperties =
        {
            DrivenProperty.ReloadSpeed
        };

        private readonly Dictionary<Agent, float> activeUntil = new Dictionary<Agent, float>();
        private readonly Dictionary<Agent, Dictionary<DrivenProperty, float>> originalStats =
            new Dictionary<Agent, Dictionary<DrivenProperty, float>>();
        private readonly HashSet<Hero> triggeredThisBattle = new HashSet<Hero>();
        private bool damageTrackerReset = false;

        public override void OnAgentHit(Agent affectedAgent, Agent affectorAgent, in MissionWeapon affectorWeapon,
            in Blow blow, in AttackCollisionData attackCollisionData)
        {
            try
            {
                var cfg = AdrenalineGlobalConfig.Get();
                if (cfg == null || !cfg.Enabled) return;
                if (!cfg.AllowInTournaments && MissionHelpers.InTournament()) return;
                if (affectedAgent == null || !affectedAgent.IsActive() || !affectedAgent.IsHuman) return;

                var hero = affectedAgent.GetAdoptedHero();
                if (hero == null) return;
                if (triggeredThisBattle.Contains(hero)) return;     // once per battle (after activation)
                if (activeUntil.ContainsKey(affectedAgent)) return;  // already active

                float maxhp = affectedAgent.HealthLimit;
                float hpPct = maxhp > 0f ? (affectedAgent.Health / maxhp) * 100f : 100f;
                if (hpPct > cfg.HpThresholdPercent) return;

                // Roll the chance. On failure, do NOT consume the trigger — later qualifying hits may re-roll.
                if (MBRandom.RandomFloat * 100f > cfg.AdrenalineChance) return;

                Activate(affectedAgent, hero, cfg);
            }
            catch (Exception ex) { Log.Exception("AdrenalineMissionBehavior.OnAgentHit", ex); }
        }

        private void Activate(Agent agent, Hero hero, AdrenalineGlobalConfig cfg)
        {
            float now = Mission.Current?.CurrentTime ?? 0f;
            activeUntil[agent] = now + cfg.DurationSeconds;
            triggeredThisBattle.Add(hero);

            ApplyBuff(agent, cfg);

            try { SafeSetContourColor(agent, Convert.ToUInt32("FFFF0000", 16), true); } catch { }

            Log.ShowInformation($"⚡ Adrenaline: {hero.FirstName}!", hero.CharacterObject);
        }

        private void ApplyBuff(Agent agent, AdrenalineGlobalConfig cfg)
        {
            if (agent.AgentDrivenProperties == null) return;

            // Capture originals so Deactivate restores exactly.
            var snapshot = new Dictionary<DrivenProperty, float>();
            foreach (var p in SpeedProperties.Concat(AttackProperties).Concat(RangedProperties).Distinct())
                snapshot[p] = agent.AgentDrivenProperties.GetStat(p);
            originalStats[agent] = snapshot;

            float moveMult = 1f + Math.Max(0f, cfg.MoveSpeedBonusPercent) / 100f;
            float attackMult = 1f + Math.Max(0f, cfg.AttackSpeedBonusPercent) / 100f;
            float rangedMult = 1f + Math.Max(0f, cfg.RangedSpeedBonusPercent) / 100f;

            foreach (var p in SpeedProperties)
                agent.AgentDrivenProperties.SetStat(p, agent.AgentDrivenProperties.GetStat(p) * moveMult);
            foreach (var p in AttackProperties)
                agent.AgentDrivenProperties.SetStat(p, agent.AgentDrivenProperties.GetStat(p) * attackMult);
            foreach (var p in RangedProperties)
                agent.AgentDrivenProperties.SetStat(p, agent.AgentDrivenProperties.GetStat(p) * rangedMult);

            agent.UpdateCustomDrivenProperties();
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            if (!damageTrackerReset) { damageTrackerReset = true; BLTDamageTracker.Reset(); }
            try
            {
                var mission = Mission.Current;
                if (mission == null || mission.CombatType == Mission.MissionCombatType.NoCombat) return;
                if (activeUntil.Count == 0) return;

                var cfg = AdrenalineGlobalConfig.Get();
                float regenPerSecond = cfg?.HpRegenPerSecond ?? 0f;
                float now = mission.CurrentTime;

                // Iterate a copy so we can remove from the dictionary while looping.
                foreach (var pair in activeUntil.ToList())
                {
                    var agent = pair.Key;
                    float expiry = pair.Value;

                    if (agent == null || !agent.IsActive() || now >= expiry)
                    {
                        Deactivate(agent);
                        activeUntil.Remove(agent);
                        continue;
                    }

                    if (regenPerSecond > 0f && agent.HealthLimit > 0f)
                        agent.Health = Math.Min(agent.HealthLimit, agent.Health + regenPerSecond * dt);
                }
            }
            catch (Exception ex) { Log.Exception("AdrenalineMissionBehavior.OnMissionTick", ex); }
        }

        private void Deactivate(Agent agent)
        {
            if (agent != null && originalStats.TryGetValue(agent, out var snapshot))
            {
                try
                {
                    if (agent.IsActive() && agent.AgentDrivenProperties != null)
                    {
                        foreach (var stat in snapshot)
                            agent.AgentDrivenProperties.SetStat(stat.Key, stat.Value);
                        agent.UpdateCustomDrivenProperties();
                    }
                }
                catch { }
            }
            if (agent != null) originalStats.Remove(agent);
            SafeSetContourColor(agent, null, false); // guards null/IsActive internally - see ContourHelpers
        }

        public override void OnAgentDeleted(Agent affectedAgent)
        {
            base.OnAgentDeleted(affectedAgent);
            if (affectedAgent == null) return;
            originalStats.Remove(affectedAgent);
            activeUntil.Remove(affectedAgent);
        }

        public static bool IsAdrenalineActive(Agent agent)
        {
            if (agent == null) return false;
            var beh = Mission.Current?.GetMissionBehavior<AdrenalineMissionBehavior>();
            if (beh == null) return false;
            return beh.activeUntil.ContainsKey(agent);
        }

        public static float RemainingFraction(Hero hero)
        {
            try
            {
                if (hero == null) return 0f;
                var beh = Mission.Current?.GetMissionBehavior<AdrenalineMissionBehavior>();
                if (beh == null) return 0f;
                var cfg = AdrenalineGlobalConfig.Get();
                float dur = cfg != null ? cfg.DurationSeconds : 60f;
                if (dur <= 0f) return 0f;
                float now = Mission.Current?.CurrentTime ?? 0f;
                foreach (var kv in beh.activeUntil)
                {
                    if (kv.Key != null && kv.Key.IsActive() && kv.Key.GetAdoptedHero() == hero)
                    {
                        float remain = kv.Value - now;
                        if (remain <= 0f) return 0f;
                        return remain / dur > 1f ? 1f : remain / dur;
                    }
                }
                return 0f;
            }
            catch { return 0f; }
        }
    }

    public static class BLTDamageTracker
    {
        private static readonly Dictionary<Hero, int> dmg = new Dictionary<Hero, int>();
        public static void Reset() { dmg.Clear(); }
        public static void Add(Hero hero, int amount)
        {
            if (hero == null || amount <= 0) return;
            int cur;
            dmg.TryGetValue(hero, out cur);
            dmg[hero] = cur + amount;
        }
        public static int Get(Hero hero)
        {
            if (hero == null) return 0;
            int v;
            return dmg.TryGetValue(hero, out v) ? v : 0;
        }
    }

    // Tracks kills made by a viewer's wanderer(s) during the current mission (resets each battle,
    // same lifecycle as BLTDamageTracker), so the count can be shown on the hero bar/overlay.
    public static class BLTWandererKillTracker
    {
        private static readonly Dictionary<Hero, int> kills = new Dictionary<Hero, int>();
        public static void Reset() { kills.Clear(); }
        public static void Add(Hero owner)
        {
            if (owner == null) return;
            int cur;
            kills.TryGetValue(owner, out cur);
            kills[owner] = cur + 1;
        }
        public static int Get(Hero owner)
        {
            if (owner == null) return 0;
            int v;
            return kills.TryGetValue(owner, out v) ? v : 0;
        }
    }

    // Computes a wanderer's Tier (1-8) from its lifetime Kills/Battles against the configured
    // per-tier thresholds. A tier is reached when EITHER the kill OR the battle threshold for it
    // is met (same kills-OR-battles pattern as PowerProgression for hero powers).
    public static class WandererTierCalculator
    {
        public static int ComputeTier(int kills, int battles, WandererGlobalConfig cfg)
        {
            var killThresholds = ParseCsv(cfg?.TierKillThresholds);
            var battleThresholds = ParseCsv(cfg?.TierBattleThresholds);
            int tier = 1;
            for (int i = 0; i < killThresholds.Count && i < battleThresholds.Count; i++)
            {
                if (kills >= killThresholds[i] || battles >= battleThresholds[i])
                    tier = i + 2; // thresholds[0] is the requirement for tier 2, etc.
                else
                    break;
            }
            return Math.Min(8, Math.Max(1, tier));
        }

        private static List<int> ParseCsv(string csv)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(csv)) return result;
            foreach (var part in csv.Split(','))
                if (int.TryParse(part.Trim(), out int v)) result.Add(v);
            return result;
        }
    }

    // +15% magnitude per tier above 1 (Tier 1 = 1.0x, Tier 8 = 2.05x), used to scale the 8
    // hand-rolled wanderer powers by the wanderer's own progression Tier.
    public static class WandererPowerScaling
    {
        public static float MagnitudeScale(int tier) => 1f + Math.Max(0, tier - 1) * 0.15f;
    }

    public class WandererGlobalConfig
    {
        private const string ID = "MBGA - Wanderers";
        internal static void Register() => ActionManager.RegisterGlobalConfigType(ID, typeof(WandererGlobalConfig));
        internal static WandererGlobalConfig Get() => ActionManager.GetGlobalConfig<WandererGlobalConfig>(ID);

        [DisplayName("Enabled"), Description("Master switch for the wanderer companion system"), UsedImplicitly]
        public bool Enabled { get; set; } = true;

        [DisplayName("Hire Gold Cost"), Description("Gold to recruit one wanderer"), UsedImplicitly]
        public int HireGoldCost { get; set; } = 5000;

        [DisplayName("Max Wanderers"), Description("Maximum wanderers per hero"), UsedImplicitly]
        public int MaxWanderers { get; set; } = 1;

        [DisplayName("Gold Per Kill"), Description("Gold awarded to the owner (viewer) each time their wanderer kills an enemy. Set to 0 to disable."), UsedImplicitly]
        public int GoldPerKill { get; set; } = 300;

        [DisplayName("Tier Kill Thresholds"), Description("Comma-separated kills required to reach tier 2,3,4,5,6,7,8 (7 numbers). A wanderer reaches a tier when its kills OR battles meet that tier's threshold."), UsedImplicitly]
        public string TierKillThresholds { get; set; } = "5,12,22,35,50,70,95";

        [DisplayName("Tier Battle Thresholds"), Description("Comma-separated battles required to reach tier 2,3,4,5,6,7,8 (7 numbers)."), UsedImplicitly]
        public string TierBattleThresholds { get; set; } = "3,7,12,18,25,33,42";

        [DisplayName("Battle Death Chance (%)"), Description("Chance a killing blow actually kills the wanderer in this battle. Low by default - most 'fatal' hits are survived."), UsedImplicitly]
        public float BattleDeathChancePercent { get; set; } = 3f;

        [DisplayName("Permanent Death Chance (%)"), Description("Only rolled if Battle Death Chance already resulted in death. 100% = any real death is permanent (today's behavior). Lower this to let a wanderer sometimes come back with its Kills/Battles/Tier intact."), UsedImplicitly]
        public float PermanentDeathChancePercent { get; set; } = 100f;
    }

    public class AdrenalineGlobalConfig
    {
        private const string ID = "MBGA - Adrenaline";
        internal static void Register() => ActionManager.RegisterGlobalConfigType(ID, typeof(AdrenalineGlobalConfig));
        internal static AdrenalineGlobalConfig Get() => ActionManager.GetGlobalConfig<AdrenalineGlobalConfig>(ID);

        [DisplayName("Enabled"), Description("Master switch for the adrenaline system"), UsedImplicitly]
        public bool Enabled { get; set; } = true;

        [DisplayName("Allow In Tournaments"), Description("Whether adrenaline can trigger during tournament fights. Off by default, matching how other BLT powers are disabled in tournaments."), UsedImplicitly]
        public bool AllowInTournaments { get; set; } = false;

        [DisplayName("Adrenaline Chance (%)"), Description("Chance to trigger when HP first drops to the threshold"), UsedImplicitly]
        public float AdrenalineChance { get; set; } = 25f;

        [DisplayName("HP Threshold (%)"), Description("Adrenaline can trigger when the hero's HP drops to or below this percent of max"), UsedImplicitly]
        public float HpThresholdPercent { get; set; } = 50f;

        [DisplayName("Duration (seconds)"), Description("How long adrenaline lasts"), UsedImplicitly]
        public float DurationSeconds { get; set; } = 60f;

        [DisplayName("Attack Speed Bonus (%)"), Description("Melee swing speed bonus while active"), UsedImplicitly]
        public float AttackSpeedBonusPercent { get; set; } = 50f;

        [DisplayName("Move Speed Bonus (%)"), Description("Movement speed bonus while active"), UsedImplicitly]
        public float MoveSpeedBonusPercent { get; set; } = 50f;

        [DisplayName("Ranged Speed Bonus (%)"), Description("Bow/crossbow draw & reload speed bonus while active"), UsedImplicitly]
        public float RangedSpeedBonusPercent { get; set; } = 50f;

        [DisplayName("HP Regen Per Second"), Description("Health regenerated per second while active"), UsedImplicitly]
        public float HpRegenPerSecond { get; set; } = 1f;

        [DisplayName("Mounted Charge Damage Multiplier"), Description("Charge damage multiplier while mounted and active (3.0 = +300%)"), UsedImplicitly]
        public float MountedChargeDamageMultiplier { get; set; } = 3f;
    }

    [DisplayName("MBGA - Hero Bar")]
    public class HeroBarGlobalConfig
    {
        private const string ID = "MBGA - Hero Bar";
        internal static void Register() => ActionManager.RegisterGlobalConfigType(ID, typeof(HeroBarGlobalConfig));
        internal static HeroBarGlobalConfig Get() => ActionManager.GetGlobalConfig<HeroBarGlobalConfig>(ID);

        [DisplayName("1. Overlay On/Off (OBS Browser Source)"),
         Description("MASTER SWITCH for the browser-based hero overlay you add as an OBS Browser Source. Turn OFF to hide it completely, no matter what the settings below say. Does NOT affect the in-game nameplate above heroes' heads - that one always shows if the mod is running."),
         PropertyOrder(1), UsedImplicitly]
        public bool ShowMissionOverlay { get; set; } = true;

        [DisplayName("2. Bar Style: New vs Old 5.2.4"),
         Description("CHECKED = new redesigned bar (class level dots, extra stats, colored adrenaline/power bars). UNCHECKED = the original simple BLT 5.2.4 look (in-game: just the hero's name; overlay: circular cooldown ring, plain-text Kills/Gold/XP, no level dots). Affects BOTH the in-game nameplate AND the browser overlay. Takes effect on the next mission load."),
         PropertyOrder(2), UsedImplicitly]
        public bool UseNewHeroBarLayout { get; set; } = true;

        [DisplayName("3. In-Game Side Bars (New Style only)"),
         Description("Only matters when setting 2 (New Style) is checked, and only affects the IN-GAME 3D nameplate above heroes' heads (not the browser overlay, which always shows its own side bars in New Style). Shows/hides the colored adrenaline (left) and power (right) bars next to the name. Takes effect on the next mission load."),
         PropertyOrder(3), UsedImplicitly]
        public bool ShowSideBars { get; set; } = true;
    }

    internal static class EquipCultureRestrictionPatch
    {
        internal static System.Reflection.MethodBase TargetMethod()
        {
            var type = typeof(BLTAdoptAHeroCampaignBehavior).Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == "EquipHero");
            return type?.GetMethod("FindRandomTieredEquipment",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        }

        // Prefix na EquipHero.FindRandomTieredEquipment — wymusza kulture bohatera przez parametry.
        // (EquipHero jest internal, wiec nie siegamy do jego helperow — tylko podmieniamy parametry,
        //  reszte robi oryginalna metoda: filtr item.Culture==kultura + wybor tieru w tej kulturze.)
        internal static void Prefix(Hero hero, ref CultureObject cultureFilter, ref bool cultureFilterSpecified)
        {
            try
            {
                var cfg = EquipCultureGlobalConfig.Get();
                if (cfg == null || !cfg.Enabled) return;             // standard BLT
                if (hero?.Culture == null) return;                   // brak kultury → standard
                cultureFilter = hero.Culture;                        // wymus kulture bohatera
                cultureFilterSpecified = true;                       // ... i nigdy nie odpuszczaj (brak obcej kultury)
            }
            catch (Exception e) { Log.Exception("[EquipCulture] Prefix", e); }
        }
    }

    // !discard w oryginalnym DLL nie ma ani ostrzezenia przed skasowaniem zalozonego przedmiotu, ani
    // refundu - fork dodaje obie rzeczy, ale refund liczy z BLTCustomItemsCampaignBehavior.PurchaseCost,
    // pola ktorego NIE MA w oryginalnym DLL (rzucilby MissingFieldException gdyby wywolane). Wlasna
    // reimplementacja: ten sam equipped-item guard (+ "force" do obejscia), ale refund to plaska,
    // konfigurowalna kwota zamiast prawdziwego 1/8 kosztu (ktorego nie da sie odczytac z zewnatrz).
    public class MBGADiscardRefundConfig
    {
        private const string ID = "MBGA - Discard Refund";
        internal static void Register() => ActionManager.RegisterGlobalConfigType(ID, typeof(MBGADiscardRefundConfig));
        internal static MBGADiscardRefundConfig Get() => ActionManager.GetGlobalConfig<MBGADiscardRefundConfig>(ID);

        [DisplayName("Enabled"), Description("Guard against accidentally discarding an equipped custom item (require \"!discard <index> force\" to confirm) and refund a flat amount of gold on discard. Standalone re-implementation for an UNMODIFIED BLTAdoptAHero.dll - off by default, leave disabled if running the MesmerTurn fork, which already has this (and a more accurate, cost-based refund)."), UsedImplicitly]
        public bool Enabled { get; set; } = false;

        [DisplayName("Flat Refund"), Description("Flat gold refunded per discarded custom item. The fork refunds 1/8th of the tracked purchase cost, but that cost isn't readable from outside - this is a flat approximation instead."), UsedImplicitly]
        public int FlatRefund { get; set; } = 1000;
    }

    [HarmonyPatch]
    internal static class DiscardItemGuardPatch
    {
        internal static System.Reflection.MethodBase TargetMethod()
            => typeof(DiscardItem).GetMethod("ExecuteInternal",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        [HarmonyPrefix]
        private static bool Prefix(Hero adoptedHero, ReplyContext context, object config,
            Action<string> onSuccess, Action<string> onFailure)
        {
            try
            {
                var cfg = MBGADiscardRefundConfig.Get();
                if (cfg == null || !cfg.Enabled) return true; // standard BLT behavior

                if (string.IsNullOrWhiteSpace(context.Args))
                {
                    onFailure(context.ArgsErrorMessage("(custom item index)"));
                    return false;
                }

                var argParts = context.Args.Trim().Split(' ');
                bool force = argParts.Length > 1 && argParts[1].Equals("force", StringComparison.OrdinalIgnoreCase);

                (var element, string error) = BLTAdoptAHeroCampaignBehavior.Current.FindCustomItemByIndex(adoptedHero, argParts[0]);
                if (element.IsEqualTo(EquipmentElement.Invalid))
                {
                    onFailure(error ?? "(unknown error)");
                    return false;
                }

                bool isEquipped = adoptedHero.BattleEquipment.YieldFilledEquipmentSlots().Any(e => e.element.IsEqualTo(element))
                                || adoptedHero.CivilianEquipment.YieldFilledEquipmentSlots().Any(e => e.element.IsEqualTo(element));
                if (isEquipped && !force)
                {
                    onFailure($"'{RewardHelpers.GetItemNameAndModifiers(element)}' is currently equipped - equip something else in that slot first, or use \"!discard {argParts[0]} force\" to discard it anyway.");
                    return false;
                }

                BLTAdoptAHeroCampaignBehavior.Current.DiscardCustomItem(adoptedHero, element);

                if (cfg.FlatRefund > 0)
                {
                    BLTAdoptAHeroCampaignBehavior.Current.ChangeHeroGold(adoptedHero, cfg.FlatRefund);
                    onSuccess($"'{RewardHelpers.GetItemNameAndModifiers(element)}' was discarded (+{cfg.FlatRefund} gold)");
                }
                else
                {
                    onSuccess($"'{RewardHelpers.GetItemNameAndModifiers(element)}' was discarded");
                }

                return false; // skip original
            }
            catch (Exception ex)
            {
                Log.Exception("DiscardItemGuardPatch.Prefix failed", ex);
                return true; // fall back to original on error
            }
        }
    }

}


