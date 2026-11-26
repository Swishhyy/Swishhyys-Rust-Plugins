using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Anti Offline Raid", "Swishhyy", "1.0.0")]
    [Description("Prevents player and NPC damage to bases while every owner is offline")]
    public class AntiOfflineRaid : RustPlugin
    {
        #region Fields

        [PluginReference] private Plugin Clans;

        private const string PermAdmin = "antiofflineraid.admin";
        private const string PermBypass = "antiofflineraid.bypass";     // attacker ignores protection
        private const string PermProtect = "antiofflineraid.protect";   // only used when RequirePermission = true

        private Configuration config;
        private StoredData storedData;

        private readonly HashSet<ulong> onlinePlayers = new HashSet<ulong>();
        private readonly Dictionary<ulong, float> lastNotified = new Dictionary<ulong, float>();

        // key: TC net id -> time of last unblocked raid damage
        private readonly Dictionary<string, double> lastRaidDamage = new Dictionary<string, double>();

        // short-lived cache of protection results so heavy explosive spam isn't recomputed per hit
        private readonly Dictionary<string, CacheEntry> protectionCache = new Dictionary<string, CacheEntry>();

        private struct CacheEntry
        {
            public bool Protected;
            public float Expires;
        }

        private enum AttackerType { Player, Npc, Other }

        #endregion

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Seconds after the last owner logs off before protection starts")]
            public float ProtectionDelaySeconds = 30f;

            [JsonProperty("Block damage from players")]
            public bool BlockPlayers = true;

            [JsonProperty("Block damage from NPCs (scientists, animals, heli, bradley)")]
            public bool BlockNpcs = true;

            [JsonProperty("Block damage with no attacker (fire, unowned explosives, etc.)")]
            public bool BlockUnknownSources = true;

            [JsonProperty("Allow decay damage while protected")]
            public bool AllowDecay = true;

            [JsonProperty("Count team members as owners")]
            public bool IncludeTeam = true;

            [JsonProperty("Count clan members as owners (requires Clans plugin)")]
            public bool IncludeClan = true;

            [JsonProperty("Minutes after raid damage before protection can activate (stops logging off mid-raid)")]
            public float RaidCooldownMinutes = 15f;

            [JsonProperty("Only protect bases where an owner has the protect permission")]
            public bool RequirePermission = false;

            [JsonProperty("Notify attacker when damage is blocked")]
            public bool NotifyAttacker = true;

            [JsonProperty("Seconds between attacker notifications")]
            public float NotifyCooldownSeconds = 10f;

            [JsonProperty("Protection cache seconds")]
            public float CacheSeconds = 2f;
        }

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) throw new Exception("null config");
                SaveConfig();
            }
            catch
            {
                PrintError("Configuration file is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(config);

        #endregion

        #region Data

        private class StoredData
        {
            // unix seconds of last disconnect per player
            public Dictionary<ulong, double> LastSeen = new Dictionary<ulong, double>();
        }

        private void LoadData()
        {
            try { storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name); }
            catch { storedData = null; }
            if (storedData == null) storedData = new StoredData();
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, storedData);

        private static double Now => DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Blocked"] = "This base is protected: its owners are offline.",
                ["NoPermission"] = "You don't have permission to use this command.",
                ["NothingFound"] = "No base entity found where you are looking.",
                ["StatusProtected"] = "Protected. All owners offline. Owners: {0}",
                ["StatusNotProtected"] = "Not protected. {0}",
                ["ReasonOnline"] = "Owner online: {0}",
                ["ReasonDelay"] = "Last owner went offline {0}s ago (needs {1}s)",
                ["ReasonRaid"] = "Raided recently; protection resumes in {0} min",
                ["ReasonNoOwners"] = "No owners found",
                ["ReasonPermission"] = "No owner has the protect permission",
                ["Help"] = "/aor check - check protection of the base you're looking at\n/aor clear - reset raid cooldowns"
            }, this);
        }

        private string Lang(string key, string userId, params object[] args)
        {
            var msg = lang.GetMessage(key, this, userId);
            return args.Length > 0 ? string.Format(msg, args) : msg;
        }

        #endregion

        #region Hooks

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermBypass, this);
            permission.RegisterPermission(PermProtect, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            foreach (var player in BasePlayer.activePlayerList)
                onlinePlayers.Add(player.userID);
        }

        private void Unload()
        {
            // record anyone still online so a reload doesn't treat them as long offline
            var now = Now;
            foreach (var id in onlinePlayers)
                storedData.LastSeen[id] = now;
            SaveData();
        }

        private void OnServerSave()
        {
            // keep online players' timestamps fresh so a crash doesn't make them look long offline
            var now = Now;
            foreach (var id in onlinePlayers)
                storedData.LastSeen[id] = now;
            SaveData();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            onlinePlayers.Add(player.userID);
            protectionCache.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            onlinePlayers.Remove(player.userID);
            storedData.LastSeen[player.userID] = Now;
            protectionCache.Clear();
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return null;
            if (!IsProtectableEntity(entity)) return null;

            if (config.AllowDecay && info.damageTypes.Has(DamageType.Decay))
                return null;

            var attackerType = GetAttackerType(info, out var attacker);
            if (attackerType == AttackerType.Player && !config.BlockPlayers) return null;
            if (attackerType == AttackerType.Npc && !config.BlockNpcs) return null;
            if (attackerType == AttackerType.Other && !config.BlockUnknownSources) return null;

            if (attacker != null && permission.UserHasPermission(attacker.UserIDString, PermBypass))
                return null;

            string key;
            var owners = GetOwners(entity, out key);
            if (owners == null || owners.Count == 0) return null;

            // owners can always damage their own stuff
            if (attacker != null && owners.Contains(attacker.userID)) return null;

            if (!IsProtectedCached(key, owners))
            {
                // only real raid damage starts the raid cooldown
                if (attackerType == AttackerType.Player && info.damageTypes.Total() > 0f)
                    lastRaidDamage[key] = Now;
                return null;
            }

            if (attacker != null && config.NotifyAttacker)
                Notify(attacker);

            info.damageTypes.ScaleAll(0f);
            info.DoHitEffects = false;
            return true;
        }

        #endregion

        #region Core logic

        // Anything inside tool cupboard range is protected, except players, NPCs and animals
        private static bool IsProtectableEntity(BaseCombatEntity entity)
        {
            if (entity is BasePlayer || entity is BaseNpc) return false;
            return entity is BuildingPrivlidge || entity.GetBuildingPrivilege() != null;
        }

        private AttackerType GetAttackerType(HitInfo info, out BasePlayer playerAttacker)
        {
            playerAttacker = null;
            var initiator = info.Initiator;

            // fire balls / flames carry their creator
            if (initiator is FireBall)
            {
                var creator = initiator.creatorEntity;
                if (creator != null) initiator = creator;
            }

            if (initiator == null)
                return AttackerType.Other;

            var player = initiator as BasePlayer;
            if (player != null)
            {
                if (player.IsNpc || !((ulong)player.userID).IsSteamId()) return AttackerType.Npc;
                playerAttacker = player;
                return AttackerType.Player;
            }

            if (initiator is BaseNpc || initiator is PatrolHelicopter || initiator is BradleyAPC ||
                initiator is CH47HelicopterAIController || initiator.GetType().Name.Contains("NPC"))
                return AttackerType.Npc;

            // turrets / traps owned by a player count as that player
            if (initiator.OwnerID.IsSteamId())
            {
                playerAttacker = BasePlayer.FindByID(initiator.OwnerID);
                return AttackerType.Player;
            }

            return AttackerType.Other;
        }

        private HashSet<ulong> GetOwners(BaseEntity entity, out string key)
        {
            key = null;
            var owners = new HashSet<ulong>();

            var priv = entity as BuildingPrivlidge ?? entity.GetBuildingPrivilege();
            if (priv == null) return null;
            key = "tc:" + priv.net.ID;
            AddAuthorized(priv, owners);

            if (owners.Count == 0) return owners;

            if (config.IncludeTeam || (config.IncludeClan && Clans != null))
            {
                foreach (var id in new List<ulong>(owners))
                {
                    if (config.IncludeTeam) AddTeam(id, owners);
                    if (config.IncludeClan && Clans != null) AddClan(id, owners);
                }
            }

            return owners;
        }

        // Works with both old (List<PlayerNameID>) and new (HashSet<ulong>) authorizedPlayers types
        private static void AddAuthorized(BuildingPrivlidge priv, HashSet<ulong> owners)
        {
            var list = priv.authorizedPlayers as IEnumerable;
            if (list == null) return;
            foreach (var entry in list)
            {
                if (entry is ulong)
                    owners.Add((ulong)entry);
                else if (entry is ProtoBuf.PlayerNameID)
                    owners.Add(((ProtoBuf.PlayerNameID)entry).userid);
            }
        }

        private static void AddTeam(ulong userId, HashSet<ulong> owners)
        {
            var team = RelationshipManager.ServerInstance?.FindPlayersTeam(userId);
            if (team == null) return;
            foreach (var member in team.members)
                owners.Add(member);
        }

        private void AddClan(ulong userId, HashSet<ulong> owners)
        {
            var members = Clans?.Call("GetClanMembers", userId.ToString()) as List<string>;
            if (members == null) return;
            foreach (var m in members)
            {
                ulong id;
                if (ulong.TryParse(m, out id)) owners.Add(id);
            }
        }

        private bool IsProtectedCached(string key, HashSet<ulong> owners)
        {
            CacheEntry entry;
            if (protectionCache.TryGetValue(key, out entry) && entry.Expires > Time.realtimeSinceStartup)
                return entry.Protected;

            string reason;
            var result = IsProtected(key, owners, null, out reason);
            protectionCache[key] = new CacheEntry { Protected = result, Expires = Time.realtimeSinceStartup + config.CacheSeconds };
            return result;
        }

        private bool IsProtected(string key, HashSet<ulong> owners, string langUser, out string reason)
        {
            reason = null;
            if (owners == null || owners.Count == 0)
            {
                reason = Lang("ReasonNoOwners", langUser);
                return false;
            }

            if (config.RequirePermission)
            {
                var any = false;
                foreach (var id in owners)
                    if (permission.UserHasPermission(id.ToString(), PermProtect)) { any = true; break; }
                if (!any)
                {
                    reason = Lang("ReasonPermission", langUser);
                    return false;
                }
            }

            var now = Now;
            double mostRecentLogoff = 0;
            foreach (var id in owners)
            {
                if (onlinePlayers.Contains(id))
                {
                    reason = Lang("ReasonOnline", langUser, GetName(id));
                    return false;
                }
                double seen;
                if (storedData.LastSeen.TryGetValue(id, out seen) && seen > mostRecentLogoff)
                    mostRecentLogoff = seen;
            }

            var offlineSeconds = now - mostRecentLogoff;
            if (offlineSeconds < config.ProtectionDelaySeconds)
            {
                reason = Lang("ReasonDelay", langUser, Math.Floor(offlineSeconds), config.ProtectionDelaySeconds);
                return false;
            }

            double raidTime;
            if (config.RaidCooldownMinutes > 0 && lastRaidDamage.TryGetValue(key, out raidTime))
            {
                var sinceRaid = (now - raidTime) / 60.0;
                if (sinceRaid < config.RaidCooldownMinutes)
                {
                    reason = Lang("ReasonRaid", langUser, Math.Ceiling(config.RaidCooldownMinutes - sinceRaid));
                    return false;
                }
                lastRaidDamage.Remove(key);
            }

            return true;
        }

        private void Notify(BasePlayer player)
        {
            float last;
            var t = Time.realtimeSinceStartup;
            if (lastNotified.TryGetValue(player.userID, out last) && t - last < config.NotifyCooldownSeconds) return;
            lastNotified[player.userID] = t;

            SendReply(player, Lang("Blocked", player.UserIDString));
        }

        private string GetName(ulong id)
        {
            var p = covalence.Players.FindPlayerById(id.ToString());
            return p?.Name ?? id.ToString();
        }

        #endregion

        #region Commands

        [ChatCommand("aor")]
        private void CmdAor(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermAdmin) && !player.IsAdmin)
            {
                SendReply(player, Lang("NoPermission", player.UserIDString));
                return;
            }

            var sub = args.Length > 0 ? args[0].ToLower() : "help";
            switch (sub)
            {
                case "check":
                {
                    RaycastHit hit;
                    var mask = LayerMask.GetMask("Construction", "Deployed", "Default");
                    BaseCombatEntity entity = null;
                    if (Physics.Raycast(player.eyes.HeadRay(), out hit, 30f, mask))
                        entity = hit.GetEntity() as BaseCombatEntity;

                    if (entity == null || !IsProtectableEntity(entity))
                    {
                        SendReply(player, Lang("NothingFound", player.UserIDString));
                        return;
                    }

                    string key;
                    var owners = GetOwners(entity, out key);
                    string reason;
                    if (IsProtected(key, owners, player.UserIDString, out reason))
                    {
                        var names = new List<string>();
                        foreach (var id in owners) names.Add(GetName(id));
                        SendReply(player, Lang("StatusProtected", player.UserIDString, string.Join(", ", names.ToArray())));
                    }
                    else
                    {
                        SendReply(player, Lang("StatusNotProtected", player.UserIDString, reason));
                    }
                    return;
                }
                case "clear":
                    lastRaidDamage.Clear();
                    protectionCache.Clear();
                    SendReply(player, "Raid cooldowns cleared.");
                    return;
                default:
                    SendReply(player, Lang("Help", player.UserIDString));
                    return;
            }
        }

        #endregion

        #region API

        // Other plugins: AntiOfflineRaid.Call<bool>("IsEntityProtected", entity)
        private bool IsEntityProtected(BaseCombatEntity entity)
        {
            if (entity == null || !IsProtectableEntity(entity)) return false;
            string key;
            var owners = GetOwners(entity, out key);
            return owners != null && owners.Count > 0 && IsProtectedCached(key, owners);
        }

        #endregion
    }
}
