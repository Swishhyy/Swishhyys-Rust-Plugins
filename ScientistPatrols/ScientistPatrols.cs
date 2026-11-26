using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("ScientistPatrols", "Swishhyy", "0.2.0")]
    [Description("Tiered scientist patrols that roam the roads, show on the map, and raid bases whose owners are online")]
    public class ScientistPatrols : RustPlugin
    {
        #region Constants & State

        private const string PermAdmin = "scientistpatrols.admin";

        private const string RoamScientistPrefab = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_roam.prefab";
        private const string HeavyScientistPrefab = "assets/rust.ai/agents/npcplayer/humannpc/heavyscientist/heavyscientist.prefab";
        private const string GenericMarkerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string LabelMarkerPrefab = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";

        private const float TickSeconds = 2f;
        private const float WaypointReachedDistance = 5f;
        private const float WaypointMaxAge = 90f;      // re-pick a waypoint if the squad seems stuck
        private const float RaidArrivalDistance = 6f;
        private const float AttackReachDistance = 4f;
        private const float SquadSpacing = 3f;

        private Configuration config;
        private readonly List<Patrol> patrols = new List<Patrol>();
        private readonly Dictionary<string, float> baseCooldowns = new Dictionary<string, float>();
        private readonly HashSet<string> warned = new HashSet<string>();
        private List<Vector3> roadPoints;
        private int blockMask;

        private enum PatrolState { Roaming, Raiding }

        private sealed class Patrol
        {
            public string TierKey;
            public TierConfig Tier;
            public readonly List<NPCPlayer> Members = new List<NPCPlayer>();
            public PatrolState State;
            public Vector3 Waypoint;
            public float WaypointSetAt;
            public float SpawnedAt;
            public BuildingPrivlidge Target;
            public float RaidEndsAt;
            public float NextAttackAt;
            public MapMarker Marker;

            public NPCPlayer Leader => Members.Count > 0 ? Members[0] : null;
        }

        #endregion

        #region Configuration

        private class TierConfig
        {
            [JsonProperty("Name shown on the map and in chat")]
            public string DisplayName = "Patrol";

            [JsonProperty("Scientist prefab")]
            public string Prefab = RoamScientistPrefab;

            [JsonProperty("Scientists per patrol")]
            public int SquadSize = 3;

            [JsonProperty("Health per scientist (0 = prefab default)")]
            public float Health = 0f;

            [JsonProperty("Replace the prefab's loadout")]
            public bool ReplaceLoadout = false;

            [JsonProperty("Weapon shortname (when replacing the loadout)")]
            public string Weapon = "";

            [JsonProperty("Clothing shortnames (when replacing the loadout)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Wear = new List<string>();

            [JsonProperty("Spawn weight (higher = more common)")]
            public float SpawnWeight = 1f;

            [JsonProperty("Damage per attack on a building block")]
            public float AttackDamage = 20f;

            [JsonProperty("Map marker colour (hex)")]
            public string MarkerColor = "#FFB300";
        }

        private class Configuration
        {
            [JsonProperty("Patrol tiers", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, TierConfig> Tiers = DefaultTiers();

            [JsonProperty("Max patrols at once")]
            public int MaxPatrols = 3;

            [JsonProperty("Minutes between new patrol spawns")]
            public float SpawnIntervalMinutes = 20f;

            [JsonProperty("Patrol lifetime in minutes (0 = until the server restarts)")]
            public float LifetimeMinutes = 90f;

            [JsonProperty("Roam radius around a patrol for its next road point (m)")]
            public float WanderRadius = 150f;

            [JsonProperty("Show patrols on the map")]
            public bool ShowOnMap = true;

            [JsonProperty("Map marker radius")]
            public float MarkerRadius = 0.2f;

            [JsonProperty("Chance a patrol spawn or raid is announced in chat (%)")]
            public float AnnounceChancePercent = 50f;

            [JsonProperty("Chance per check that a roaming patrol picks a base to raid (%)")]
            public float RaidChancePercent = 25f;

            [JsonProperty("Seconds between raid checks")]
            public float RaidCheckSeconds = 60f;

            [JsonProperty("Max distance from a patrol to a base it can raid (m)")]
            public float RaidRange = 250f;

            [JsonProperty("Minutes a raid lasts before the patrol gives up")]
            public float RaidMinutes = 15f;

            [JsonProperty("Minutes before the same base can be raided again")]
            public float BaseCooldownMinutes = 60f;

            [JsonProperty("Only raid bases where an owner is online")]
            public bool RequireOwnerOnline = true;

            [JsonProperty("Seconds between attacks while raiding")]
            public float AttackInterval = 2f;

            public static Dictionary<string, TierConfig> DefaultTiers() =>
                new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["light"] = new TierConfig
                    {
                        DisplayName = "Light Patrol",
                        Prefab = RoamScientistPrefab,
                        SquadSize = 2,
                        ReplaceLoadout = true,
                        Weapon = "pistol.semiauto",
                        Wear = new List<string> { "hoodie", "pants", "shoes.boots" },
                        SpawnWeight = 50f,
                        AttackDamage = 10f,
                        MarkerColor = "#7CB342"
                    },
                    ["normal"] = new TierConfig
                    {
                        DisplayName = "Patrol",
                        Prefab = RoamScientistPrefab,
                        SquadSize = 3,
                        SpawnWeight = 35f,
                        AttackDamage = 20f,
                        MarkerColor = "#FFB300"
                    },
                    ["heavy"] = new TierConfig
                    {
                        DisplayName = "Heavy Patrol",
                        Prefab = HeavyScientistPrefab,
                        SquadSize = 2,
                        SpawnWeight = 15f,
                        AttackDamage = 40f,
                        MarkerColor = "#E53935"
                    }
                };
        }

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) throw new JsonException("config file is empty");

                config.Tiers = config.Tiers == null || config.Tiers.Count == 0
                    ? Configuration.DefaultTiers()
                    : new Dictionary<string, TierConfig>(config.Tiers, StringComparer.OrdinalIgnoreCase);
                foreach (TierConfig tier in config.Tiers.Values)
                {
                    if (tier == null) continue;
                    tier.SquadSize = Mathf.Clamp(tier.SquadSize, 1, 10);
                    tier.Wear = tier.Wear ?? new List<string>();
                }

                config.MaxPatrols = Mathf.Max(0, config.MaxPatrols);
                config.SpawnIntervalMinutes = Mathf.Max(1f, config.SpawnIntervalMinutes);
                config.RaidCheckSeconds = Mathf.Max(5f, config.RaidCheckSeconds);
                config.AttackInterval = Mathf.Max(0.5f, config.AttackInterval);
                config.AnnounceChancePercent = Mathf.Clamp(config.AnnounceChancePercent, 0f, 100f);
                config.RaidChancePercent = Mathf.Clamp(config.RaidChancePercent, 0f, 100f);
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

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
        }

        private void OnServerInitialized()
        {
            blockMask = LayerMask.GetMask("Construction", "Deployed");
            roadPoints = BuildRoadPoints();
            if (roadPoints.Count == 0)
            {
                PrintError("No road points found on this map. Scientist patrols are disabled.");
                return;
            }

            timer.Every(TickSeconds, Tick);
            timer.Every(config.RaidCheckSeconds, RaidCheck);
            timer.Every(config.SpawnIntervalMinutes * 60f, TrySpawnPatrol);
            TrySpawnPatrol();
        }

        private void Unload()
        {
            foreach (Patrol patrol in patrols.ToList())
                RemovePatrol(patrol);
        }

        #endregion

        #region Command & API

        [ChatCommand("patrol")]
        private void CmdPatrol(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                player.ChatMessage("You don't have permission to use this.");
                return;
            }

            if (roadPoints == null || roadPoints.Count == 0)
            {
                player.ChatMessage("No road points loaded.");
                return;
            }

            string tier = args.Length > 0 ? args[0] : "random";
            if (!tier.Equals("random", StringComparison.OrdinalIgnoreCase) && !config.Tiers.ContainsKey(tier))
            {
                player.ChatMessage($"Unknown tier '{tier}'. Tiers: random, {string.Join(", ", config.Tiers.Keys)}");
                return;
            }

            bool spawned = SpawnPatrolAt(RandomRoadPoint(), tier);
            player.ChatMessage(spawned ? "Spawned a scientist patrol." : "Could not spawn a patrol. Check the console.");
        }

        // API for other plugins (RoadBradley uses this):
        // ScientistPatrols.Call("SpawnPatrolAt", position, "light" | "normal" | "heavy" | "random")
        private bool SpawnPatrolAt(Vector3 position, string tierKey)
        {
            string key = ResolveTierKey(tierKey);
            TierConfig tier;
            if (key == null || !config.Tiers.TryGetValue(key, out tier) || tier == null)
            {
                PrintWarning($"Unknown patrol tier '{tierKey}'.");
                return false;
            }

            var patrol = new Patrol
            {
                TierKey = key,
                Tier = tier,
                State = PatrolState.Roaming,
                SpawnedAt = Time.realtimeSinceStartup
            };

            for (int i = 0; i < tier.SquadSize; i++)
            {
                NPCPlayer npc = SpawnScientist(tier, position + SquadOffset(i) + Vector3.up * 0.5f);
                if (npc != null)
                    patrol.Members.Add(npc);
            }

            if (patrol.Members.Count == 0)
                return false;

            patrols.Add(patrol);
            if (config.ShowOnMap)
                patrol.Marker = MapMarker.Create(patrol.Leader.transform.position, tier.MarkerColor, tier.DisplayName, config.MarkerRadius);
            PickWaypoint(patrol);
            return true;
        }

        #endregion

        #region Spawning

        private void TrySpawnPatrol()
        {
            if (patrols.Count >= config.MaxPatrols || roadPoints == null || roadPoints.Count == 0)
                return;

            string key = PickTier();
            if (SpawnPatrolAt(RandomRoadPoint(), key) && Chance(config.AnnounceChancePercent))
                rust.BroadcastChat($"A {config.Tiers[key].DisplayName} is out on the roads.");
        }

        private NPCPlayer SpawnScientist(TierConfig tier, Vector3 position)
        {
            var npc = GameManager.server.CreateEntity(tier.Prefab, position, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f)) as NPCPlayer;
            if (npc == null)
            {
                WarnOnce("prefab:" + tier.Prefab, $"Could not spawn scientist prefab: {tier.Prefab}");
                return null;
            }

            npc.EnableSaving(false);
            npc.Spawn();

            var navigator = npc.GetComponent<BaseNavigator>();
            if (navigator != null)
                navigator.PlaceOnNavMesh(0f);

            if (tier.Health > 0f)
                npc.InitializeHealth(tier.Health, tier.Health);

            if (tier.ReplaceLoadout)
                ApplyLoadout(npc, tier);

            return npc;
        }

        private void ApplyLoadout(NPCPlayer npc, TierConfig tier)
        {
            npc.inventory.Strip();

            foreach (string shortname in tier.Wear)
            {
                Item clothing = ItemManager.CreateByName(shortname);
                if (clothing == null)
                {
                    WarnOnce("item:" + shortname, $"Item '{shortname}' doesn't exist on this server.");
                    continue;
                }
                if (!clothing.MoveToContainer(npc.inventory.containerWear))
                    clothing.Remove();
            }

            if (!string.IsNullOrEmpty(tier.Weapon))
            {
                Item weapon = ItemManager.CreateByName(tier.Weapon);
                if (weapon == null)
                {
                    WarnOnce("item:" + tier.Weapon, $"Item '{tier.Weapon}' doesn't exist on this server.");
                }
                else if (!weapon.MoveToContainer(npc.inventory.containerBelt, 0))
                {
                    weapon.Remove();
                }
                else
                {
                    var gun = weapon.GetHeldEntity() as BaseProjectile;
                    if (gun != null)
                        gun.primaryMagazine.contents = gun.primaryMagazine.capacity;
                    npc.UpdateActiveItem(weapon.uid);
                }
            }

            npc.SendNetworkUpdate();
        }

        private void RemovePatrol(Patrol patrol)
        {
            patrols.Remove(patrol);
            foreach (NPCPlayer npc in patrol.Members)
            {
                if (npc != null && !npc.IsDestroyed)
                    npc.Kill();
            }
            patrol.Members.Clear();
            patrol.Marker?.Destroy();
            patrol.Marker = null;
        }

        #endregion

        #region Movement

        private void PickWaypoint(Patrol patrol)
        {
            Vector3 from = patrol.Leader.transform.position;
            List<Vector3> nearby = roadPoints
                .Where(p =>
                {
                    float d = FlatDistance(p, from);
                    return d <= config.WanderRadius && d > WaypointReachedDistance;
                })
                .ToList();

            patrol.Waypoint = nearby.Count > 0 ? nearby[UnityEngine.Random.Range(0, nearby.Count)] : RandomRoadPoint();
            patrol.WaypointSetAt = Time.realtimeSinceStartup;
            MoveSquad(patrol, patrol.Waypoint);
        }

        // The leader heads for the destination; the rest spread out around it.
        private static void MoveSquad(Patrol patrol, Vector3 destination)
        {
            for (int i = 0; i < patrol.Members.Count; i++)
                MoveTo(patrol.Members[i], destination + SquadOffset(i));
        }

        private static void MoveTo(NPCPlayer npc, Vector3 destination)
        {
            var navigator = npc.GetComponent<BaseNavigator>();
            if (navigator != null)
                navigator.SetDestination(destination, BaseNavigator.NavigationSpeed.Normal, 1f, 0f);
        }

        private static Vector3 SquadOffset(int index)
        {
            if (index == 0)
                return Vector3.zero;
            float angle = index * 2.4f;
            return new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * SquadSpacing;
        }

        #endregion

        #region Ticks

        // Runs every TickSeconds: prunes dead scientists, drives roaming and raiding, moves map markers.
        private void Tick()
        {
            float now = Time.realtimeSinceStartup;

            foreach (Patrol patrol in patrols.ToList())
            {
                patrol.Members.RemoveAll(m => m == null || m.IsDestroyed || m.IsDead());
                if (patrol.Members.Count == 0)
                {
                    RemovePatrol(patrol);
                    continue;
                }

                if (config.LifetimeMinutes > 0f && now - patrol.SpawnedAt >= config.LifetimeMinutes * 60f)
                {
                    RemovePatrol(patrol);
                    continue;
                }

                if (patrol.State == PatrolState.Raiding)
                    UpdateRaid(patrol, now);
                else
                    UpdateRoam(patrol, now);

                patrol.Marker?.MoveTo(patrol.Leader.transform.position);
            }
        }

        private void UpdateRoam(Patrol patrol, float now)
        {
            bool arrived = FlatDistance(patrol.Leader.transform.position, patrol.Waypoint) <= WaypointReachedDistance;
            bool stale = now - patrol.WaypointSetAt >= WaypointMaxAge;
            if (arrived || stale)
                PickWaypoint(patrol);
        }

        private void UpdateRaid(Patrol patrol, float now)
        {
            BuildingPrivlidge target = patrol.Target;
            bool lost = target == null || target.IsDestroyed
                        || (config.RequireOwnerOnline && !AnyOwnerOnline(target))
                        || now >= patrol.RaidEndsAt;
            if (lost)
            {
                EndRaid(patrol);
                return;
            }

            Vector3 targetPos = target.transform.position;
            if (FlatDistance(patrol.Leader.transform.position, targetPos) > RaidArrivalDistance)
            {
                MoveSquad(patrol, targetPos);
                return;
            }

            if (now < patrol.NextAttackAt)
                return;

            patrol.NextAttackAt = now + config.AttackInterval;
            foreach (NPCPlayer member in patrol.Members)
            {
                BaseCombatEntity block = NearestBlock(member.transform.position);
                if (block != null)
                    block.Hurt(patrol.Tier.AttackDamage, DamageType.Bullet, member, false);
            }
        }

        #endregion

        #region Raiding

        // Every RaidCheckSeconds, roaming patrols may pick a nearby base to raid.
        private void RaidCheck()
        {
            if (patrols.Count == 0)
                return;

            float now = Time.realtimeSinceStartup;
            BuildingPrivlidge[] bases = UnityEngine.Object.FindObjectsOfType<BuildingPrivlidge>();

            foreach (Patrol patrol in patrols.ToList())
            {
                if (patrol.State != PatrolState.Roaming || patrol.Leader == null || !Chance(config.RaidChancePercent))
                    continue;

                BuildingPrivlidge best = null;
                float bestDistance = float.MaxValue;
                Vector3 from = patrol.Leader.transform.position;

                foreach (BuildingPrivlidge priv in bases)
                {
                    if (priv == null || priv.IsDestroyed || priv.net == null)
                        continue;

                    float cooldownEnds;
                    if (baseCooldowns.TryGetValue(KeyOf(priv), out cooldownEnds) && now < cooldownEnds)
                        continue;

                    if (config.RequireOwnerOnline && !AnyOwnerOnline(priv))
                        continue;

                    float distance = FlatDistance(priv.transform.position, from);
                    if (distance > config.RaidRange || distance >= bestDistance)
                        continue;

                    best = priv;
                    bestDistance = distance;
                }

                if (best != null)
                    StartRaid(patrol, best, now);
            }
        }

        private void StartRaid(Patrol patrol, BuildingPrivlidge target, float now)
        {
            patrol.State = PatrolState.Raiding;
            patrol.Target = target;
            patrol.RaidEndsAt = now + config.RaidMinutes * 60f;
            patrol.NextAttackAt = now;
            baseCooldowns[KeyOf(target)] = now + config.BaseCooldownMinutes * 60f;

            if (Chance(config.AnnounceChancePercent))
                rust.BroadcastChat($"A {patrol.Tier.DisplayName} is raiding a base!");
        }

        private void EndRaid(Patrol patrol)
        {
            patrol.State = PatrolState.Roaming;
            patrol.Target = null;
            PickWaypoint(patrol);
        }

        private BaseCombatEntity NearestBlock(Vector3 position)
        {
            BaseCombatEntity nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (Collider collider in Physics.OverlapSphere(position, AttackReachDistance, blockMask, QueryTriggerInteraction.Ignore))
            {
                var entity = collider.GetComponentInParent<BaseCombatEntity>();
                if (entity == null || entity is BasePlayer || entity.IsDestroyed)
                    continue;

                float distance = Vector3.Distance(entity.transform.position, position);
                if (distance < nearestDistance)
                {
                    nearest = entity;
                    nearestDistance = distance;
                }
            }
            return nearest;
        }

        #endregion

        #region Map markers

        // A coloured dot plus a hover label, moved as the patrol moves.
        private sealed class MapMarker
        {
            private MapMarkerGenericRadius _dot;
            private VendingMachineMapMarker _label;

            public static MapMarker Create(Vector3 position, string hexColor, string text, float radius)
            {
                var marker = new MapMarker();
                Color color;
                if (!ColorUtility.TryParseHtmlString(hexColor, out color))
                    color = Color.red;

                marker._dot = GameManager.server.CreateEntity(GenericMarkerPrefab, position, Quaternion.identity) as MapMarkerGenericRadius;
                if (marker._dot != null)
                {
                    marker._dot.EnableSaving(false);
                    marker._dot.alpha = 0.8f;
                    marker._dot.color1 = color;
                    marker._dot.color2 = Color.black;
                    marker._dot.radius = radius;
                    marker._dot.Spawn();
                    marker._dot.SendUpdate();
                }

                marker._label = GameManager.server.CreateEntity(LabelMarkerPrefab, position, Quaternion.identity) as VendingMachineMapMarker;
                if (marker._label != null)
                {
                    marker._label.EnableSaving(false);
                    marker._label.markerShopName = text;
                    marker._label.Spawn();
                }

                return marker;
            }

            public void MoveTo(Vector3 position)
            {
                if (_dot != null && !_dot.IsDestroyed)
                {
                    _dot.transform.position = position;
                    _dot.SendUpdate();
                }
                if (_label != null && !_label.IsDestroyed)
                {
                    _label.transform.position = position;
                    _label.SendNetworkUpdate();
                }
            }

            public void Destroy()
            {
                if (_dot != null && !_dot.IsDestroyed)
                    _dot.Kill();
                if (_label != null && !_label.IsDestroyed)
                    _label.Kill();
            }
        }

        #endregion

        #region Helpers

        private string ResolveTierKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Equals("random", StringComparison.OrdinalIgnoreCase))
                return PickTier();
            return config.Tiers.ContainsKey(key) ? config.Tiers.Keys.First(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) : null;
        }

        // Weighted random tier.
        private string PickTier()
        {
            float total = config.Tiers.Values.Sum(t => t == null ? 0f : Mathf.Max(0f, t.SpawnWeight));
            if (total <= 0f)
                return config.Tiers.Keys.FirstOrDefault();

            float roll = UnityEngine.Random.Range(0f, total);
            foreach (KeyValuePair<string, TierConfig> pair in config.Tiers)
            {
                roll -= pair.Value == null ? 0f : Mathf.Max(0f, pair.Value.SpawnWeight);
                if (roll < 0f)
                    return pair.Key;
            }
            return config.Tiers.Keys.Last();
        }

        private Vector3 RandomRoadPoint() => roadPoints[UnityEngine.Random.Range(0, roadPoints.Count)];

        private void WarnOnce(string key, string message)
        {
            if (warned.Add(key))
                PrintWarning(message);
        }

        private static bool Chance(float percent) => UnityEngine.Random.Range(0f, 100f) < percent;

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private static string KeyOf(BuildingPrivlidge priv) => "tc:" + priv.net.ID;

        // Owners are the players authorized on the cupboard. Handles both list and set types.
        private static bool AnyOwnerOnline(BuildingPrivlidge priv)
        {
            var list = priv.authorizedPlayers as IEnumerable;
            if (list == null)
                return false;

            foreach (object entry in list)
            {
                ulong id;
                if (entry is ulong)
                    id = (ulong)entry;
                else if (entry is ProtoBuf.PlayerNameID)
                    id = ((ProtoBuf.PlayerNameID)entry).userid;
                else
                    continue;

                if (BasePlayer.FindByID(id) != null)
                    return true;
            }
            return false;
        }

        private List<Vector3> BuildRoadPoints()
        {
            var points = new List<Vector3>();
            try
            {
                foreach (var road in TerrainMeta.Path.Roads)
                {
                    if (road?.Path?.Points == null) continue;
                    points.AddRange(road.Path.Points);
                }
            }
            catch (Exception ex)
            {
                PrintError($"Could not read road network: {ex.Message}");
            }
            return points;
        }

        #endregion
    }
}
