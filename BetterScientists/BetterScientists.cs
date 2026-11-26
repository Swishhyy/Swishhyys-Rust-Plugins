using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("BetterScientists", "Swishhyy", "0.1.0")]
    [Description("Makes monument scientists tougher, hit harder, and drop rewards for the player who takes one down")]
    public class BetterScientists : RustPlugin
    {
        #region Configuration

        private Configuration config;

        private class RewardItem
        {
            [JsonProperty("Item shortname")]
            public string Shortname = "scrap";

            [JsonProperty("Amount")]
            public int Amount = 50;
        }

        private class Configuration
        {
            [JsonProperty("Scientist health multiplier")]
            public float HealthMultiplier = 2.5f;

            [JsonProperty("Damage scientists deal multiplier")]
            public float DamageMultiplier = 1.5f;

            [JsonProperty("Reward chance per kill (%)")]
            public float RewardChancePercent = 100f;

            [JsonProperty("Rewards given to the killer", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<RewardItem> Rewards = new List<RewardItem>
            {
                new RewardItem { Shortname = "scrap", Amount = 50 },
                new RewardItem { Shortname = "lowgradefuel", Amount = 25 }
            };

            [JsonProperty("Announce scientist kills in chat")]
            public bool AnnounceKills = true;
        }

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) throw new JsonException("config file is empty");
                config.HealthMultiplier = Mathf.Max(0.1f, config.HealthMultiplier);
                config.DamageMultiplier = Mathf.Max(0f, config.DamageMultiplier);
                config.RewardChancePercent = Mathf.Clamp(config.RewardChancePercent, 0f, 100f);
                config.Rewards = config.Rewards ?? new List<RewardItem>();
                SaveConfig();
            }
            catch (Exception ex)
            {
                PrintError($"Config is invalid, running on defaults until it's fixed (your file was left alone): {ex.Message}");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        #endregion

        #region Helpers

        private static bool IsScientist(BaseEntity entity) =>
            entity != null && entity.ShortPrefabName != null &&
            entity.ShortPrefabName.ToLowerInvariant().Contains("scientist");

        #endregion

        #region Hooks

        // Scientists spawn with more health. Their max health is scaled, so they still match the server's own settings.
        private void OnEntitySpawned(NPCPlayer npc)
        {
            if (!IsScientist(npc) || Mathf.Approximately(config.HealthMultiplier, 1f))
                return;

            float max = npc.MaxHealth() * config.HealthMultiplier;
            npc.InitializeHealth(max, max);
        }

        // Scientist attacks hit harder. Only scientist-initiated damage is scaled.
        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (info == null || Mathf.Approximately(config.DamageMultiplier, 1f))
                return null;

            if (IsScientist(info.Initiator))
                info.damageTypes.ScaleAll(config.DamageMultiplier);

            return null;
        }

        // The player who takes a scientist down gets the rewards.
        private void OnEntityDeath(NPCPlayer npc, HitInfo info)
        {
            if (!IsScientist(npc) || info == null)
                return;

            BasePlayer killer = info.InitiatorPlayer;
            if (killer == null || killer.IsNpc || killer.IsDead())
                return;

            if (UnityEngine.Random.Range(0f, 100f) >= config.RewardChancePercent)
                return;

            foreach (RewardItem reward in config.Rewards)
            {
                if (reward == null || string.IsNullOrEmpty(reward.Shortname) || reward.Amount <= 0)
                    continue;

                Item item = ItemManager.CreateByName(reward.Shortname, reward.Amount);
                if (item == null)
                {
                    PrintWarning($"Reward item '{reward.Shortname}' doesn't exist on this server.");
                    continue;
                }
                killer.GiveItem(item);
            }

            if (config.AnnounceKills)
                rust.BroadcastChat($"{killer.displayName} took down a scientist.");
        }

        #endregion
    }
}
