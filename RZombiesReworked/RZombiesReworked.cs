using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using UnityEngine;
using UnityEngine.AI;
using Pool = Facepunch.Pool;
using Random = UnityEngine.Random;

namespace Oxide.Plugins
{
    [Info("RZombies Reworked", "Swishhyy", "1.6.0")]
    [Description("Scheduled zombie hordes and monument outbreaks, built on Rust's scarecrow NPC")]
    public class RZombiesReworked : RustPlugin
    {
        #region Constants & State

        private const string PermAdmin = "rzombiesreworked.admin";
        private const string PermImmune = "rzombiesreworked.immune";

        private const string ScarecrowPath = "assets/prefabs/npc/scarecrow/scarecrow.prefab";
        private const int BeancanItemId = 1840822026;
        private const string GlowEyesShortname = "gloweyes";
        private const string CandleHatShortname = "hat.candle";
        private const string FlashlightShortname = "flashlight.held";
        private const string TorchShortname = "torch";

        private const float QueueTick = 0.2f;     // spawn/despawn queue
        private const float WatchdogTick = 2f;    // water + leash checks
        private const float ScheduleTick = 5f;    // horde windows and refills
        private const int FallenMemory = 5000;

        private PluginConfig _cfg;
        private StoredData _data;

        [PluginReference] private Plugin Kits;
        [PluginReference] private Plugin Vanish;

        private sealed class Zombie
        {
            public ScarecrowNPC Npc;
            public Group Group;
            public ZombieProfile Profile;
            public float SecondsUnderwater;
            public ItemDefinition Light; // candle hat, flashlight or torch, if the zombie carries one
            public bool LightOn;
        }

        private readonly Dictionary<ScarecrowNPC, Zombie> _alive = new Dictionary<ScarecrowNPC, Zombie>();
        private readonly Dictionary<ulong, string> _fallen = new Dictionary<ulong, string>();   // dead zombie id -> name, for corpses/backpacks
        private readonly Queue<Group> _spawnQueue = new Queue<Group>();
        private readonly Queue<ScarecrowNPC> _despawnQueue = new Queue<ScarecrowNPC>();
        private readonly List<Horde> _hordes = new List<Horde>();
        private readonly List<OutbreakSite> _sites = new List<OutbreakSite>();
        private readonly Dictionary<ulong, AmbientGroup> _ambient = new Dictionary<ulong, AmbientGroup>();
        private ZombieProfile _ambientProfile;
        private bool _isNight;
        private readonly HashSet<string> _neverTarget = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _kitWarnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private ItemDefinition _glowEyes;
        private bool _glowEyesWarned;
        private readonly HashSet<NightLight> _lightWarnings = new HashSet<NightLight>();

        private int _obstacleMask;
        private int _buildingMask;

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermImmune, this);
            AddCovalenceCommand("rz", nameof(RzCommand));

            foreach (string prefab in _cfg.Targeting.NeverTarget)
            {
                if (!string.IsNullOrWhiteSpace(prefab))
                    _neverTarget.Add(prefab.Trim().Replace(".prefab", string.Empty));
            }

            if (_cfg.Targeting.SentriesShootZombies)
                Unsubscribe(nameof(OnTurretTarget));
            if (_cfg.Corpses.DropLoot)
                Unsubscribe(nameof(OnCorpsePopulate));
            if (!AnyDamageScaling())
                Unsubscribe(nameof(OnEntityTakeDamage));

            _data = LoadData();
        }

        private void OnServerInitialized()
        {
            _obstacleMask = LayerMask.GetMask("Construction", "Deployed", "World", "Tree", "Vehicle_Large", "Vehicle_Detailed");
            _buildingMask = LayerMask.GetMask("Construction", "Deployed");

            if (Kits == null && UsesKits())
                PrintWarning("A zombie profile lists kits but the Kits plugin isn't loaded - zombies will keep their default gear.");

            _glowEyes = ItemManager.FindItemDefinition(GlowEyesShortname);
            if (_glowEyes == null)
                PrintWarning($"Item '{GlowEyesShortname}' wasn't found - the glowing eyes option will do nothing.");


            foreach (HordeConfig hordeCfg in _cfg.Hordes)
            {
                if (!hordeCfg.Enabled)
                    continue;

                HordeState state;
                if (!_data.Hordes.TryGetValue(hordeCfg.Name, out state))
                    _data.Hordes[hordeCfg.Name] = state = new HordeState();

                _hordes.Add(new Horde(this, hordeCfg, ResolveProfile(hordeCfg.Profile), state));
            }

            if (_cfg.Ambient.Enabled)
                _ambientProfile = ResolveProfile(_cfg.Ambient.Profile);

            if (_cfg.Outbreaks.Enabled)
                FindOutbreakSites();

            timer.Every(QueueTick, ProcessQueues);
            timer.Every(WatchdogTick, Watchdog);
            timer.Every(ScheduleTick, Schedule);
            Schedule();
        }

        private void Unload()
        {
            SaveData();

            var everyone = Pool.Get<List<ScarecrowNPC>>();
            everyone.AddRange(_alive.Keys);
            everyone.AddRange(_despawnQueue);
            _alive.Clear();
            _despawnQueue.Clear();

            foreach (ScarecrowNPC npc in everyone)
            {
                if (npc != null && !npc.IsDestroyed)
                    npc.AdminKill();
            }
            Pool.FreeUnmanaged(ref everyone);
        }

        private void OnServerSave() => SaveData();

        #endregion

        #region Groups

        private abstract class Group
        {
            public readonly HashSet<Zombie> Members = new HashSet<Zombie>();
            public int Queued;

            public abstract string Label { get; }
            public abstract ZombieProfile Profile { get; }
            public abstract bool WantsMore { get; }
            public abstract bool TryPickSpot(out Vector3 spot);
            public virtual void OnLost(Zombie zombie, bool killed) { }
        }

        private enum HordeMode { Dormant, Night, Manual }

        private sealed class Horde : Group
        {
            public readonly HordeConfig Cfg;
            public HordeMode Mode = HordeMode.Dormant;

            private readonly RZombiesReworked _plugin;
            private readonly ZombieProfile _profile;
            private readonly HordeState _state;
            private float _manualUntil;
            private float _nextRefill;
            private bool _resumeChecked;

            public Horde(RZombiesReworked plugin, HordeConfig cfg, ZombieProfile profile, HordeState state)
            {
                _plugin = plugin;
                Cfg = cfg;
                _profile = profile;
                _state = state;
            }

            public override string Label => Cfg.Name;
            public override ZombieProfile Profile => _profile;
            public override bool WantsMore => Mode != HordeMode.Dormant && Members.Count < Cfg.Count;
            public DateTime? LastRise => _state.LastRise;

            public void Evaluate(DateTime now)
            {
                float hour = (float)now.TimeOfDay.TotalHours;
                bool wraps = Cfg.RisesAt > Cfg.LeavesAt;
                bool open = !Mathf.Approximately(Cfg.RisesAt, Cfg.LeavesAt)
                            && (wraps ? hour >= Cfg.RisesAt || hour < Cfg.LeavesAt
                                      : hour >= Cfg.RisesAt && hour < Cfg.LeavesAt);

                if (Mode == HordeMode.Manual && Time.realtimeSinceStartup >= _manualUntil)
                    Leave();

                if (!open)
                {
                    _resumeChecked = true;
                    if (Mode == HordeMode.Night)
                        Leave();
                }
                else if (Mode != HordeMode.Night)
                {
                    // The calendar day this night's window opened on.
                    DateTime night = now.Date;
                    if (wraps && hour < Cfg.LeavesAt)
                        night = night.AddDays(-1);

                    if (!_resumeChecked)
                    {
                        // Plugin reloaded mid-window after this horde had already risen: bring it straight back.
                        _resumeChecked = true;
                        if (_state.LastRise == night)
                        {
                            Rise(HordeMode.Night);
                            return;
                        }
                    }

                    if (_state.LastRoll != night)
                    {
                        _state.LastRoll = night;
                        if (Rested(night) && Random.Range(0f, 100f) < Cfg.ChancePercent)
                        {
                            _state.LastRise = night;
                            Rise(HordeMode.Night);
                            return;
                        }
                    }
                }

                if (Mode != HordeMode.Dormant && Cfg.ReplaceKilled && Time.realtimeSinceStartup >= _nextRefill)
                    TopUp();
            }

            public void StartManual(float minutes)
            {
                if (Mode == HordeMode.Night)
                {
                    TopUp();
                    return;
                }

                _manualUntil = Time.realtimeSinceStartup + minutes * 60f;
                Rise(HordeMode.Manual);
            }

            public void Leave()
            {
                if (Mode == HordeMode.Dormant)
                    return;

                Mode = HordeMode.Dormant;
                _plugin.RetireAll(this);

                if (_plugin._cfg.Chat.AnnounceLeave)
                    _plugin.Announce("HordeLeaves", Cfg.Name);
            }

            public override bool TryPickSpot(out Vector3 spot)
            {
                BasePlayer anchor;
                if (Cfg.Placement == Placement.NearPlayers
                    && BasePlayer.activePlayerList.Count >= Cfg.MinOnlineForNearPlayers
                    && _plugin.TryPickPlayer(out anchor)
                    && _plugin.TryFindSpotAround(anchor.transform.position, Cfg.MinDistance, Cfg.MaxDistance, true, out spot))
                    return true;

                return _plugin.TryFindSpotAnywhere(out spot);
            }

            public override void OnLost(Zombie zombie, bool killed)
            {
                if (killed)
                    _nextRefill = Time.realtimeSinceStartup + Cfg.ReplacementDelay;
            }

            private void Rise(HordeMode mode)
            {
                bool wasDormant = Mode == HordeMode.Dormant;
                Mode = mode;
                int added = TopUp();

                if (wasDormant && added > 0 && _plugin._cfg.Chat.AnnounceRise)
                    _plugin.Announce("HordeRises", Cfg.Name, Cfg.Count);
            }

            private int TopUp()
            {
                int missing = Cfg.Count - Members.Count - Queued;
                _plugin.Enqueue(this, missing);
                return Math.Max(0, missing);
            }

            private bool Rested(DateTime night)
            {
                if (!_state.LastRise.HasValue)
                    return true;
                double gap = (night - _state.LastRise.Value).TotalDays;
                return gap < 0 || gap > Cfg.NightsBetween; // gap < 0: server clock was wound back
            }
        }

        private sealed class OutbreakSite : Group
        {
            public readonly string Name;
            public readonly Vector3 Center;
            public bool Active { get; private set; }

            private readonly RZombiesReworked _plugin;
            private readonly ZombieProfile _profile;
            private float _nextRefill;
            private float _emptySince = -1f;

            public OutbreakSite(RZombiesReworked plugin, string name, Vector3 center, ZombieProfile profile)
            {
                _plugin = plugin;
                Name = name;
                Center = center;
                _profile = profile;
            }

            private OutbreakConfig Cfg => _plugin._cfg.Outbreaks;

            public override string Label => Name;
            public override ZombieProfile Profile => _profile;
            public override bool WantsMore => Active && Members.Count < Cfg.PerMonument;

            // Monuments only fill up while a player is close, and empty out again once everyone has left.
            public void Evaluate(List<BasePlayer> players)
            {
                float now = Time.realtimeSinceStartup;
                float rangeSqr = Cfg.ActivationRange * Cfg.ActivationRange;
                bool someoneNear = Cfg.ActivationRange <= 0f;
                for (int i = 0; !someoneNear && i < players.Count; i++)
                    someoneNear = (players[i].transform.position - Center).sqrMagnitude <= rangeSqr;

                if (someoneNear)
                {
                    Active = true;
                    _emptySince = -1f;
                    if (now >= _nextRefill)
                        _plugin.Enqueue(this, Cfg.PerMonument - Members.Count - Queued);
                    return;
                }

                if (!Active)
                    return;

                if (_emptySince < 0f)
                    _emptySince = now;
                else if (now - _emptySince >= Cfg.EmptyGrace)
                {
                    Active = false;
                    _emptySince = -1f;
                    _plugin.RetireAll(this);
                }
            }

            public void Clear()
            {
                _plugin.RetireAll(this);
                _nextRefill = Time.realtimeSinceStartup + Cfg.RespawnDelay;
            }

            public override bool TryPickSpot(out Vector3 spot) =>
                _plugin.TryFindSpotAround(Center, 0f, Cfg.SpawnRadius, false, out spot);

            public override void OnLost(Zombie zombie, bool killed)
            {
                if (killed)
                    _nextRefill = Mathf.Max(_nextRefill, Time.realtimeSinceStartup + Cfg.RespawnDelay);
            }
        }

        // DayZ-style infected that follow each player around: spawned just out of sight, recycled once left behind.
        private sealed class AmbientGroup : Group
        {
            public readonly BasePlayer Owner;

            private readonly RZombiesReworked _plugin;
            private float _nextRefill;

            public AmbientGroup(RZombiesReworked plugin, BasePlayer owner)
            {
                _plugin = plugin;
                Owner = owner;
            }

            private AmbientConfig Cfg => _plugin._cfg.Ambient;
            private int Target => _plugin._isNight ? Cfg.PerPlayerNight : Cfg.PerPlayerDay;

            public override string Label => Owner != null ? Owner.displayName : "?";
            public override ZombieProfile Profile => _plugin._ambientProfile;
            public override bool WantsMore =>
                Owner != null && Owner.IsConnected && !Owner.IsDead() && Members.Count < Target;

            public void Evaluate()
            {
                if (Time.realtimeSinceStartup >= _nextRefill && _plugin.IsEligible(Owner))
                    _plugin.Enqueue(this, Target - Members.Count - Queued);
            }

            public void Clear()
            {
                _plugin.RetireAll(this);
                _nextRefill = Time.realtimeSinceStartup + Cfg.RespawnDelay;
            }

            public override bool TryPickSpot(out Vector3 spot) =>
                _plugin.TryFindSpotAround(Owner.transform.position, Cfg.MinDistance, Cfg.MaxDistance, true, out spot);

            public override void OnLost(Zombie zombie, bool killed)
            {
                if (killed)
                    _nextRefill = Mathf.Max(_nextRefill, Time.realtimeSinceStartup + Cfg.RespawnDelay);
            }
        }

        private void FindOutbreakSites()
        {
            if (TerrainMeta.Path == null || _cfg.Outbreaks.PerMonument <= 0)
                return;

            var wanted = new List<string>();
            foreach (string entry in _cfg.Outbreaks.Monuments)
            {
                if (!string.IsNullOrWhiteSpace(entry))
                    wanted.Add(entry.Trim().ToLowerInvariant().Replace(' ', '_'));
            }

            ZombieProfile profile = ResolveProfile(_cfg.Outbreaks.Profile);
            foreach (MonumentInfo monument in TerrainMeta.Path.Monuments)
            {
                if (monument == null)
                    continue;

                string prefab = monument.name.ToLowerInvariant();
                int slash = prefab.LastIndexOf('/');
                if (slash >= 0)
                    prefab = prefab.Substring(slash + 1);

                if (wanted.Exists(prefab.Contains))
                    _sites.Add(new OutbreakSite(this, prefab.Replace(".prefab", string.Empty), monument.transform.position, profile));
            }

            if (_sites.Count == 0)
                PrintWarning("Monument outbreaks are enabled but no monument on this map matched the list.");
            else
                Puts($"Monument outbreaks running at {_sites.Count} monument(s).");
        }

        #endregion

        #region Spawning

        private void Enqueue(Group group, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _spawnQueue.Enqueue(group);
                group.Queued++;
            }
        }

        private void ProcessQueues()
        {
            int budget = Mathf.Max(1, Mathf.RoundToInt(_cfg.Performance.SpawnsPerSecond * QueueTick));

            int spawns = budget;
            while (spawns > 0 && _spawnQueue.Count > 0)
            {
                Group group = _spawnQueue.Dequeue();
                group.Queued--;

                if (!group.WantsMore || _alive.Count >= _cfg.Performance.MaxZombies)
                    continue;

                spawns--;
                Vector3 spot;
                if (group.TryPickSpot(out spot))
                    SpawnInto(group, spot);
            }

            int removals = budget * 2;
            while (removals > 0 && _despawnQueue.Count > 0)
            {
                ScarecrowNPC npc = _despawnQueue.Dequeue();
                if (npc == null || npc.IsDestroyed)
                    continue;
                removals--;
                npc.AdminKill();
            }
        }

        private void SpawnInto(Group group, Vector3 spot)
        {
            var npc = GameManager.server.CreateEntity(ScarecrowPath, spot, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f)) as ScarecrowNPC;
            if (npc == null)
                return;

            ZombieProfile profile = group.Profile;

            npc.EnableSaving(false);
            npc.Spawn();
            npc.displayName = profile.Name;
            npc.InitializeHealth(profile.Health, profile.Health);

            BaseNavigator navigator = npc.GetComponent<BaseNavigator>();
            if (navigator != null)
                navigator.PlaceOnNavMesh(0f);

            EquipKit(npc, profile);
            if (!profile.CanThrowGrenades)
                StripItem(npc, BeancanItemId);
            ItemDefinition light = GiveNightLight(npc, profile.NightLight);
            if (profile.GlowingEyes)
                AddGlowingEyes(npc);

            npc.SendNetworkUpdate();

            var zombie = new Zombie { Npc = npc, Group = group, Profile = profile, Light = light };
            if (light != null && _isNight)
                SetLight(zombie, true);
            _alive[npc] = zombie;
            group.Members.Add(zombie);
        }

        private void EquipKit(ScarecrowNPC npc, ZombieProfile profile)
        {
            if (profile.Kits.Count == 0 || Kits == null || !Kits.IsLoaded)
                return;

            string kit = profile.Kits[Random.Range(0, profile.Kits.Count)];
            if (profile.ReplaceGear)
            {
                npc.inventory.Strip();
                ItemManager.DoRemoves();
            }

            object result = Kits.Call("GiveKit", npc, kit);
            if (result is string && _kitWarnings.Add(kit))
                PrintWarning($"Kits couldn't give '{kit}' to a zombie: {result}");
        }

        private void AddGlowingEyes(ScarecrowNPC npc)
        {
            if (_glowEyes == null || npc.inventory.containerWear.FindItemByItemID(_glowEyes.itemid) != null)
                return;

            Item eyes = ItemManager.Create(_glowEyes);
            if (eyes.MoveToContainer(npc.inventory.containerWear))
                return;

            eyes.Remove();
            if (!_glowEyesWarned)
            {
                _glowEyesWarned = true;
                PrintWarning("Couldn't fit Glowing Eyes on a zombie (its headwear blocks that slot). " +
                             "Try a kit without a full-face mask.");
            }
        }

        private ItemDefinition GiveNightLight(ScarecrowNPC npc, NightLight kind)
        {
            if (kind == NightLight.None)
                return null;

            string shortname = kind == NightLight.CandleHat ? CandleHatShortname
                             : kind == NightLight.Flashlight ? FlashlightShortname
                             : TorchShortname;
            ItemDefinition def = ItemManager.FindItemDefinition(shortname);

            bool given = def != null && (kind == NightLight.CandleHat ? PutOnCandleHat(npc, def) : PutInHand(npc, def));
            if (given)
                return def;

            if (_lightWarnings.Add(kind))
                PrintWarning(def == null
                    ? $"Item '{shortname}' doesn't exist on this server - zombies won't get a {kind}."
                    : $"Couldn't give a zombie a {kind} - its outfit or inventory blocked it.");
            return null;
        }

        private static bool PutOnCandleHat(ScarecrowNPC npc, ItemDefinition def)
        {
            ItemContainer wear = npc.inventory.containerWear;
            if (wear.FindItemByItemID(def.itemid) != null)
                return true;

            Item hat = ItemManager.Create(def);
            if (hat.MoveToContainer(wear))
                return true;

            // Something on the head is in the way (usually the scarecrow's own mask): find it and swap it for the hat.
            var worn = Pool.Get<List<Item>>();
            worn.AddRange(wear.itemList);
            bool fitted = false;
            foreach (Item item in worn)
            {
                int slot = item.position;
                item.RemoveFromContainer();
                if (hat.MoveToContainer(wear))
                {
                    item.Remove();
                    fitted = true;
                    break;
                }
                if (!item.MoveToContainer(wear, slot))
                    item.Remove();
            }
            Pool.FreeUnmanaged(ref worn);
            ItemManager.DoRemoves();

            if (!fitted)
                hat.Remove();
            return fitted;
        }

        // Flashlight or torch: empty the hotbar so the zombie has nothing else to switch to, then hold the light.
        private static bool PutInHand(ScarecrowNPC npc, ItemDefinition def)
        {
            ItemContainer belt = npc.inventory.containerBelt;
            for (int i = belt.itemList.Count - 1; i >= 0; i--)
            {
                Item item = belt.itemList[i];
                item.RemoveFromContainer();
                item.Remove();
            }
            ItemManager.DoRemoves();

            Item light = ItemManager.Create(def);
            if (!light.MoveToContainer(belt, 0))
            {
                light.Remove();
                return false;
            }

            npc.UpdateActiveItem(light.uid);
            return true;
        }

        // Uses the same toggle a player gets from their light key, which covers worn and held lights.
        private static void SetLight(Zombie zombie, bool on)
        {
            if (zombie.Light == null || zombie.LightOn == on || zombie.Npc == null || zombie.Npc.IsDestroyed)
                return;
            zombie.Npc.LightToggle();
            zombie.LightOn = on;
        }

        // Lights come on at dusk and go off at dawn.
        private void RefreshLights()
        {
            foreach (Zombie zombie in _alive.Values)
                SetLight(zombie, _isNight);
        }

        private static void StripWorn(ScarecrowNPC npc, int itemId)
        {
            Item item = npc.inventory.containerWear.FindItemByItemID(itemId)
                        ?? npc.inventory.containerBelt.FindItemByItemID(itemId);
            if (item == null)
                return;
            item.RemoveFromContainer();
            item.Remove();
            ItemManager.DoRemoves();
        }

        private static void StripItem(ScarecrowNPC npc, int itemId)
        {
            bool removed = false;
            foreach (ItemContainer container in new[] { npc.inventory.containerBelt, npc.inventory.containerMain })
            {
                if (container == null)
                    continue;

                for (int i = container.itemList.Count - 1; i >= 0; i--)
                {
                    Item item = container.itemList[i];
                    if (item.info.itemid != itemId)
                        continue;
                    item.RemoveFromContainer();
                    item.Remove();
                    removed = true;
                }
            }

            if (removed)
                ItemManager.DoRemoves();
        }

        // Unlink a zombie from tracking without touching the entity.
        private void Detach(Zombie zombie)
        {
            _alive.Remove(zombie.Npc);
            zombie.Group.Members.Remove(zombie);
        }

        // Unlink every member of a group and remove them gradually through the despawn queue.
        private void RetireAll(Group group)
        {
            var members = Pool.Get<List<Zombie>>();
            members.AddRange(group.Members);
            foreach (Zombie zombie in members)
            {
                Detach(zombie);
                _despawnQueue.Enqueue(zombie.Npc);
            }
            Pool.FreeUnmanaged(ref members);
        }

        #endregion

        #region Placement

        private bool TryPickPlayer(out BasePlayer chosen)
        {
            var candidates = Pool.Get<List<BasePlayer>>();
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (IsEligible(player))
                    candidates.Add(player);
            }

            chosen = candidates.Count == 0 ? null : candidates[Random.Range(0, candidates.Count)];
            Pool.FreeUnmanaged(ref candidates);
            return chosen != null;
        }

        // Players zombies can be spawned around or activate monuments: alive, awake, not noclipping,
        // not in a safe zone, not immune and not vanished.
        private bool IsEligible(BasePlayer player)
        {
            if (player == null || !player.IsConnected || player.IsDead() || player.IsSleeping() || player.IsFlying || player.InSafeZone())
                return false;
            if (permission.UserHasPermission(player.UserIDString, PermImmune))
                return false;
            return !(Vanish != null && Vanish.Call("IsInvisible", player) is true);
        }

        private bool TryFindSpotAround(Vector3 center, float minDistance, float maxDistance, bool avoidBuildings, out Vector3 spot)
        {
            maxDistance = Mathf.Max(minDistance + 1f, maxDistance);
            for (int attempt = 0; attempt < 12; attempt++)
            {
                float angle = Random.Range(0f, Mathf.PI * 2f);
                float distance = Random.Range(minDistance, maxDistance);
                spot = new Vector3(center.x + Mathf.Cos(angle) * distance, 0f, center.z + Mathf.Sin(angle) * distance);
                if (Settle(ref spot) && IsUsable(spot, avoidBuildings))
                    return true;
            }

            spot = Vector3.zero;
            return false;
        }

        private bool TryFindSpotAnywhere(out Vector3 spot)
        {
            Vector3 corner = TerrainMeta.Position;
            Vector3 size = TerrainMeta.Size;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                spot = new Vector3(corner.x + Random.value * size.x, 0f, corner.z + Random.value * size.z);
                if (Settle(ref spot) && IsUsable(spot, true))
                    return true;
            }

            spot = Vector3.zero;
            return false;
        }

        // Drop a point onto the ground, preferring the AI navmesh so zombies can actually walk from it.
        private static bool Settle(ref Vector3 spot)
        {
            if (TerrainMeta.HeightMap == null)
                return false;

            spot.y = TerrainMeta.HeightMap.GetHeight(spot);

            NavMeshHit hit;
            if (NavMesh.SamplePosition(spot, out hit, 6f, NavMesh.AllAreas))
                spot = hit.position;
            return true;
        }

        private bool IsUsable(Vector3 spot, bool avoidBuildings)
        {
            if (AntiHack.TestInsideTerrain(spot + Vector3.up * 0.5f))
                return false;
            if (Physics.CheckSphere(spot + Vector3.up * 1.1f, 0.45f, _obstacleMask, QueryTriggerInteraction.Ignore))
                return false;
            if (WaterLevel.GetWaterDepth(spot, true, true) > 0.4f)
                return false;
            if (avoidBuildings && _cfg.Performance.BuildingClearance > 0f
                && Physics.CheckSphere(spot, _cfg.Performance.BuildingClearance, _buildingMask, QueryTriggerInteraction.Ignore))
                return false;
            return true;
        }

        #endregion

        #region Timers

        private void Schedule()
        {
            if (TOD_Sky.Instance == null)
                return;

            DateTime now = TOD_Sky.Instance.Cycle.DateTime;
            float hour = (float)now.TimeOfDay.TotalHours;
            float nightStart = _cfg.Ambient.NightStarts, nightEnd = _cfg.Ambient.NightEnds;
            bool wasNight = _isNight;
            _isNight = nightStart > nightEnd ? hour >= nightStart || hour < nightEnd : hour >= nightStart && hour < nightEnd;
            if (_isNight != wasNight)
                RefreshLights();

            foreach (Horde horde in _hordes)
                horde.Evaluate(now);

            var players = Pool.Get<List<BasePlayer>>();
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (IsEligible(player))
                    players.Add(player);
            }

            foreach (OutbreakSite site in _sites)
                site.Evaluate(players);

            if (_cfg.Ambient.Enabled)
            {
                foreach (BasePlayer player in BasePlayer.activePlayerList)
                {
                    if (player != null && ((ulong)player.userID).IsSteamId() && !_ambient.ContainsKey(player.userID))
                        _ambient[player.userID] = new AmbientGroup(this, player);
                }
                foreach (AmbientGroup group in _ambient.Values)
                    group.Evaluate();
            }

            Pool.FreeUnmanaged(ref players);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            AmbientGroup group;
            if (player == null || !_ambient.TryGetValue(player.userID, out group))
                return;
            RetireAll(group);
            _ambient.Remove(player.userID);
        }

        private void Watchdog()
        {
            if (_alive.Count == 0)
                return;

            float drownAfter = _cfg.Cleanup.UnderwaterSeconds;
            float leash = _cfg.Outbreaks.LeashDistance;
            float recycle = _cfg.Ambient.RecycleDistance;
            var lost = Pool.Get<List<Zombie>>();

            foreach (Zombie zombie in _alive.Values)
            {
                ScarecrowNPC npc = zombie.Npc;
                if (npc == null || npc.IsDestroyed)
                {
                    lost.Add(zombie);
                    continue;
                }

                Vector3 position = npc.transform.position;

                if (drownAfter > 0f)
                {
                    if (WaterLevel.GetWaterDepth(position, true, true) >= _cfg.Cleanup.UnderwaterDepth)
                    {
                        zombie.SecondsUnderwater += WatchdogTick;
                        if (zombie.SecondsUnderwater >= drownAfter)
                        {
                            lost.Add(zombie);
                            continue;
                        }
                    }
                    else
                    {
                        zombie.SecondsUnderwater = 0f;
                    }
                }

                var site = zombie.Group as OutbreakSite;
                if (site != null && leash > 0f && (position - site.Center).sqrMagnitude > leash * leash)
                {
                    lost.Add(zombie);
                    continue;
                }

                var ambient = zombie.Group as AmbientGroup;
                if (ambient != null && (ambient.Owner == null || ambient.Owner.IsDead()
                    || (position - ambient.Owner.transform.position).sqrMagnitude > recycle * recycle))
                    lost.Add(zombie);
            }

            foreach (Zombie zombie in lost)
            {
                Detach(zombie);
                zombie.Group.OnLost(zombie, false);
                if (zombie.Npc != null && !zombie.Npc.IsDestroyed)
                    zombie.Npc.AdminKill();
            }
            Pool.FreeUnmanaged(ref lost);
        }

        #endregion

        #region Hooks

        private object OnNpcTarget(ScarecrowNPC npc, BaseEntity target)
        {
            if (npc == null || target == null || !_alive.ContainsKey(npc))
                return null;
            return IsOffLimits(target) ? (object)true : null;
        }

        // Subscribed only when outpost sentries should leave zombies alone.
        private object OnTurretTarget(NPCAutoTurret turret, ScarecrowNPC npc) =>
            npc != null && _alive.ContainsKey(npc) ? (object)true : null;

        // Subscribed only when a profile changes zombie damage.
        private object OnEntityTakeDamage(BaseCombatEntity victim, HitInfo info)
        {
            var npc = info?.Initiator as ScarecrowNPC;
            Zombie zombie;
            if (npc != null && _alive.TryGetValue(npc, out zombie) && !Mathf.Approximately(zombie.Profile.DamageMultiplier, 1f))
                info.damageTypes.ScaleAll(zombie.Profile.DamageMultiplier);
            return null;
        }

        private void OnPlayerDeath(ScarecrowNPC npc, HitInfo info)
        {
            Zombie zombie;
            if (npc == null || !_alive.TryGetValue(npc, out zombie))
                return;

            Detach(zombie);

            // Don't let corpses hand out free Glowing Eyes and candle hats.
            if (zombie.Profile.GlowingEyes && _glowEyes != null)
                StripWorn(npc, _glowEyes.itemid);
            if (zombie.Light != null)
                StripWorn(npc, zombie.Light.itemid);

            if (_fallen.Count >= FallenMemory)
                _fallen.Clear();
            _fallen[npc.userID] = zombie.Profile.Name;

            if (!string.IsNullOrEmpty(_cfg.Corpses.DeathEffect))
                Effect.server.Run(_cfg.Corpses.DeathEffect, npc.transform.position);

            zombie.Group.OnLost(zombie, true);
        }

        private void OnEntityKill(ScarecrowNPC npc)
        {
            Zombie zombie;
            if (npc == null || !_alive.TryGetValue(npc, out zombie))
                return;

            Detach(zombie);
            zombie.Group.OnLost(zombie, false);
        }

        // Subscribed only when zombie corpses should be empty. Returning the corpse skips loot generation.
        private object OnCorpsePopulate(ScarecrowNPC npc, NPCPlayerCorpse corpse) =>
            npc != null && _fallen.ContainsKey(npc.userID) ? corpse : null;

        private void OnEntitySpawned(NPCPlayerCorpse corpse)
        {
            if (corpse == null || _fallen.Count == 0)
                return;

            NextTick(() =>
            {
                string name;
                if (corpse == null || corpse.IsDestroyed || !_fallen.TryGetValue(corpse.playerSteamID, out name))
                    return;

                if (!_cfg.Corpses.LeaveCorpses)
                {
                    _fallen.Remove(corpse.playerSteamID);
                    corpse.Kill();
                    return;
                }

                corpse.playerName = name;
                corpse.SendNetworkUpdate();
            });
        }

        private void OnEntitySpawned(DroppedItemContainer backpack)
        {
            float multiplier = _cfg.Corpses.BackpackLifetimeMultiplier;
            if (backpack == null || _fallen.Count == 0 || Mathf.Approximately(multiplier, 1f))
                return;

            NextTick(() =>
            {
                if (backpack == null || backpack.IsDestroyed || !_fallen.Remove(backpack.playerSteamID))
                    return;

                backpack.CancelInvoke(backpack.RemoveMe);
                backpack.Invoke(backpack.RemoveMe, Mathf.Max(5f, backpack.CalculateRemovalTime() * multiplier));
            });
        }

        private bool IsOffLimits(BaseEntity target)
        {
            if (_neverTarget.Count > 0 && _neverTarget.Contains(target.ShortPrefabName))
                return true;

            var player = target as BasePlayer;
            if (player == null)
                return false;

            // userID is an EncryptedValue<ulong> on current Rust, so cast before using ulong helpers.
            if (((ulong)player.userID).IsSteamId())
            {
                if (!_cfg.Targeting.AttackSleepers && player.IsSleeping())
                    return true;
                return permission.UserHasPermission(player.UserIDString, PermImmune);
            }

            // Non-Steam players that aren't vanilla NPCs are plugin bots (HumanNPC and similar).
            return !(player is NPCPlayer) && !_cfg.Targeting.AttackBots;
        }

        #endregion

        #region Command

        private void RzCommand(IPlayer caller, string command, string[] args)
        {
            if (!caller.IsServer && !caller.HasPermission(PermAdmin))
            {
                Reply(caller, "NoPermission");
                return;
            }

            string action = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            string target = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : "all";

            switch (action)
            {
                case "status":
                    caller.Reply(BuildStatus(caller));
                    return;

                case "start":
                {
                    List<Horde> matched = MatchHordes(caller, target);
                    foreach (Horde horde in matched)
                        horde.StartManual(_cfg.ManualRiseMinutes);
                    if (matched.Count > 0)
                        Reply(caller, "Started", matched.Count, _cfg.ManualRiseMinutes);
                    return;
                }

                case "stop":
                {
                    List<Horde> matched = MatchHordes(caller, target);
                    foreach (Horde horde in matched)
                        horde.Leave();
                    if (matched.Count > 0)
                        Reply(caller, "Stopped", matched.Count);
                    return;
                }

                case "clear":
                    foreach (Horde horde in _hordes)
                        horde.Leave();
                    foreach (OutbreakSite site in _sites)
                        site.Clear();
                    foreach (AmbientGroup group in _ambient.Values)
                        group.Clear();
                    Reply(caller, "Cleared", Mathf.Min(_cfg.Ambient.RespawnDelay, _cfg.Outbreaks.RespawnDelay));
                    return;

                default:
                    Reply(caller, "Help");
                    return;
            }
        }

        private List<Horde> MatchHordes(IPlayer caller, string name)
        {
            List<Horde> matched = name.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? new List<Horde>(_hordes)
                : _hordes.FindAll(h => h.Cfg.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (matched.Count == 0)
                Reply(caller, "NoSuchHorde", name, string.Join(", ", _hordes.ConvertAll(h => h.Cfg.Name)));
            return matched;
        }

        private string BuildStatus(IPlayer caller)
        {
            var sb = new StringBuilder();
            float hour = TOD_Sky.Instance != null ? (float)TOD_Sky.Instance.Cycle.DateTime.TimeOfDay.TotalHours : 0f;
            sb.AppendLine(Msg("StatusHeader", caller, hour.ToString("00.00"), _alive.Count, _cfg.Performance.MaxZombies, _spawnQueue.Count));

            foreach (Horde horde in _hordes)
            {
                string last = horde.LastRise.HasValue ? horde.LastRise.Value.ToString("yyyy-MM-dd") : "never";
                sb.AppendLine(Msg("StatusHorde", caller, horde.Cfg.Name, horde.Mode, horde.Members.Count, horde.Cfg.Count,
                    horde.Cfg.RisesAt.ToString("0.0"), horde.Cfg.LeavesAt.ToString("0.0"), last));
            }

            int ambientZombies = 0;
            foreach (AmbientGroup group in _ambient.Values)
                ambientZombies += group.Members.Count;
            sb.AppendLine(Msg("StatusAmbient", caller, _ambient.Count, ambientZombies, _isNight ? "night" : "day",
                _isNight ? _cfg.Ambient.PerPlayerNight : _cfg.Ambient.PerPlayerDay));

            int outbreakZombies = 0, activeSites = 0;
            foreach (OutbreakSite site in _sites)
            {
                outbreakZombies += site.Members.Count;
                if (site.Active)
                    activeSites++;
            }
            sb.Append(Msg("StatusOutbreaks", caller, _sites.Count, activeSites, outbreakZombies));
            return sb.ToString();
        }

        #endregion

        #region Helpers

        private ZombieProfile ResolveProfile(string key)
        {
            ZombieProfile profile;
            if (!string.IsNullOrEmpty(key) && _cfg.Profiles.TryGetValue(key, out profile) && profile != null)
                return profile;

            PrintWarning($"Zombie profile '{key}' doesn't exist - using built-in defaults.");
            return new ZombieProfile();
        }

        private bool AnyDamageScaling()
        {
            foreach (ZombieProfile profile in _cfg.Profiles.Values)
            {
                if (profile != null && !Mathf.Approximately(profile.DamageMultiplier, 1f))
                    return true;
            }
            return false;
        }

        private bool UsesKits()
        {
            foreach (ZombieProfile profile in _cfg.Profiles.Values)
            {
                if (profile != null && profile.Kits.Count > 0)
                    return true;
            }
            return false;
        }

        #endregion

        #region Data

        private sealed class StoredData
        {
            public Dictionary<string, HordeState> Hordes = new Dictionary<string, HordeState>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class HordeState
        {
            public DateTime? LastRoll;
            public DateTime? LastRise;
        }

        private StoredData LoadData()
        {
            try
            {
                StoredData data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
                if (data?.Hordes != null)
                {
                    data.Hordes = new Dictionary<string, HordeState>(data.Hordes, StringComparer.OrdinalIgnoreCase);
                    return data;
                }
            }
            catch (Exception ex)
            {
                PrintWarning($"Couldn't read data file, horde history reset: {ex.Message}");
            }
            return new StoredData();
        }

        private void SaveData()
        {
            if (_data != null)
                Interface.Oxide.DataFileSystem.WriteObject(Name, _data);
        }

        #endregion

        #region Config

        private enum Placement { NearPlayers, Anywhere }

        private enum NightLight { CandleHat, Flashlight, Torch, None }

        private sealed class ZombieProfile
        {
            [JsonProperty("Name shown")]
            public string Name = "Zombie";

            [JsonProperty("Health")]
            public float Health = 75f;

            [JsonProperty("Damage multiplier")]
            public float DamageMultiplier = 1f;

            [JsonProperty("Can throw grenades")]
            public bool CanThrowGrenades = true;

            [JsonProperty("Kits (one picked at random, needs the Kits plugin)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Kits = new List<string>();

            [JsonProperty("Kit replaces default gear")]
            public bool ReplaceGear = false;

            [JsonProperty("Glowing eyes (wear the Glowing Eyes item)")]
            public bool GlowingEyes = true;

            [JsonProperty("Night light (CandleHat, Flashlight, Torch or None)")]
            [JsonConverter(typeof(StringEnumConverter))]
            public NightLight NightLight = NightLight.CandleHat;
        }

        private sealed class HordeConfig
        {
            [JsonProperty("Name")]
            public string Name = "Nightfall";

            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Profile")]
            public string Profile = "walker";

            [JsonProperty("Rises at (hour 0-24)")]
            public float RisesAt = 20f;

            [JsonProperty("Leaves at (hour 0-24)")]
            public float LeavesAt = 6.5f;

            [JsonProperty("Chance to rise each night (%)")]
            public float ChancePercent = 35f;

            [JsonProperty("Nights to skip between rises")]
            public int NightsBetween = 0;

            [JsonProperty("Zombie count")]
            public int Count = 40;

            [JsonProperty("Replace killed zombies")]
            public bool ReplaceKilled = true;

            [JsonProperty("Replacement delay (seconds)")]
            public float ReplacementDelay = 10f;

            [JsonProperty("Placement (NearPlayers or Anywhere)")]
            [JsonConverter(typeof(StringEnumConverter))]
            public Placement Placement = Placement.NearPlayers;

            [JsonProperty("Players online needed for NearPlayers")]
            public int MinOnlineForNearPlayers = 1;

            [JsonProperty("Min distance from player")]
            public float MinDistance = 30f;

            [JsonProperty("Max distance from player")]
            public float MaxDistance = 70f;
        }

        private sealed class AmbientConfig
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Profile")]
            public string Profile = "walker";

            [JsonProperty("Zombies around each player (day)")]
            public int PerPlayerDay = 10;

            [JsonProperty("Zombies around each player (night)")]
            public int PerPlayerNight = 20;

            [JsonProperty("Night starts (hour 0-24)")]
            public float NightStarts = 19.5f;

            [JsonProperty("Night ends (hour 0-24)")]
            public float NightEnds = 6.5f;

            [JsonProperty("Min spawn distance from player")]
            public float MinDistance = 40f;

            [JsonProperty("Max spawn distance from player")]
            public float MaxDistance = 100f;

            [JsonProperty("Recycle zombies further than this from their player")]
            public float RecycleDistance = 160f;

            [JsonProperty("Respawn delay after kill (seconds)")]
            public float RespawnDelay = 30f;
        }

        private sealed class OutbreakConfig
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Profile")]
            public string Profile = "brute";

            [JsonProperty("Zombies per monument")]
            public int PerMonument = 12;

            [JsonProperty("Spawn radius")]
            public float SpawnRadius = 45f;

            [JsonProperty("Only active when a player is within (0 = always)")]
            public float ActivationRange = 300f;

            [JsonProperty("Empty out after nobody is near for (seconds)")]
            public float EmptyGrace = 90f;

            [JsonProperty("Leash distance (0 = off)")]
            public float LeashDistance = 120f;

            [JsonProperty("Respawn delay after kill (seconds)")]
            public float RespawnDelay = 90f;

            [JsonProperty("Monuments (prefab name contains)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Monuments = new List<string>
            {
                "airfield", "power_plant", "powerplant", "trainyard", "military_tunnel", "water_treatment",
                "launch_site", "junkyard", "satellite_dish", "sphere_tank", "harbor", "radtown", "excavator"
            };
        }

        private sealed class TargetingConfig
        {
            [JsonProperty("Attack sleeping players")]
            public bool AttackSleepers = false;

            [JsonProperty("Attack non-Steam bot players")]
            public bool AttackBots = false;

            [JsonProperty("Outpost sentries shoot zombies")]
            public bool SentriesShootZombies = true;

            [JsonProperty("Never target (prefab short names)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> NeverTarget = new List<string> { "scarecrow", "scientistjunkpile" };
        }

        private sealed class CorpseConfig
        {
            [JsonProperty("Leave corpses")]
            public bool LeaveCorpses = true;

            [JsonProperty("Corpses drop loot")]
            public bool DropLoot = true;

            [JsonProperty("Backpack lifetime multiplier")]
            public float BackpackLifetimeMultiplier = 0.5f;

            [JsonProperty("Death effect prefab (empty = none)")]
            public string DeathEffect = "assets/prefabs/npc/murderer/sound/death.prefab";
        }

        private sealed class CleanupConfig
        {
            [JsonProperty("Remove zombies underwater for (seconds, 0 = off)")]
            public float UnderwaterSeconds = 10f;

            [JsonProperty("Water depth that counts as underwater")]
            public float UnderwaterDepth = 1.6f;
        }

        private sealed class PerformanceConfig
        {
            [JsonProperty("Spawns per second")]
            public float SpawnsPerSecond = 10f;

            [JsonProperty("Max zombies on the server")]
            public int MaxZombies = 300;

            [JsonProperty("Keep spawns this far from bases (0 = off)")]
            public float BuildingClearance = 15f;
        }

        private sealed class ChatConfig
        {
            [JsonProperty("Announce when a horde rises")]
            public bool AnnounceRise = true;

            [JsonProperty("Announce when a horde leaves")]
            public bool AnnounceLeave = false;
        }

        private sealed class PluginConfig
        {
            [JsonProperty("Zombie profiles", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, ZombieProfile> Profiles = new Dictionary<string, ZombieProfile>(StringComparer.OrdinalIgnoreCase);

            [JsonProperty("Roaming zombies (per player)")]
            public AmbientConfig Ambient = new AmbientConfig();

            [JsonProperty("Hordes", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<HordeConfig> Hordes = new List<HordeConfig>();

            [JsonProperty("Monument outbreaks")]
            public OutbreakConfig Outbreaks = new OutbreakConfig();

            [JsonProperty("Targeting")]
            public TargetingConfig Targeting = new TargetingConfig();

            [JsonProperty("Corpses")]
            public CorpseConfig Corpses = new CorpseConfig();

            [JsonProperty("Cleanup")]
            public CleanupConfig Cleanup = new CleanupConfig();

            [JsonProperty("Performance")]
            public PerformanceConfig Performance = new PerformanceConfig();

            [JsonProperty("Chat")]
            public ChatConfig Chat = new ChatConfig();

            [JsonProperty("Manual rise length (minutes)")]
            public float ManualRiseMinutes = 10f;

            public static PluginConfig CreateDefault()
            {
                var cfg = new PluginConfig();
                cfg.Profiles["walker"] = new ZombieProfile { Name = "Walker", Health = 75f };
                cfg.Profiles["brute"] = new ZombieProfile { Name = "Brute", Health = 150f, DamageMultiplier = 1.25f, CanThrowGrenades = false };
                cfg.Hordes.Add(new HordeConfig());
                return cfg;
            }

            public void Repair()
            {
                Profiles = Profiles == null
                    ? new Dictionary<string, ZombieProfile>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, ZombieProfile>(Profiles, StringComparer.OrdinalIgnoreCase);
                Hordes = Hordes ?? new List<HordeConfig>();
                Hordes.RemoveAll(h => h == null);
                Ambient = Ambient ?? new AmbientConfig();
                Outbreaks = Outbreaks ?? new OutbreakConfig();
                Targeting = Targeting ?? new TargetingConfig();
                Corpses = Corpses ?? new CorpseConfig();
                Cleanup = Cleanup ?? new CleanupConfig();
                Performance = Performance ?? new PerformanceConfig();
                Chat = Chat ?? new ChatConfig();

                foreach (ZombieProfile profile in Profiles.Values)
                {
                    if (profile == null)
                        continue;
                    profile.Kits = profile.Kits ?? new List<string>();
                    profile.Health = Mathf.Max(1f, profile.Health);
                    profile.DamageMultiplier = Mathf.Max(0f, profile.DamageMultiplier);
                }

                foreach (HordeConfig horde in Hordes)
                {
                    horde.Count = Mathf.Max(0, horde.Count);
                    horde.RisesAt = Mathf.Repeat(horde.RisesAt, 24f);
                    horde.LeavesAt = Mathf.Repeat(horde.LeavesAt, 24f);
                }

                Outbreaks.Monuments = Outbreaks.Monuments ?? new List<string>();
                Targeting.NeverTarget = Targeting.NeverTarget ?? new List<string>();
                Performance.SpawnsPerSecond = Mathf.Max(1f, Performance.SpawnsPerSecond);
                ManualRiseMinutes = Mathf.Max(1f, ManualRiseMinutes);
                Ambient.PerPlayerDay = Mathf.Max(0, Ambient.PerPlayerDay);
                Ambient.PerPlayerNight = Mathf.Max(0, Ambient.PerPlayerNight);
                Ambient.MaxDistance = Mathf.Max(Ambient.MinDistance + 5f, Ambient.MaxDistance);
                Ambient.RecycleDistance = Mathf.Max(Ambient.MaxDistance + 20f, Ambient.RecycleDistance);
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _cfg = Config.ReadObject<PluginConfig>();
                if (_cfg == null)
                    throw new JsonException("config file is empty");
                _cfg.Repair();
                SaveConfig();
            }
            catch (Exception ex)
            {
                PrintError($"Config is invalid, running on defaults until it's fixed (your file was left alone): {ex.Message}");
                LoadDefaultConfig();
            }
        }

        protected override void LoadDefaultConfig() => _cfg = PluginConfig.CreateDefault();

        protected override void SaveConfig() => Config.WriteObject(_cfg);

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Prefix"] = "<color=#8fbf3f>[RZombies]</color> ",
                ["HordeRises"] = "A horde is rising tonight ({0}) - {1} more of them are out there.",
                ["HordeLeaves"] = "The {0} horde has retreated.",
                ["NoPermission"] = "You don't have permission to use this command.",
                ["NoSuchHorde"] = "No horde called '{0}'. Hordes: {1}",
                ["Started"] = "Started {0} horde(s) for {1} minutes.",
                ["Stopped"] = "Sent {0} horde(s) away.",
                ["Cleared"] = "All zombies removed. Monument outbreaks return in {0} seconds.",
                ["Help"] = "Usage: rz status | rz start [horde|all] | rz stop [horde|all] | rz clear",
                ["StatusHeader"] = "Time {0} | {1}/{2} zombies alive | {3} queued",
                ["StatusHorde"] = "  {0}: {1}, {2}/{3}, window {4}-{5}, last rose {6}",
                ["StatusAmbient"] = "  Roaming: {0} players, {1} zombies ({2}, {3} per player)",
                ["StatusOutbreaks"] = "  Outbreaks: {0} monuments ({1} active), {2} zombies"
            }, this);
        }

        private string Msg(string key, IPlayer player, params object[] args)
        {
            string text = lang.GetMessage(key, this, player == null || player.IsServer ? null : player.Id);
            return args.Length > 0 ? string.Format(text, args) : text;
        }

        private void Reply(IPlayer player, string key, params object[] args) =>
            player.Reply(Msg("Prefix", player) + Msg(key, player, args));

        private void Announce(string key, params object[] args)
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                string prefix = lang.GetMessage("Prefix", this, player.UserIDString);
                player.ChatMessage(prefix + string.Format(lang.GetMessage(key, this, player.UserIDString), args));
            }
        }

        #endregion
    }
}
