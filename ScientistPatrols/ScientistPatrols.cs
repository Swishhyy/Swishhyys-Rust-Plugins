using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("ScientistPatrols", "Swishhyy", "0.1.0")]
    [Description("Scientist patrols that roam the roads and raid bases whose owners are online")]
    public class ScientistPatrols : RustPlugin
    {
        #region Constants & State

        private const string PermAdmin = "scientistpatrols.admin";

        // Unverified on this build: confirm the scientist prefab path on your server.
        private const string ScientistPath = "assets/prefabs/npc/scientist/scientist.prefab";
        private const float TickSeconds = 2f;
        private const float WaypointReachedDistance = 5f;
        private const float WaypointMaxAge = 90f;      // re-pick a waypoint if the navigator seems stuck
        private const float RaidArrivalDistance = 6f;
        private const float AttackReachDistance = 4f;

        private Configuration config;
        private readonly List<Patrol> patrols = new List<Patrol>();
        private readonly Dictionary<string, float> baseCooldowns = new Dictionary<string, float>();
        private List<Vector3> roadPoints;
        private int blockMask;

        private enum PatrolState { Roaming, Raiding }

        private sealed class Patrol
        {
            public NPCPlayer Npc;
            public PatrolState State;
            public Vector3 Waypoint;
            public float WaypointSetAt;
            public float SpawnedAt;
            public BuildingPrivlidge Target;
            public string TargetKey;
            public float RaidEndsAt;
            public float NextAttackAt;
        }

        #endregion

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Max patrols at once")]
            public int MaxPatrols = 3;

            [JsonProperty("Minutes between new patrol spawns")]
            public float SpawnIntervalMinutes = 20f;

            [JsonProperty("Patrol lifetime in minutes (0 = until the server restarts)")]
            public float LifetimeMinutes = 90f;

            [JsonProperty("Roam radius around a patrol for its next road point (m)")]
            public float WanderRadius = 150f;

            [JsonProperty("Chance a patrol spawn or raid is announced in chat (%)")]
            public float AnnounceChancePercent = 50f;

            [JsonProperty("Chance per check that an idle patrol picks a base to raid (%)")]
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

            [JsonProperty("Damage per attack on a building block")]
            public float AttackDamage = 20f;

            [JsonProperty("Seconds between attacks while raiding")]
            public float AttackInterval = 2f;
        }

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) throw new JsonException("config file is empty");
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
            foreach (Patrol patrol in patrols)
            {
                if (patrol.Npc != null && !patrol.Npc.IsDestroyed)
                    patrol.Npc.Kill();
            }
            patrols.Clear();
        }

        #endregion

        #region Command

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

            bool spawned = SpawnPatrol();
            player.ChatMessage(spawned ? "Spawned a scientist patrol." : "Could not spawn a patrol. Check the console.");
        }

        #endregion

        #region Spawning

        private void TrySpawnPatrol()
        {
            if (patrols.Count >= config.MaxPatrols)
                return;

            if (SpawnPatrol() && Chance(config.AnnounceChancePercent))
                rust.BroadcastChat("A scientist patrol is out on the roads.");
        }

        private bool SpawnPatrol()
        {
            if (roadPoints == null || roadPoints.Count == 0)
                return false;

            Vector3 start = roadPoints[UnityEngine.Random.Range(0, roadPoints.Count)];
            var npc = GameManager.server.CreateEntity(ScientistPath, start + Vector3.up * 0.5f, Quaternion.identity) as NPCPlayer;
            if (npc == null)
            {
                PrintWarning($"Could not spawn scientist prefab: {ScientistPath}");
                return false;
            }

            npc.Spawn();
            var navigator = npc.GetComponent<BaseNavigator>();
            if (navigator != null)
                navigator.PlaceOnNavMesh(0f);

            var patrol = new Patrol
            {
                Npc = npc,
                State = PatrolState.Roaming,
                SpawnedAt = Time.realtimeSinceStartup
            };
            patrols.Add(patrol);
            PickWaypoint(patrol);
            return true;
        }

        private void RemovePatrol(Patrol patrol)
        {
            patrols.Remove(patrol);
            if (patrol.Npc != null && !patrol.Npc.IsDestroyed)
                patrol.Npc.Kill();
        }

        #endregion

        #region Movement

        private void PickWaypoint(Patrol patrol)
        {
            Vector3 from = patrol.Npc.transform.position;
            List<Vector3> nearby = roadPoints
                .Where(p => FlatDistance(p, from) <= config.WanderRadius && FlatDistance(p, from) > WaypointReachedDistance)
                .ToList();

            patrol.Waypoint = nearby.Count > 0
                ? nearby[UnityEngine.Random.Range(0, nearby.Count)]
                : roadPoints[UnityEngine.Random.Range(0, roadPoints.Count)];
            patrol.WaypointSetAt = Time.realtimeSinceStartup;
            MoveTo(patrol.Npc, patrol.Waypoint);
        }

        private static void MoveTo(NPCPlayer npc, Vector3 destination)
        {
            var navigator = npc.GetComponent<BaseNavigator>();
            if (navigator != null)
                navigator.SetDestination(destination, BaseNavigator.NavigationSpeed.Normal, 1f, 0f);
        }

        #endregion

        #region Ticks

        // Runs every TickSeconds: drives roaming and raiding for each patrol.
        private void Tick()
        {
            float now = Time.realtimeSinceStartup;

            for (int i = patrols.Count - 1; i >= 0; i--)
            {
                Patrol patrol = patrols[i];
                if (patrol.Npc == null || patrol.Npc.IsDestroyed)
                {
                    patrols.RemoveAt(i);
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
            }
        }

        private void UpdateRoam(Patrol patrol, float now)
        {
            bool arrived = FlatDistance(patrol.Npc.transform.position, patrol.Waypoint) <= WaypointReachedDistance;
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
            if (FlatDistance(patrol.Npc.transform.position, targetPos) > RaidArrivalDistance)
            {
                MoveTo(patrol.Npc, targetPos);
                return;
            }

            if (now >= patrol.NextAttackAt)
            {
                patrol.NextAttackAt = now + config.AttackInterval;
                BaseCombatEntity block = NearestBlock(patrol.Npc.transform.position);
                if (block != null)
                    block.Hurt(config.AttackDamage, DamageType.Bullet, patrol.Npc, false);
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
            List<BuildingPrivlidge> bases = UnityEngine.Object.FindObjectsOfType<BuildingPrivlidge>().ToList();

            foreach (Patrol patrol in patrols.ToList())
            {
                if (patrol.State != PatrolState.Roaming || !Chance(config.RaidChancePercent))
                    continue;

                BuildingPrivlidge best = null;
                float bestDistance = float.MaxValue;
                Vector3 from = patrol.Npc.transform.position;

                foreach (BuildingPrivlidge priv in bases)
                {
                    if (priv == null || priv.IsDestroyed || priv.net == null)
                        continue;

                    string key = KeyOf(priv);
                    float cooldownEnds;
                    if (baseCooldowns.TryGetValue(key, out cooldownEnds) && now < cooldownEnds)
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
            patrol.TargetKey = KeyOf(target);
            patrol.RaidEndsAt = now + config.RaidMinutes * 60f;
            patrol.NextAttackAt = now;
            baseCooldowns[patrol.TargetKey] = now + config.BaseCooldownMinutes * 60f;

            if (Chance(config.AnnounceChancePercent))
                rust.BroadcastChat("Scientists are raiding a base!");
        }

        private void EndRaid(Patrol patrol)
        {
            patrol.State = PatrolState.Roaming;
            patrol.Target = null;
            patrol.TargetKey = null;
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

        #region Helpers

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
