// ══════════════════════════════════════════════════════════════════════════════
// Peasant Rebellion — Perks
// Standalone module: small permanent combat/utility bonuses, unlocked with points
// earned from kills/battles/hero-level, spent via !perk. Deliberately independent
// of MakeBltGreatAgain.dll — only requires BannerlordTwitch.dll and BLTAdoptAHero.dll
// (both already required for Peasant Rebellion regardless), so it works whether or
// not the MakeBltGreatAgain module is enabled.
//
// Extracted from MakeBltGreatAgain.cs (RebellionOfThePeasants, commits 9e20a1c..
// 6d6d472) at the user's request to be a fully separate module. The 4 branches that
// were designed to synergize with MakeBltGreatAgain-only mechanics (Lance Mastery,
// Wanderer Bond, Aura Potency, Adrenaline Surge) are dropped here on purpose — they
// have nothing to hook into without MakeBltGreatAgain's Lancer Charge/Wanderer/
// Composed Aura/Adrenaline systems. The "Min Tier" gate (tied to MakeBltGreatAgain's
// Power Progression tiers) is dropped for the same reason. Everything else (12
// branches, all engine/BLTAdoptAHero-native hooks) is unchanged.
// ══════════════════════════════════════════════════════════════════════════════

using BLTAdoptAHero;
using BLTAdoptAHero.Achievements;
using BannerlordTwitch;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Rewards;
using BannerlordTwitch.Util;
using HarmonyLib;
using JetBrains.Annotations;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;

namespace PeasantRebellionPerks
{
    // ════════════════════════════════════════════════════════════════════
    //  PERK SYSTEM — data model
    // ════════════════════════════════════════════════════════════════════

    public class PerkDef
    {
        [DisplayName("Key"), Description("Unique internal identifier, e.g. 'hp_1'. Do not change once players have ranks in it."), UsedImplicitly]
        public string Key { get; set; } = "";

        [DisplayName("Display Name"), Description("Shown in !perk and on the Neocities page."), UsedImplicitly]
        public string DisplayName { get; set; } = "";

        [DisplayName("Branch"), Description("Groups this perk with others for rendering and rank lookup, e.g. 'Speed'."), UsedImplicitly]
        public string Branch { get; set; } = "";

        [DisplayName("Max Rank"), Description("How many times this perk can be purchased. 3-4 typical."), UsedImplicitly]
        public int MaxRank { get; set; } = 3;

        [DisplayName("Bonus Per Rank"), Description("Effect size added per rank, e.g. 0.02 = 2%. Deliberately small by design."), UsedImplicitly]
        public float BonusPerRank { get; set; } = 0.02f;

        [DisplayName("Required Perk Key"), Description("Empty = this is a branch root (buyable with 0 prerequisites). Otherwise the Key of the perk that must be ranked first."), UsedImplicitly]
        public string RequiredPerkKey { get; set; } = "";

        [DisplayName("Required Rank"), Description("Minimum rank the required perk must have before this one can be bought."), UsedImplicitly]
        public int RequiredRank { get; set; } = 1;

        [DisplayName("Min Level"), Description("Hero level required. 0 = no gate."), UsedImplicitly]
        public int MinLevel { get; set; } = 0;

        [DisplayName("Required Achievement Key"), Description("Capstones only. Empty = not achievement-gated. When set, this perk cannot be bought with points at all until the named achievement has fired for the hero."), UsedImplicitly]
        public string RequiredAchievementKey { get; set; } = "";
    }

    [DisplayName("Peasant Rebellion - Perks")]
    public class PerkGlobalConfig : IDocumentable
    {
        private const string ID = "Peasant Rebellion - Perks";
        internal static void Register() => ActionManager.RegisterGlobalConfigType(ID, typeof(PerkGlobalConfig));
        internal static PerkGlobalConfig Get() => ActionManager.GetGlobalConfig<PerkGlobalConfig>(ID);

        [DisplayName("Enabled"), Category("1 - General"), PropertyOrder(1),
         Description("Enables/disables the entire perk system. When disabled, !perk reports it's off and no bonuses apply."), UsedImplicitly]
        public bool Enabled { get; set; } = false;

        [DisplayName("Kills Per Point"), Category("1 - General"), PropertyOrder(2),
         Description("How many kills (in this class) grant one perk point. 0 = kills don't grant points."), UsedImplicitly]
        public int KillsPerPoint { get; set; } = 10;

        [DisplayName("Battles Per Point"), Category("1 - General"), PropertyOrder(3),
         Description("How many battles (in this class) grant one perk point. 0 = battles don't grant points."), UsedImplicitly]
        public int BattlesPerPoint { get; set; } = 5;

        [DisplayName("Points Per Hero Level"), Category("1 - General"), PropertyOrder(4),
         Description("How many perk points are granted per hero level gained. 0 = level doesn't grant points."), UsedImplicitly]
        public int PointsPerLevel { get; set; } = 1;

        [DisplayName("Perks"), Category("2 - Catalog"), PropertyOrder(10),
         Description("The full perk catalog. Add/remove/edit freely — Key must stay unique."),
         ExpandableObject, UsedImplicitly]
        public List<PerkDef> Perks { get; set; } = PerkCatalog.Default();

        // Picked up automatically by Settings.GenerateDocumentation's existing
        // "GlobalConfigs.Select(c => c.Config).OfType<IDocumentable>()" loop.
        public void GenerateDocumentation(IDocumentationGenerator generator)
        {
            var branches = Perks
                .GroupBy(p => p.Branch)
                .Select(g => (
                    BranchName: g.Key,
                    Perks: g.OrderBy(p => p.RequiredRank).Select(p => (
                        Key: p.Key,
                        DisplayName: p.DisplayName,
                        BonusPerRank: p.BonusPerRank,
                        RequiredPerkKey: p.RequiredPerkKey,
                        RequirementText: BuildRequirementText(p)
                    ))
                ));
            generator.PerksSection(branches, _ => 1);
        }

        private static string BuildRequirementText(PerkDef p)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(p.RequiredPerkKey)) parts.Add($"{p.RequiredPerkKey} rank {p.RequiredRank}+");
            if (p.MinLevel > 0) parts.Add($"level {p.MinLevel}+");
            if (!string.IsNullOrEmpty(p.RequiredAchievementKey)) parts.Add($"achievement '{p.RequiredAchievementKey}'");
            return string.Join(", ", parts);
        }
    }

    public static class PerkCatalog
    {
        public static List<PerkDef> Default()
        {
            var list = new List<PerkDef>();
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

            Branch("HP",             "hp",       0.02f);
            Branch("Damage",         "dmg",      0.015f);
            Branch("Evade",          "evade",    0.01f);
            Branch("Regen",          "regen",    0.02f);
            Branch("Berserk",        "berserk",  0.02f);
            Branch("Loot",           "loot",     0.03f);
            Branch("Speed",          "speed",    0.01f);
            Branch("Accuracy",       "acc",      0.01f);
            Branch("Mounted Armor",  "mtdarmor", 0.02f);
            Branch("Mounted Charge", "mtdchg",   0.02f);
            Branch("Weapon Mastery: Two-Handed", "wm2h", 0.015f);
            Branch("Weapon Mastery: Polearm",    "wmpole", 0.015f);
            Branch("Weapon Mastery: Thrown",     "wmthrow", 0.015f);
            Branch("Weapon Swap Speed", "swapspd", 0.015f);

            return list;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  PERK SYSTEM — per-hero state
    // ════════════════════════════════════════════════════════════════════

    public class PerkHeroRecord
    {
        public int PointsAvailable { get; set; } = 0;
        public int PointsEarnedTotal { get; set; } = 0;
        public Dictionary<string, int> Ranks { get; set; } = new Dictionary<string, int>();
        public int LastKillsCounted { get; set; } = 0;
        public int LastBattlesCounted { get; set; } = 0;
        public int LastLevelCounted { get; set; } = 0;
    }

    public class PerkBehavior : CampaignBehaviorBase
    {
        public static PerkBehavior Current => Campaign.Current?.GetCampaignBehavior<PerkBehavior>();

        private Dictionary<string, PerkHeroRecord> records = new Dictionary<string, PerkHeroRecord>();

        public override void RegisterEvents() { }

        public override void SyncData(IDataStore dataStore)
        {
            string json = dataStore.IsSaving
                ? Newtonsoft.Json.JsonConvert.SerializeObject(records ?? new Dictionary<string, PerkHeroRecord>())
                : null;
            dataStore.SyncData("PeasantRebellionPerkJson", ref json);
            if (!dataStore.IsSaving)
            {
                records = string.IsNullOrEmpty(json)
                    ? new Dictionary<string, PerkHeroRecord>()
                    : (Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, PerkHeroRecord>>(json)
                       ?? new Dictionary<string, PerkHeroRecord>());
            }
            if (records == null) records = new Dictionary<string, PerkHeroRecord>();
        }

        public PerkHeroRecord GetOrCreate(Hero hero)
        {
            if (hero == null) return new PerkHeroRecord();
            if (!records.TryGetValue(hero.StringId, out var rec))
            {
                rec = new PerkHeroRecord();
                records[hero.StringId] = rec;
            }
            return rec;
        }

        public int GetRank(Hero hero, string perkKey)
        {
            if (hero == null || string.IsNullOrEmpty(perkKey)) return 0;
            var rec = GetOrCreate(hero);
            return rec.Ranks.TryGetValue(perkKey, out var r) ? r : 0;
        }
    }

    public class PeasantRebellionPerksModule : MBSubModuleBase
    {
        private static Harmony harmony;

        public PeasantRebellionPerksModule()
        {
            ActionManager.RegisterAll(typeof(PeasantRebellionPerksModule).Assembly);
            try { PerkGlobalConfig.Register(); } catch (Exception ex) { Log.Exception("[Perk] Register failed", ex); }
        }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            if (harmony != null) return;
            try
            {
                harmony = new Harmony("mod.bannerlord.peasantrebellionperks");
                harmony.Patch(
                    AccessTools.Method(typeof(MissionCombatMechanicsHelper), "ComputeBlowDamage"),
                    prefix: new HarmonyMethod(typeof(PerkDamageEvadePatch).GetMethod(nameof(PerkDamageEvadePatch.Prefix), BindingFlags.Static | BindingFlags.Public)),
                    postfix: new HarmonyMethod(typeof(PerkDamageEvadePatch).GetMethod(nameof(PerkDamageEvadePatch.Postfix), BindingFlags.Static | BindingFlags.Public)));
                harmony.Patch(
                    AccessTools.PropertyGetter(typeof(Agent), "HealthLimit"),
                    postfix: new HarmonyMethod(typeof(PerkHealthLimitPatch).GetMethod(nameof(PerkHealthLimitPatch.Postfix), BindingFlags.Static | BindingFlags.Public)));
                Log.Info("[Perk] Combat patches applied: Damage/Evade/Berserk (ComputeBlowDamage), HP (Agent.HealthLimit).");
            }
            catch (Exception ex) { Log.Exception("[Perk] Combat patch setup failed", ex); }
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);
            if (gameStarterObject is CampaignGameStarter campaignStarter)
            {
                campaignStarter.AddBehavior(new PerkBehavior());
            }
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            base.OnMissionBehaviorInitialize(mission);
            mission.AddMissionBehavior(new PerkRegenMissionBehavior());
            mission.AddMissionBehavior(new PerkLootMissionBehavior());
            mission.AddMissionBehavior(new PerkStatMissionBehavior());
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  PERK SYSTEM — service (shared by !perk and the Neocities page)
    // ════════════════════════════════════════════════════════════════════

    public static class PerkService
    {
        public static void RefreshPoints(Hero hero)
        {
            var cfg = PerkGlobalConfig.Get();
            if (cfg == null || !cfg.Enabled || hero == null) return;
            var perkBeh = PerkBehavior.Current;
            var aahBeh = BLTAdoptAHeroCampaignBehavior.Current;
            if (perkBeh == null || aahBeh == null) return;

            var rec = perkBeh.GetOrCreate(hero);
            int kills = aahBeh.GetAchievementClassStat(hero, AchievementStatsData.Statistic.TotalKills);
            int battles = aahBeh.GetAchievementClassStat(hero, AchievementStatsData.Statistic.Battles);
            int level = hero.Level;

            int newPoints = 0;
            if (cfg.KillsPerPoint > 0)
            {
                int deltaKills = Math.Max(0, kills - rec.LastKillsCounted);
                newPoints += deltaKills / cfg.KillsPerPoint;
                rec.LastKillsCounted += (deltaKills / cfg.KillsPerPoint) * cfg.KillsPerPoint;
            }
            if (cfg.BattlesPerPoint > 0)
            {
                int deltaBattles = Math.Max(0, battles - rec.LastBattlesCounted);
                newPoints += deltaBattles / cfg.BattlesPerPoint;
                rec.LastBattlesCounted += (deltaBattles / cfg.BattlesPerPoint) * cfg.BattlesPerPoint;
            }
            if (cfg.PointsPerLevel > 0 && level > rec.LastLevelCounted)
            {
                newPoints += (level - rec.LastLevelCounted) * cfg.PointsPerLevel;
                rec.LastLevelCounted = level;
            }

            if (newPoints > 0)
            {
                rec.PointsAvailable += newPoints;
                rec.PointsEarnedTotal += newPoints;
            }
        }

        public static float GetBonus(Hero hero, string branch)
        {
            var cfg = PerkGlobalConfig.Get();
            var perkBeh = PerkBehavior.Current;
            if (cfg == null || !cfg.Enabled || perkBeh == null || hero == null) return 0f;
            float total = 0f;
            foreach (var p in cfg.Perks.Where(p => p.Branch == branch))
            {
                int rank = perkBeh.GetRank(hero, p.Key);
                if (rank > 0) total += p.BonusPerRank * rank;
            }
            return total;
        }

        public static bool CanBuy(Hero hero, PerkDef perk, out string reason)
        {
            reason = "";
            var cfg = PerkGlobalConfig.Get();
            var perkBeh = PerkBehavior.Current;
            if (cfg == null || !cfg.Enabled) { reason = "Perk system is disabled."; return false; }
            if (perk == null) { reason = "Unknown perk."; return false; }
            if (perkBeh == null) { reason = "Perk state unavailable."; return false; }

            int currentRank = perkBeh.GetRank(hero, perk.Key);
            if (currentRank >= perk.MaxRank) { reason = $"{perk.DisplayName} is already at max rank."; return false; }

            var rec = perkBeh.GetOrCreate(hero);
            if (rec.PointsAvailable < 1) { reason = "Not enough perk points."; return false; }

            if (!string.IsNullOrEmpty(perk.RequiredPerkKey) &&
                perkBeh.GetRank(hero, perk.RequiredPerkKey) < perk.RequiredRank)
            {
                var reqDef = cfg.Perks.FirstOrDefault(p => p.Key == perk.RequiredPerkKey);
                reason = $"Requires {(reqDef?.DisplayName ?? perk.RequiredPerkKey)} rank {perk.RequiredRank}+.";
                return false;
            }
            if (perk.MinLevel > 0 && hero.Level < perk.MinLevel)
            {
                reason = $"Requires hero level {perk.MinLevel}+.";
                return false;
            }
            if (!string.IsNullOrEmpty(perk.RequiredAchievementKey) &&
                !HasAchievement(hero, perk.RequiredAchievementKey))
            {
                reason = $"Requires the '{perk.RequiredAchievementKey}' achievement.";
                return false;
            }
            return true;
        }

        public static bool Buy(Hero hero, PerkDef perk, out string message)
        {
            if (!CanBuy(hero, perk, out message)) return false;
            var perkBeh = PerkBehavior.Current;
            var rec = perkBeh.GetOrCreate(hero);
            int newRank = perkBeh.GetRank(hero, perk.Key) + 1;
            rec.Ranks[perk.Key] = newRank;
            rec.PointsAvailable -= 1;
            message = $"{perk.DisplayName} is now rank {newRank}/{perk.MaxRank}.";
            return true;
        }

        // Achievements are host-configured (BLTAdoptAHero.Achievements.AchievementDef), evaluated
        // live via IsAchieved(hero), no persistent unlocked flag or stable key. The container
        // (GlobalCommonConfig) is internal to BLTAdoptAHero.dll, bridged via reflection (internal
        // is a compile-time restriction, not a runtime one). Matches by display Name.
        private static bool HasAchievement(Hero hero, string achievementKey)
        {
            if (hero == null || string.IsNullOrEmpty(achievementKey)) return false;
            try
            {
                var cfgType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => { try { return a.GetType("BLTAdoptAHero.GlobalConfigs.GlobalCommonConfig"); } catch { return null; } })
                    .FirstOrDefault(t => t != null);
                if (cfgType == null) return false;

                var getMethod = cfgType.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                var cfgInstance = getMethod?.Invoke(null, null);
                if (cfgInstance == null) return false;

                var achievementsProp = cfgType.GetProperty("Achievements", BindingFlags.Public | BindingFlags.Instance);
                var achievements = achievementsProp?.GetValue(cfgInstance) as System.Collections.IEnumerable;
                if (achievements == null) return false;

                foreach (var achievement in achievements)
                {
                    var nameProp = achievement.GetType().GetProperty("Name");
                    string name = nameProp?.GetValue(achievement)?.ToString() ?? "";
                    if (name.IndexOf(achievementKey, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var isAchievedMethod = achievement.GetType().GetMethod("IsAchieved", BindingFlags.Public | BindingFlags.Instance);
                    if (isAchievedMethod?.Invoke(achievement, new object[] { hero }) is bool result)
                        return result;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Exception("PerkService.HasAchievement (reflection bridge)", ex);
                return false;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  PERK SYSTEM — direct-multiplier branches (HP, Damage, Evade, Regen, Berserk, Loot)
    //  Same real engine hooks as the original MakeBltGreatAgain implementation:
    //  MissionCombatMechanicsHelper.ComputeBlowDamage, Agent.HealthLimit,
    //  agent.Health tick-heal, OnAgentRemoved + ChangeHeroGold.
    // ════════════════════════════════════════════════════════════════════

    [HarmonyPatch(typeof(MissionCombatMechanicsHelper), "ComputeBlowDamage")]
    internal static class PerkDamageEvadePatch
    {
        public static void Prefix(ref AttackInformation attackInformation, ref bool cancelDamage)
        {
            try
            {
                if (cancelDamage) return;
                var victimHero = attackInformation.VictimAgent?.GetAdoptedHero();
                if (victimHero == null) return;
                float evadeBonus = PerkService.GetBonus(victimHero, "Evade");
                if (evadeBonus <= 0f) return;
                if (MBRandom.RandomFloat < evadeBonus) cancelDamage = true;
            }
            catch (Exception ex) { Log.Exception("PerkDamageEvadePatch.Prefix", ex); }
        }

        public static void Postfix(ref AttackInformation attackInformation, bool cancelDamage, ref int inflictedDamage)
        {
            try
            {
                if (cancelDamage || inflictedDamage <= 0) return;
                var atkAgent = attackInformation.AttackerAgent;
                var attackerHero = atkAgent?.GetAdoptedHero();
                if (attackerHero == null) return;

                float dmgBonus = PerkService.GetBonus(attackerHero, "Damage");

                float berserkBonus = 0f;
                if (atkAgent.HealthLimit > 0f)
                {
                    float hpFrac = atkAgent.Health / atkAgent.HealthLimit;
                    if (hpFrac < 0.3f) berserkBonus = PerkService.GetBonus(attackerHero, "Berserk");
                }

                float totalBonus = dmgBonus + berserkBonus;
                if (totalBonus > 0f) inflictedDamage = (int)(inflictedDamage * (1f + totalBonus));
            }
            catch (Exception ex) { Log.Exception("PerkDamageEvadePatch.Postfix", ex); }
        }
    }

    [HarmonyPatch(typeof(Agent), "HealthLimit", MethodType.Getter)]
    internal static class PerkHealthLimitPatch
    {
        public static void Postfix(Agent __instance, ref float __result)
        {
            try
            {
                var hero = __instance?.GetAdoptedHero();
                if (hero == null) return;
                float bonus = PerkService.GetBonus(hero, "HP");
                if (bonus > 0f) __result *= (1f + bonus);
            }
            catch (Exception ex) { Log.Exception("PerkHealthLimitPatch.Postfix", ex); }
        }
    }

    public class PerkRegenMissionBehavior : MissionBehavior
    {
        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;
        private const float TickIntervalSeconds = 5f;
        private float accumulator = 0f;

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            accumulator += dt;
            if (accumulator < TickIntervalSeconds) return;
            accumulator = 0f;
            if (Mission.Current == null) return;

            foreach (var agent in Mission.Current.Agents)
            {
                if (agent == null || !agent.IsActive() || agent.Health <= 0f) continue;
                var hero = agent.GetAdoptedHero();
                if (hero == null) continue;
                float bonus = PerkService.GetBonus(hero, "Regen");
                if (bonus <= 0f || agent.HealthLimit <= 0f) continue;
                agent.Health = Math.Min(agent.HealthLimit, agent.Health + bonus * agent.HealthLimit);
            }
        }
    }

    public class PerkLootMissionBehavior : MissionBehavior
    {
        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            try
            {
                if (agentState != AgentState.Killed && agentState != AgentState.Unconscious) return;
                if (affectorAgent == null || affectorAgent == affectedAgent) return;
                var hero = affectorAgent.GetAdoptedHero();
                if (hero == null) return;
                float bonus = PerkService.GetBonus(hero, "Loot");
                if (bonus <= 0f) return;
                int bonusGold = (int)Math.Round(bonus * 100f); // e.g. 0.03 (rank 1) -> 3 gold/kill
                if (bonusGold > 0) BLTAdoptAHeroCampaignBehavior.Current?.ChangeHeroGold(hero, bonusGold, true);
            }
            catch (Exception ex) { Log.Exception("PerkLootMissionBehavior.OnAgentRemoved", ex); }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  PERK SYSTEM — continuous-stat branches (Speed, Accuracy, Mounted Armor,
    //  Mounted Charge, Weapon Mastery x3, Weapon Swap Speed) via
    //  BLTAgentModifierBehavior/AgentModifierConfig/PropertyModifierDef (native BannerlordTwitch).
    // ════════════════════════════════════════════════════════════════════

    public class PerkStatMissionBehavior : MissionBehavior
    {
        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        private readonly Dictionary<Agent, AgentModifierConfig> applied = new Dictionary<Agent, AgentModifierConfig>();

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            if (Mission.Current == null) return;
            foreach (var agent in Mission.Current.Agents)
            {
                if (agent == null || !agent.IsActive() || applied.ContainsKey(agent)) continue;
                var hero = agent.GetAdoptedHero();
                if (hero == null) continue;

                var config = BuildConfig(hero);
                if (config.Properties.Count == 0) continue;
                applied[agent] = config;
                BLTAgentModifierBehavior.Current?.Add(agent, config);
            }
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            if (affectedAgent != null && applied.TryGetValue(affectedAgent, out var config))
            {
                BLTAgentModifierBehavior.Current?.Remove(affectedAgent, config);
                applied.Remove(affectedAgent);
            }
        }

        private static AgentModifierConfig BuildConfig(Hero hero)
        {
            var config = new AgentModifierConfig();
            void AddPct(DrivenProperty prop, float bonus)
            {
                if (bonus > 0f) config.Properties.Add(new PropertyModifierDef { Name = prop, ModifierPercent = 100f + bonus * 100f });
            }

            AddPct(DrivenProperty.SwingSpeedMultiplier, PerkService.GetBonus(hero, "Speed"));
            AddPct(DrivenProperty.ReloadSpeed, PerkService.GetBonus(hero, "Speed"));

            float accBonus = PerkService.GetBonus(hero, "Accuracy");
            if (accBonus > 0f) config.Properties.Add(new PropertyModifierDef { Name = DrivenProperty.WeaponInaccuracy, ModifierPercent = 100f - accBonus * 100f });

            AddPct(DrivenProperty.ArmorHead, PerkService.GetBonus(hero, "Mounted Armor"));
            AddPct(DrivenProperty.ArmorTorso, PerkService.GetBonus(hero, "Mounted Armor"));
            AddPct(DrivenProperty.ArmorLegs, PerkService.GetBonus(hero, "Mounted Armor"));
            AddPct(DrivenProperty.ArmorArms, PerkService.GetBonus(hero, "Mounted Armor"));

            AddPct(DrivenProperty.MountChargeDamage, PerkService.GetBonus(hero, "Mounted Charge"));

            float wm2h = PerkService.GetBonus(hero, "Weapon Mastery: Two-Handed");
            float wmPole = PerkService.GetBonus(hero, "Weapon Mastery: Polearm");
            float wmThrow = PerkService.GetBonus(hero, "Weapon Mastery: Thrown");
            AddPct(DrivenProperty.MeleeWeaponDamageMultiplierBonus, Math.Max(wm2h, wmPole));
            AddPct(DrivenProperty.ThrowingWeaponDamageMultiplierBonus, wmThrow);

            AddPct(DrivenProperty.ThrustOrRangedReadySpeedMultiplier, PerkService.GetBonus(hero, "Weapon Swap Speed"));

            return config;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  PERK SYSTEM — !perk chat command
    // ════════════════════════════════════════════════════════════════════

    public class PerkCommand : ICommandHandler
    {
        public class Settings { }
        public Type HandlerConfigType => typeof(Settings);

        public void Execute(ReplyContext context, object config)
        {
            var cfg = PerkGlobalConfig.Get();
            if (cfg == null || !cfg.Enabled)
            {
                ActionManager.SendReply(context, "Perks are currently disabled.");
                return;
            }

            var hero = BLTAdoptAHeroCampaignBehavior.Current?.GetAdoptedHero(context.UserName);
            if (hero == null)
            {
                ActionManager.SendReply(context, "Adopt a hero first with !adopt.");
                return;
            }

            PerkService.RefreshPoints(hero);
            var perkBeh = PerkBehavior.Current;
            var rec = perkBeh.GetOrCreate(hero);
            var args = (context.Args ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (args.Length == 0)
            {
                var branchSummaries = cfg.Perks.GroupBy(p => p.Branch)
                    .Select(g => $"{g.Key}: {g.Sum(p => perkBeh.GetRank(hero, p.Key))}/{g.Sum(p => p.MaxRank)}");
                ActionManager.SendReply(context,
                    $"Points available: {rec.PointsAvailable}. Ranks — {string.Join(" | ", branchSummaries)}. Use !perk <branch> for details or !perk buy <branch> to spend a point.");
                return;
            }

            if (args[0].Equals("buy", StringComparison.OrdinalIgnoreCase) && args.Length >= 2)
            {
                string branchQuery = string.Join(" ", args.Skip(1));
                var nextPerk = cfg.Perks
                    .Where(p => p.Branch.IndexOf(branchQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(p => p.RequiredRank)
                    .FirstOrDefault(p => perkBeh.GetRank(hero, p.Key) < p.MaxRank);
                if (nextPerk == null)
                {
                    ActionManager.SendReply(context, $"No purchasable perk found in branch '{branchQuery}'.");
                    return;
                }
                if (PerkService.Buy(hero, nextPerk, out var msg))
                    ActionManager.SendReply(context, msg);
                else
                    ActionManager.SendReply(context, $"Can't buy {nextPerk.DisplayName}: {msg}");
                return;
            }

            string query = string.Join(" ", args);
            var branchPerks = cfg.Perks
                .Where(p => p.Branch.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(p => p.RequiredRank).ToList();
            if (branchPerks.Count == 0)
            {
                ActionManager.SendReply(context, $"Unknown branch '{query}'. Use !perk to see all branches.");
                return;
            }
            var lines = branchPerks.Select(p =>
            {
                int rank = perkBeh.GetRank(hero, p.Key);
                string status = rank >= p.MaxRank ? "MAXED" : CanBuySummary(hero, p);
                return $"{p.DisplayName}: rank {rank}/{p.MaxRank}, +{p.BonusPerRank * 100:0.#}%/rank ({status})";
            });
            ActionManager.SendReply(context, string.Join(" | ", lines));
        }

        private static string CanBuySummary(Hero hero, PerkDef p)
        {
            return PerkService.CanBuy(hero, p, out var reason) ? "buyable" : reason;
        }
    }
}
