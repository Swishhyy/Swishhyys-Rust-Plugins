using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Road Bradley", "you", "0.2.0")]
    [Description("Spawns a Bradley APC that roams the road network, shows on the map, and leaves scientist patrols behind when destroyed.")]
    public class RoadBradley : RustPlugin
    {
        // Standard Bradley prefab. A real BradleyAPC seen on this server overrides it.
        private const string DefaultBradleyPrefab = "assets/prefabs/npc/m2bradley/bradleyapc.prefab";
        private const string GenericMarkerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string LabelMarkerPrefab = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";
        private const float MarkerUpdateSeconds = 2f;

        private string _bradleyPrefab = DefaultBradleyPrefab;
        private bool _prefabLearned;

        [PluginReference] private Plugin ScientistPatrols;

        private Configuration _config;
        private readonly List<BradleyAPC> _active = new List<BradleyAPC>();
        private readonly Dictionary<BradleyAPC, Timer> _despawnTimers = new Dictionary<BradleyAPC, Timer>();
        private readonly Dictionary<BradleyAPC, MapMarker> _markers = new Dictionary<BradleyAPC, MapMarker>();
        private Timer _spawnTimer;
        private List<Vector3> _roadPoints;

        #region Config

        private class Configuration
        {
            [JsonProperty("Max active Bradleys")]
            public int MaxActive = 1;

            [JsonProperty("Spawn interval (minutes)")]
            public float SpawnIntervalMinutes = 30f;

            [JsonProperty("Despawn after (minutes, 0 = never)")]
            public float DespawnAfterMinutes = 60f;

            [JsonProperty("Minimum distance between road points (m)")]
            public float MinLegDistance = 200f;

            [JsonProperty("Spawn on first server start")]
            public bool SpawnOnStart = true;

            [JsonProperty("Scientist patrols left behind when destroyed (needs ScientistPatrols)")]
            public int PatrolsOnDeath = 2;

            [JsonProperty("Tier of those patrols (light, normal, heavy or random)")]
            public string PatrolTierOnDeath = "random";

            [JsonProperty("Announce when a road Bradley is destroyed")]
            public bool AnnounceDeath = true;

            [JsonProperty("Show road Bradleys on the map")]
            public bool ShowOnMap = true;

            [JsonProperty("Map marker label")]
            public string MarkerLabel = "Road Bradley";

            [JsonProperty("Map marker colour (hex)")]
            public string MarkerColor = "#8E24AA";

            [JsonProperty("Map marker radius")]
            public float MarkerRadius = 0.25f;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>() ?? new Configuration();
            }
            catch (Exception ex)
            {
                PrintError($"Config is invalid, using defaults: {ex.Message}");
                _config = new Configuration();
            }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        protected override void LoadDefaultConfig() => _config = new Configuration();

        #endregion

        #region Hooks

        private void Init()
        {
            permission.RegisterPermission("roadbradley.admin", this);
        }

        private void OnServerInitialized()
        {
            _roadPoints = BuildRoadPoints();
            if (_roadPoints.Count < 2)
            {
                PrintError("No road points found on this map. Plugin disabled.");
                return;
            }

            Puts($"Loaded {_roadPoints.Count} road points.");

            var existing = UnityEngine.Object.FindObjectsOfType<BradleyAPC>().FirstOrDefault();
            if (existing != null)
            {
                _bradleyPrefab = existing.PrefabName;
                _prefabLearned = true;
            }

            Puts($"Using Bradley prefab: {_bradleyPrefab}");

            _spawnTimer = timer.Every(_config.SpawnIntervalMinutes * 60f, TrySpawn);
            timer.Every(MarkerUpdateSeconds, UpdateMarkers);
            if (_config.SpawnOnStart) TrySpawn();
        }

        private void Unload()
        {
            _spawnTimer?.Destroy();
            foreach (var t in _despawnTimers.Values) t?.Destroy();
            _despawnTimers.Clear();

            foreach (var marker in _markers.Values) marker.Destroy();
            _markers.Clear();

            foreach (var apc in _active.ToList())
            {
                if (apc != null && !apc.IsDestroyed) apc.Kill();
            }
            _active.Clear();
        }

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            var apc = entity as BradleyAPC;
            if (apc != null && !_prefabLearned)
            {
                _bradleyPrefab = apc.PrefabName;
                _prefabLearned = true;
                Puts($"Learned Bradley prefab: {_bradleyPrefab}");
            }
        }

        // Fires when the Bradley is destroyed by damage (not when it despawns).
        private void OnEntityDeath(BradleyAPC apc, HitInfo info)
        {
            if (apc == null || !_active.Contains(apc)) return;

            Vector3 wreck = apc.transform.position;
            if (_config.AnnounceDeath)
                rust.BroadcastChat("The road Bradley is down. Scientists are moving in to secure the wreck!");

            SpawnDeathPatrols(wreck);
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            var apc = entity as BradleyAPC;
            if (apc == null) return;

            // The despawn timer drops the APC from _active before killing it, so always clear its marker.
            RemoveMarker(apc);

            if (!_active.Contains(apc)) return;

            _active.Remove(apc);
            if (_despawnTimers.TryGetValue(apc, out var t))
            {
                t?.Destroy();
                _despawnTimers.Remove(apc);
            }
        }

        #endregion

        #region Commands

        [ChatCommand("roadbradley")]
        private void CmdRoadBradley(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, "roadbradley.admin"))
            {
                player.ChatMessage("You don't have permission to use this.");
                return;
            }

            if (args.Length > 0 && args[0].ToLower() == "spawn")
            {
                if (SpawnOne(out string reason)) player.ChatMessage("Spawned a road Bradley.");
                else player.ChatMessage($"Could not spawn: {reason}");
                return;
            }

            player.ChatMessage($"Active road Bradleys: {_active.Count}/{_config.MaxActive}. Use /roadbradley spawn to add one.");
        }

        #endregion

        #region Spawning

        private void TrySpawn()
        {
            if (_active.Count >= _config.MaxActive) return;
            SpawnOne(out _);
        }

        private bool SpawnOne(out string reason)
        {
            reason = null;
            if (_roadPoints == null || _roadPoints.Count < 2)
            {
                reason = "no road points loaded";
                return false;
            }

            var start = _roadPoints[UnityEngine.Random.Range(0, _roadPoints.Count)];
            var entity = GameManager.server.CreateEntity(_bradleyPrefab, start + Vector3.up * 0.5f, Quaternion.identity);
            if (entity == null)
            {
                reason = $"CreateEntity returned null at {start}";
                return false;
            }

            entity.Spawn();

            var apc = entity as BradleyAPC;
            if (apc == null)
            {
                entity.Kill();
                reason = "spawned entity was not a BradleyAPC";
                return false;
            }

            _active.Add(apc);
            SetRoute(apc, start);
            ScheduleDespawn(apc);

            if (_config.ShowOnMap)
                _markers[apc] = MapMarker.Create(apc.transform.position, _config.MarkerColor, _config.MarkerLabel, _config.MarkerRadius);
            return true;
        }

        // Leaves scientist patrols at the wreck through the ScientistPatrols plugin.
        private void SpawnDeathPatrols(Vector3 wreck)
        {
            if (_config.PatrolsOnDeath <= 0) return;

            if (ScientistPatrols == null || !ScientistPatrols.IsLoaded)
            {
                PrintWarning("ScientistPatrols isn't loaded, so no patrols were left at the Bradley wreck.");
                return;
            }

            for (int i = 0; i < _config.PatrolsOnDeath; i++)
            {
                // Spread the patrols around the wreck so they don't spawn inside it or each other.
                float angle = i * Mathf.PI * 2f / _config.PatrolsOnDeath;
                Vector3 spot = wreck + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 8f;
                if (TerrainMeta.HeightMap != null)
                    spot.y = TerrainMeta.HeightMap.GetHeight(spot);

                object spawned = ScientistPatrols.Call("SpawnPatrolAt", spot, _config.PatrolTierOnDeath);
                if (!(spawned is bool) || !(bool)spawned)
                    PrintWarning($"ScientistPatrols could not spawn a '{_config.PatrolTierOnDeath}' patrol at the wreck.");
            }
        }

        #endregion

        #region Map markers

        private void UpdateMarkers()
        {
            foreach (var pair in _markers.ToList())
            {
                if (pair.Key == null || pair.Key.IsDestroyed)
                {
                    pair.Value.Destroy();
                    _markers.Remove(pair.Key);
                    continue;
                }
                pair.Value.MoveTo(pair.Key.transform.position);
            }
        }

        private void RemoveMarker(BradleyAPC apc)
        {
            MapMarker marker;
            if (!_markers.TryGetValue(apc, out marker)) return;
            marker.Destroy();
            _markers.Remove(apc);
        }

        // A coloured dot plus a hover label, moved as the Bradley moves.
        private sealed class MapMarker
        {
            private MapMarkerGenericRadius _dot;
            private VendingMachineMapMarker _label;

            public static MapMarker Create(Vector3 position, string hexColor, string text, float radius)
            {
                var marker = new MapMarker();
                Color color;
                if (!ColorUtility.TryParseHtmlString(hexColor, out color))
                    color = Color.magenta;

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

        private void ScheduleDespawn(BradleyAPC apc)
        {
            if (_config.DespawnAfterMinutes <= 0f) return;

            _despawnTimers[apc] = timer.Once(_config.DespawnAfterMinutes * 60f, () =>
            {
                _despawnTimers.Remove(apc);
                if (apc != null && !apc.IsDestroyed)
                {
                    _active.Remove(apc);
                    apc.Kill();
                }
            });
        }

        #endregion

        #region Routing

        /// <summary>
        /// Builds a looping route of random road points and hands it to the APC's
        /// path system. The chain starts at `start` and each leg is at least
        /// MinLegDistance away from the previous point.
        /// </summary>
        private void SetRoute(BradleyAPC apc, Vector3 start)
        {
            var route = BuildRoute(start);
            if (route.Count < 2)
            {
                PrintWarning("Could not build a road route; this Bradley will use the game's default patrol.");
                return;
            }

            if (!TryApplyRoute(apc, route))
            {
                PrintWarning("Could not find a writable route list on this BradleyAPC. Member dump follows once.");
                LogBradleyMembers(apc);
            }
        }

        private const int RouteLegs = 8;

        // Start point followed by up to RouteLegs random road points, each at least MinLegDistance from the last.
        private List<Vector3> BuildRoute(Vector3 start)
        {
            var route = new List<Vector3> { start };
            var current = start;

            for (int i = 0; i < RouteLegs; i++)
            {
                var candidates = _roadPoints.Where(p => Vector3.Distance(p, current) >= _config.MinLegDistance).ToList();
                if (candidates.Count == 0) break;

                current = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                route.Add(current);
            }
            return route;
        }

        // Writes the route into the first List<Vector3> on the APC (or on a path object it holds)
        // whose name looks like a route. Returns false if none is found.
        private bool TryApplyRoute(BradleyAPC apc, List<Vector3> route)
        {
            var list = FindRouteList(apc, 2);
            if (list == null) return false;

            list.Clear();
            list.AddRange(route);
            return true;
        }

        private static List<Vector3> FindRouteList(object owner, int depth)
        {
            if (owner == null || depth < 0) return null;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = owner.GetType();

            foreach (var f in type.GetFields(flags))
            {
                if (f.FieldType == typeof(List<Vector3>) && IsRouteName(f.Name) && f.GetValue(owner) is List<Vector3> found)
                    return found;
            }

            foreach (var p in type.GetProperties(flags))
            {
                if (p.PropertyType == typeof(List<Vector3>) && p.CanRead && IsRouteName(p.Name)
                    && p.GetIndexParameters().Length == 0 && p.GetValue(owner) is List<Vector3> found)
                    return found;
            }

            // The route may sit on a nested path object, so look one level deeper.
            foreach (var f in type.GetFields(flags))
            {
                if (f.FieldType.IsClass && f.FieldType != typeof(string) && f.FieldType.Name.ToLowerInvariant().Contains("path"))
                {
                    var nested = FindRouteList(f.GetValue(owner), depth - 1);
                    if (nested != null) return nested;
                }
            }

            return null;
        }

        private static bool IsRouteName(string name)
        {
            name = name.ToLowerInvariant();
            return name.Contains("path") || name.Contains("point") || name.Contains("route") || name.Contains("interest");
        }

        private static bool _membersLogged;

        private void LogBradleyMembers(BradleyAPC apc)
        {
            if (_membersLogged) return;
            _membersLogged = true;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = apc.GetType();

            var fields = type.GetFields(flags).Select(f => $"{f.FieldType.Name} {f.Name}");
            var props = type.GetProperties(flags).Select(p => $"{p.PropertyType.Name} {p.Name}");

            Puts("BradleyAPC fields: " + string.Join(", ", fields));
            Puts("BradleyAPC properties: " + string.Join(", ", props));
        }

        /// <summary>
        /// Collects road points from the map's road network.
        /// ASSUMPTION TO VERIFY: TerrainMeta.Path.Roads is a list of paths whose
        /// Points array holds the road polyline.
        /// </summary>
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
