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
    [Info("Road Bradley", "you", "0.3.0")]
    [Description("Spawns a Bradley APC that roams the road network, shows on the map, and leaves scientist patrols behind when destroyed.")]
    public class RoadBradley : RustPlugin
    {
        // Standard Bradley prefab. A real BradleyAPC seen on this server overrides it.
        private const string DefaultBradleyPrefab = "assets/prefabs/npc/m2bradley/bradleyapc.prefab";
        private const string GenericMarkerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string LabelMarkerPrefab = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";
        private const float MarkerUpdateSeconds = 2f;
        private const float RouteCheckSeconds = 3f;

        private string _bradleyPrefab = DefaultBradleyPrefab;
        private bool _prefabLearned;

        [PluginReference] private Plugin ScientistPatrols;

        private Configuration _config;
        private readonly List<BradleyAPC> _active = new List<BradleyAPC>();
        private readonly Dictionary<BradleyAPC, Timer> _despawnTimers = new Dictionary<BradleyAPC, Timer>();
        private readonly Dictionary<BradleyAPC, MapMarker> _markers = new Dictionary<BradleyAPC, MapMarker>();
        private Timer _spawnTimer;
        private List<List<Vector3>> _roads;   // each road as evenly spaced points, in order
        private readonly Dictionary<BradleyAPC, BradleyRoute> _routes = new Dictionary<BradleyAPC, BradleyRoute>();

        private sealed class BradleyRoute
        {
            public List<Vector3> Loop;      // the road there and back, so the APC never has to leave it
            public List<Vector3> Written;   // what was last written into the APC's route list
        }

        #region Config

        private class Configuration
        {
            [JsonProperty("Max active Bradleys")]
            public int MaxActive = 1;

            [JsonProperty("Spawn interval (minutes)")]
            public float SpawnIntervalMinutes = 30f;

            [JsonProperty("Despawn after (minutes, 0 = never)")]
            public float DespawnAfterMinutes = 60f;

            [JsonProperty("Minimum road length to drive (m)")]
            public float MinRoadLength = 400f;

            [JsonProperty("Minimum road width to drive (m, 0 = any; skips narrow trails)")]
            public float MinRoadWidth = 8f;

            [JsonProperty("Spacing between route points along the road (m)")]
            public float RouteSpacing = 10f;

            [JsonProperty("Distance off the road before steering back (m)")]
            public float OffRoadTolerance = 12f;

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
            public float MarkerRadius = 0.08f;

            [JsonProperty("Show a hover label (shop icon) with the marker")]
            public bool ShowMarkerLabel = true;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>() ?? new Configuration();
                // 0.2.0 shipped a marker that covered half a grid square.
                if (Mathf.Approximately(_config.MarkerRadius, 0.25f)) _config.MarkerRadius = 0.08f;
                _config.RouteSpacing = Mathf.Max(2f, _config.RouteSpacing);
                _config.OffRoadTolerance = Mathf.Max(3f, _config.OffRoadTolerance);
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
            _roads = BuildRoads();
            if (_roads.Count == 0)
            {
                PrintError("No roads long and wide enough were found on this map. Plugin disabled.");
                return;
            }

            Puts($"Loaded {_roads.Count} drivable road(s).");

            var existing = UnityEngine.Object.FindObjectsOfType<BradleyAPC>().FirstOrDefault();
            if (existing != null)
            {
                _bradleyPrefab = existing.PrefabName;
                _prefabLearned = true;
            }

            Puts($"Using Bradley prefab: {_bradleyPrefab}");

            _spawnTimer = timer.Every(_config.SpawnIntervalMinutes * 60f, TrySpawn);
            timer.Every(MarkerUpdateSeconds, UpdateMarkers);
            timer.Every(RouteCheckSeconds, KeepOnRoads);
            if (_config.SpawnOnStart) TrySpawn();
        }

        private void Unload()
        {
            _spawnTimer?.Destroy();
            foreach (var t in _despawnTimers.Values) t?.Destroy();
            _despawnTimers.Clear();

            foreach (var marker in _markers.Values) marker.Destroy();
            _markers.Clear();
            _routes.Clear();

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
            _routes.Remove(apc);

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
            if (_roads == null || _roads.Count == 0)
            {
                reason = "no drivable roads loaded";
                return false;
            }

            List<Vector3> road = _roads[UnityEngine.Random.Range(0, _roads.Count)];
            var start = road[UnityEngine.Random.Range(0, road.Count)];
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
            SetRoute(apc, road);
            ScheduleDespawn(apc);

            if (_config.ShowOnMap)
                _markers[apc] = new MapMarker(apc.transform.position, _config.MarkerColor, _config.MarkerLabel, _config.MarkerRadius, _config.ShowMarkerLabel);
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

        // A coloured dot plus an optional hover label. Rust clients don't follow a marker that
        // moves on the server, so it is respawned at the new spot once it has moved far enough.
        private sealed class MapMarker
        {
            private const float RespawnDistance = 5f;

            private readonly Color _color;
            private readonly string _text;
            private readonly float _radius;
            private readonly bool _showLabel;
            private Vector3 _position;
            private MapMarkerGenericRadius _dot;
            private VendingMachineMapMarker _label;

            public MapMarker(Vector3 position, string hexColor, string text, float radius, bool showLabel)
            {
                if (!ColorUtility.TryParseHtmlString(hexColor, out _color))
                    _color = Color.magenta;
                _text = text;
                _radius = radius;
                _showLabel = showLabel;
                Spawn(position);
            }

            public void MoveTo(Vector3 position)
            {
                if (Vector3.Distance(position, _position) < RespawnDistance)
                    return;
                Destroy();
                Spawn(position);
            }

            private void Spawn(Vector3 position)
            {
                _position = position;

                _dot = GameManager.server.CreateEntity(GenericMarkerPrefab, position, Quaternion.identity) as MapMarkerGenericRadius;
                if (_dot != null)
                {
                    _dot.EnableSaving(false);
                    _dot.alpha = 0.9f;
                    _dot.color1 = _color;
                    _dot.color2 = Color.black;
                    _dot.radius = _radius;
                    _dot.Spawn();
                    _dot.SendUpdate();
                }

                if (!_showLabel)
                    return;

                _label = GameManager.server.CreateEntity(LabelMarkerPrefab, position, Quaternion.identity) as VendingMachineMapMarker;
                if (_label != null)
                {
                    _label.EnableSaving(false);
                    _label.markerShopName = _text;
                    _label.Spawn();
                }
            }

            public void Destroy()
            {
                if (_dot != null && !_dot.IsDestroyed)
                    _dot.Kill();
                if (_label != null && !_label.IsDestroyed)
                    _label.Kill();
                _dot = null;
                _label = null;
            }
        }

        #endregion

        #region Despawn

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

        // Gives the APC one road's points, in order, there and back on a loop, so it drives along
        // the road instead of cutting across country between far-apart points.
        private void SetRoute(BradleyAPC apc, List<Vector3> road)
        {
            var state = new BradleyRoute { Loop = BuildLoop(road) };
            _routes[apc] = state;
            ApplyRoute(apc, state, NearestIndex(state.Loop, apc.transform.position, out _));
        }

        // Every few seconds: if the APC strayed off the road, or its own AI replaced the route,
        // write the route again starting from the nearest road point so it drives straight back.
        private void KeepOnRoads()
        {
            foreach (var pair in _routes.ToList())
            {
                BradleyAPC apc = pair.Key;
                BradleyRoute state = pair.Value;
                if (apc == null || apc.IsDestroyed)
                {
                    _routes.Remove(apc);
                    continue;
                }

                float offRoad;
                int nearest = NearestIndex(state.Loop, apc.transform.position, out offRoad);
                List<Vector3> list = FindRouteList(apc, 2);
                bool replaced = list == null || state.Written == null || list.Count != state.Written.Count
                                || (list.Count > 0 && list[0] != state.Written[0]);

                if (replaced || offRoad > _config.OffRoadTolerance)
                    ApplyRoute(apc, state, nearest);
            }
        }

        private void ApplyRoute(BradleyAPC apc, BradleyRoute state, int startIndex)
        {
            var rotated = new List<Vector3>(state.Loop.Count);
            for (int i = 0; i < state.Loop.Count; i++)
                rotated.Add(state.Loop[(startIndex + i) % state.Loop.Count]);

            List<Vector3> list = FindRouteList(apc, 2);
            if (list == null)
            {
                if (!_membersLogged)
                    PrintWarning("Could not find a writable route list on this BradleyAPC, so it can't be kept on roads. Member dump follows.");
                LogBradleyMembers(apc);
                return;
            }

            list.Clear();
            list.AddRange(rotated);
            state.Written = rotated;

            // Start at the first point and loop at the end, where the APC exposes those settings.
            SetMember(apc, typeof(int), "pathindex", 0);
            SetMember(apc, typeof(bool), "loop", true);
        }

        // The road forward and then back again; a closed road is just driven round.
        private List<Vector3> BuildLoop(List<Vector3> road)
        {
            var loop = new List<Vector3>(road);
            bool closed = road.Count > 2 && Vector3.Distance(road[0], road[road.Count - 1]) <= _config.RouteSpacing * 2f;
            if (!closed)
            {
                for (int i = road.Count - 2; i >= 1; i--)
                    loop.Add(road[i]);
            }
            return loop;
        }

        private static int NearestIndex(List<Vector3> points, Vector3 position, out float distance)
        {
            int best = 0;
            distance = float.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 a = points[i];
                float dx = a.x - position.x, dz = a.z - position.z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < distance)
                {
                    distance = d;
                    best = i;
                }
            }
            return best;
        }

        private static bool _membersReported;

        // Sets the first instance field of the given type whose name contains `nameHint`.
        private void SetMember(BradleyAPC apc, Type type, string nameHint, object value)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo field = apc.GetType().GetFields(flags)
                .FirstOrDefault(f => f.FieldType == type && f.Name.ToLowerInvariant().Contains(nameHint));
            if (field == null)
                return;

            try
            {
                field.SetValue(apc, value);
                if (!_membersReported)
                    Puts($"Route control: set {apc.GetType().Name}.{field.Name} = {value}");
            }
            catch (Exception ex)
            {
                PrintWarning($"Could not set {field.Name}: {ex.Message}");
            }

            if (nameHint == "loop")
                _membersReported = true;
        }

        private static List<Vector3> FindRouteList(object owner, int depth)
        {
            if (owner == null || depth < 0) return null;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = owner.GetType();

            foreach (var f in type.GetFields(flags))
            {
                if (f.FieldType == typeof(List<Vector3>) && IsRouteName(f.Name) && SafeGet(() => f.GetValue(owner)) is List<Vector3> found)
                    return found;
            }

            foreach (var p in type.GetProperties(flags))
            {
                if (p.PropertyType == typeof(List<Vector3>) && p.CanRead && IsRouteName(p.Name)
                    && p.GetIndexParameters().Length == 0 && SafeGet(() => p.GetValue(owner)) is List<Vector3> found)
                    return found;
            }

            // The route may sit on a nested path object, so look one level deeper.
            foreach (var f in type.GetFields(flags))
            {
                if (f.FieldType.IsClass && f.FieldType != typeof(string) && f.FieldType.Name.ToLowerInvariant().Contains("path"))
                {
                    var nested = FindRouteList(SafeGet(() => f.GetValue(owner)), depth - 1);
                    if (nested != null) return nested;
                }
            }

            return null;
        }

        // Some engine getters throw when read by reflection; treat that member as absent.
        private static object SafeGet(Func<object> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return null;
            }
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

        // Each road as points every RouteSpacing metres, skipping short roads and (where the game
        // exposes a width) narrow trails.
        private List<List<Vector3>> BuildRoads()
        {
            var roads = new List<List<Vector3>>();
            try
            {
                foreach (var road in TerrainMeta.Path.Roads)
                {
                    Vector3[] points = road?.Path?.Points;
                    if (points == null || points.Length < 2) continue;

                    if (_config.MinRoadWidth > 0f && SafeGet(() => road.GetType().GetField("Width")?.GetValue(road)) is float width
                        && width < _config.MinRoadWidth)
                        continue;

                    var spaced = new List<Vector3> { points[0] };
                    float length = 0f;
                    for (int i = 1; i < points.Length; i++)
                    {
                        length += Vector3.Distance(points[i - 1], points[i]);
                        if (Vector3.Distance(spaced[spaced.Count - 1], points[i]) >= _config.RouteSpacing)
                            spaced.Add(points[i]);
                    }
                    if (spaced[spaced.Count - 1] != points[points.Length - 1])
                        spaced.Add(points[points.Length - 1]);

                    if (length >= _config.MinRoadLength && spaced.Count >= 2)
                        roads.Add(spaced);
                }
            }
            catch (Exception ex)
            {
                PrintError($"Could not read road network: {ex.Message}");
            }
            return roads;
        }

        #endregion
    }
}
