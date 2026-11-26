using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SwishhyysFunToys", "Swishhyy", "0.3.0")]
    [Description("Fun toys: jet airstrikes, mass airdrops, meteor showers and lightning storms.")]
    public class SwishhyysFunToys : RustPlugin
    {
        private const string PermUse = "swishhyysfuntoys.use";
        private const string PermAdmin = "swishhyysfuntoys.admin";

        // Default prefab paths. Each is checked against the game's prefab list at startup;
        // a missing one is replaced by a keyword search, and the console says what was used.
        private const string PrefabJet = "assets/scripts/entity/misc/f15/f15e.prefab";
        private const string PrefabCargoPlane = "assets/prefabs/npc/cargo plane/cargo_plane.prefab";
        private const string PrefabBomb = "assets/content/vehicles/mlrs/rocket_mlrs.prefab";
        private const string PrefabMeteor = "assets/prefabs/ammo/rocket/rocket_fire.prefab";
        private const string PrefabRock = "assets/bundled/prefabs/autospawn/resource/ores/stone-ore.prefab";
        private const string PrefabSupplyDrop = "assets/prefabs/misc/supply drop/supply_drop.prefab";
        private const string PrefabFireBall = "assets/bundled/prefabs/oilfireballsmall.prefab";
        private const string FxBigExplosion = "assets/prefabs/npc/patrol helicopter/effects/heli_explosion.prefab";
        private const string FxExplosion = "assets/bundled/prefabs/fx/explosions/explosion_01.prefab";   // last-resort fallback

        // Old defaults, migrated when found in a saved config.
        private const string FxC4 = "assets/prefabs/tools/c4/effects/c4_explosion.prefab";
        private const string FxRocket = "assets/prefabs/weapons/rocketlauncher/effects/rocket_explosion.prefab";

        private const float DropHeight = 100f;
        private const float AirdropSpacing = 0.4f;
        private const float JetAltitude = 150f;
        private const float JetRunIn = 800f;
        private const float BombHeight = 120f;
        private const float BombSpeed = 60f;
        private const float MeteorHeight = 150f;
        private const float MeteorSlant = 70f;          // horizontal distance meteors travel while falling
        private const float ProjectileTimeout = 30f;    // safety cleanup if a projectile never lands

        private Configuration config;
        private readonly Dictionary<ulong, float> lastUsed = new Dictionary<ulong, float>();

        // Resolved at startup.
        private List<string> airstrikeFx = new List<string> { FxExplosion };
        private List<string> meteorFx = new List<string>();
        private List<string> lightningFx = new List<string> { FxExplosion };
        private string jetPrefab;
        private string bombPrefab;
        private string meteorPrefab;
        private string rockPrefab;
        private bool fireOk;

        // Projectiles in flight -> what happens where they land.
        private readonly Dictionary<BaseEntity, Impact> projectiles = new Dictionary<BaseEntity, Impact>();

        private sealed class Impact
        {
            public float Radius;
            public float Damage;
            public BasePlayer Caller;
            public List<string> Effects;
            public bool Fire;
            public BaseEntity Rock;
        }

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Cooldown per player (seconds, admins skip it)")]
            public float CooldownSeconds = 300f;

            [JsonProperty("Max aim distance for toys (metres)")]
            public float MaxRange = 500f;

            [JsonProperty("Airstrike: seconds from call to bomb impact")]
            public float AirstrikeImpactDelay = 8f;

            [JsonProperty("Airstrike: blast radius (metres)")]
            public float AirstrikeRadius = 15f;

            [JsonProperty("Airstrike: damage at the centre")]
            public float AirstrikeDamage = 500f;

            [JsonProperty("Airstrike: jet prefab (falls back to the cargo plane if missing)")]
            public string AirstrikeJetPrefab = PrefabJet;

            [JsonProperty("Airstrike: bomb projectile prefab")]
            public string AirstrikeBombPrefab = PrefabBomb;

            [JsonProperty("Airstrike: explosion effects (all play together)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> AirstrikeEffects = new List<string> { FxBigExplosion };

            [JsonProperty("Mass airdrop: default crate count")]
            public int DefaultCrates = 50;

            [JsonProperty("Mass airdrop: maximum crate count")]
            public int MaxCrates = 200;

            [JsonProperty("Mass airdrop: zone radius (metres)")]
            public float AirdropRadius = 80f;

            [JsonProperty("Meteor shower: default count")]
            public int DefaultMeteors = 20;

            [JsonProperty("Meteor shower: maximum count")]
            public int MaxMeteors = 100;

            [JsonProperty("Meteor shower: zone radius (metres)")]
            public float MeteorRadius = 100f;

            [JsonProperty("Meteor shower: seconds over which meteors land")]
            public float MeteorWindow = 30f;

            [JsonProperty("Meteor shower: blast radius (metres)")]
            public float MeteorBlastRadius = 8f;

            [JsonProperty("Meteor shower: damage at the centre")]
            public float MeteorDamage = 150f;

            [JsonProperty("Meteor shower: meteor projectile prefab")]
            public string MeteorPrefab = PrefabMeteor;

            [JsonProperty("Meteor shower: fall speed (m/s)")]
            public float MeteorSpeed = 55f;

            [JsonProperty("Meteor shower: attach a rock to each meteor")]
            public bool MeteorRock = true;

            [JsonProperty("Meteor shower: rock prefab")]
            public string MeteorRockPrefab = PrefabRock;

            [JsonProperty("Meteor shower: extra impact effects (empty = just the meteor's own burst)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> MeteorEffects = new List<string>();

            [JsonProperty("Meteor shower: extra fire at each impact")]
            public bool MeteorExtraFire = false;

            [JsonProperty("Lightning storm: default strike count")]
            public int DefaultStrikes = 12;

            [JsonProperty("Lightning storm: maximum strike count")]
            public int MaxStrikes = 50;

            [JsonProperty("Lightning storm: zone radius (metres)")]
            public float LightningRadius = 60f;

            [JsonProperty("Lightning storm: seconds over which strikes happen")]
            public float LightningWindow = 6f;

            [JsonProperty("Lightning storm: damage at the centre")]
            public float LightningDamage = 25f;

            [JsonProperty("Lightning storm: strike effects (empty = find a lightning effect automatically)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> LightningEffects = new List<string>();
        }

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
            }
            catch
            {
                config = null;
            }
            if (config == null)
            {
                LoadDefaultConfig();
            }

            // 0.2.0 layered two explosions for the airstrike and a rocket burst on top of each meteor.
            if (SameList(config.AirstrikeEffects, FxBigExplosion, FxC4))
                config.AirstrikeEffects = new List<string> { FxBigExplosion };
            if (SameList(config.MeteorEffects, FxRocket))
                config.MeteorEffects = new List<string>();
            config.MeteorSpeed = Mathf.Max(10f, config.MeteorSpeed);

            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        private static bool SameList(List<string> list, params string[] values) =>
            list != null && list.SequenceEqual(values);

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);
        }

        private void OnServerInitialized()
        {
            airstrikeFx = ResolveEffects("Airstrike", config.AirstrikeEffects, false, "heli_explosion", "c4_explosion");
            meteorFx = ResolveEffects("Meteor shower", config.MeteorEffects, true);
            lightningFx = ResolveEffects("Lightning storm", config.LightningEffects, false, "lightning", "tesla");

            jetPrefab = ResolvePrefab("Airstrike jet", config.AirstrikeJetPrefab, "f15") ?? ResolvePrefab("Airstrike plane", PrefabCargoPlane, "cargo_plane");
            bombPrefab = ResolvePrefab("Airstrike bomb", config.AirstrikeBombPrefab, "rocket_mlrs", "rocket_basic");
            meteorPrefab = ResolvePrefab("Meteor", config.MeteorPrefab, "rocket_fire", "rocket_basic");
            rockPrefab = config.MeteorRock ? ResolvePrefab("Meteor rock", config.MeteorRockPrefab, "stone-ore") : null;
            fireOk = IsKnownPrefab(PrefabFireBall);
        }

        private void Unload()
        {
            foreach (KeyValuePair<BaseEntity, Impact> pair in projectiles.ToList())
            {
                if (pair.Value.Rock != null && !pair.Value.Rock.IsDestroyed)
                    pair.Value.Rock.Kill();
                if (pair.Key != null && !pair.Key.IsDestroyed)
                    pair.Key.Kill();
            }
            projectiles.Clear();
        }

        // A tracked projectile has landed (or been removed): apply the toy's damage and effects there.
        private void OnEntityKill(BaseNetworkable networkable)
        {
            var entity = networkable as BaseEntity;
            Impact impact;
            if (entity == null || !projectiles.TryGetValue(entity, out impact))
                return;

            projectiles.Remove(entity);
            Vector3 at = entity.transform.position;

            if (impact.Rock != null && !impact.Rock.IsDestroyed)
                impact.Rock.Kill();

            Detonate(at, impact.Radius, impact.Damage, impact.Caller, impact.Effects);
            if (impact.Fire && fireOk)
                SpawnFire(at);
        }

        #endregion

        #region Chat commands

        [ChatCommand("airstrike")]
        private void CmdAirstrike(BasePlayer player, string command, string[] args)
        {
            if (!TryStartUse(player, out Vector3 target))
            {
                return;
            }

            PrintToChat($"Air support inbound for {player.displayName}. Clear the area!");

            Vector2 heading = UnityEngine.Random.insideUnitCircle.normalized;
            Vector3 direction = new Vector3(heading.x, 0f, heading.y);
            SpawnJet(target, direction);

            // Release the bomb so it lands at the impact delay.
            float fall = BombHeight / BombSpeed;
            float release = Mathf.Max(0.5f, config.AirstrikeImpactDelay - fall);
            timer.Once(release, () =>
            {
                var impact = new Impact
                {
                    Radius = config.AirstrikeRadius,
                    Damage = config.AirstrikeDamage,
                    Caller = player,
                    Effects = airstrikeFx
                };

                Vector3 from = target + Vector3.up * BombHeight;
                if (!LaunchProjectile(bombPrefab, from, target, BombSpeed, impact, false))
                    timer.Once(fall, () => Detonate(target, impact.Radius, impact.Damage, player, impact.Effects));
            });
        }

        [ChatCommand("massdrop")]
        private void CmdMassDrop(BasePlayer player, string command, string[] args)
        {
            if (!TryParseCount(args, config.DefaultCrates, config.MaxCrates, player, out int count))
            {
                return;
            }
            if (!TryStartUse(player, out Vector3 center))
            {
                return;
            }

            PrintToChat($"Mass airdrop: {count} crates incoming!");
            for (int i = 0; i < count; i++)
            {
                Vector3 spot = RandomPointInDisc(center, config.AirdropRadius);
                timer.Once(i * AirdropSpacing, () => SpawnSupplyDrop(spot));
            }
        }

        [ChatCommand("meteors")]
        private void CmdMeteors(BasePlayer player, string command, string[] args)
        {
            if (!TryParseCount(args, config.DefaultMeteors, config.MaxMeteors, player, out int count))
            {
                return;
            }
            if (!TryStartUse(player, out Vector3 center))
            {
                return;
            }

            PrintToChat("Meteor shower over the area!");
            for (int i = 0; i < count; i++)
            {
                Vector3 spot = RandomPointInDisc(center, config.MeteorRadius);
                float delay = UnityEngine.Random.Range(0f, config.MeteorWindow);
                timer.Once(delay, () => LaunchMeteor(spot, player));
            }
        }

        [ChatCommand("lightningstorm")]
        private void CmdLightningStorm(BasePlayer player, string command, string[] args)
        {
            if (!TryParseCount(args, config.DefaultStrikes, config.MaxStrikes, player, out int count))
            {
                return;
            }
            if (!TryStartUse(player, out Vector3 center))
            {
                return;
            }

            PrintToChat("Lightning storm rolling in!");
            for (int i = 0; i < count; i++)
            {
                Vector3 spot = RandomPointInDisc(center, config.LightningRadius);
                float delay = UnityEngine.Random.Range(0f, config.LightningWindow);
                timer.Once(delay, () => Detonate(spot, 2f, config.LightningDamage, player, lightningFx));
            }
        }

        #endregion

        #region Toys

        // Flies the jet (or the cargo plane fallback) over the target along `direction`, then removes it.
        private void SpawnJet(Vector3 target, Vector3 direction)
        {
            if (jetPrefab == null)
                return;

            Vector3 start = target - direction * JetRunIn + Vector3.up * JetAltitude;
            BaseEntity jet = GameManager.server.CreateEntity(jetPrefab, start, Quaternion.LookRotation(direction));
            if (jet == null)
            {
                PrintWarning($"Could not spawn airstrike aircraft: {jetPrefab}");
                return;
            }
            jet.EnableSaving(false);
            jet.Spawn();

            // The stock aircraft fly their own routes (the cargo plane can even drop a crate),
            // so remove it shortly after the strike so it's only a flyover.
            timer.Once(config.AirstrikeImpactDelay + 6f, () =>
            {
                if (jet != null && !jet.IsDestroyed)
                    jet.Kill();
            });
        }

        // A meteor comes in at an angle from a random side and bursts where it lands.
        private void LaunchMeteor(Vector3 spot, BasePlayer caller)
        {
            Vector2 side = UnityEngine.Random.insideUnitCircle.normalized * MeteorSlant;
            Vector3 from = spot + new Vector3(side.x, MeteorHeight, side.y);

            var impact = new Impact
            {
                Radius = config.MeteorBlastRadius,
                Damage = config.MeteorDamage,
                Caller = caller,
                Effects = meteorFx,
                Fire = config.MeteorExtraFire
            };

            if (!LaunchProjectile(meteorPrefab, from, spot, config.MeteorSpeed, impact, rockPrefab != null))
            {
                // No projectile prefab on this build: fall back to an explosion on the spot.
                Detonate(spot, impact.Radius, impact.Damage, caller, meteorFx.Count > 0 ? meteorFx : new List<string> { FxExplosion });
            }
        }

        // Fires a projectile from `from` towards `to`. Its own damage is switched off; this plugin
        // applies the toy's player damage where it lands (see OnEntityKill), so bases aren't hurt.
        private bool LaunchProjectile(string prefab, Vector3 from, Vector3 to, float speed, Impact impact, bool withRock)
        {
            if (string.IsNullOrEmpty(prefab))
                return false;

            Vector3 direction = (to - from).normalized;
            BaseEntity entity = GameManager.server.CreateEntity(prefab, from, Quaternion.LookRotation(direction));
            if (entity == null)
                return false;

            var projectile = entity.GetComponent<ServerProjectile>();
            if (projectile == null)
            {
                PrintWarning($"'{prefab}' isn't a projectile, so it can't be launched.");
                entity.Kill();
                return false;
            }

            projectile.InitializeVelocity(direction * speed);
            var explosive = entity as TimedExplosive;
            if (explosive != null)
                explosive.damageTypes.Clear();
            entity.creatorEntity = impact.Caller;
            entity.EnableSaving(false);
            entity.Spawn();
            projectiles[entity] = impact;

            if (withRock)
                impact.Rock = AttachRock(entity);

            timer.Once(ProjectileTimeout, () =>
            {
                if (entity == null || entity.IsDestroyed || !projectiles.Remove(entity))
                    return;
                if (impact.Rock != null && !impact.Rock.IsDestroyed)
                    impact.Rock.Kill();
                entity.Kill();
            });
            return true;
        }

        // Rides a rock just behind the meteor so it reads as a burning rock rather than a rocket.
        private BaseEntity AttachRock(BaseEntity meteor)
        {
            BaseEntity rock = GameManager.server.CreateEntity(rockPrefab, meteor.transform.position, meteor.transform.rotation);
            if (rock == null)
                return null;

            rock.EnableSaving(false);
            rock.SetParent(meteor);
            rock.transform.localPosition = new Vector3(0f, 0f, -2.5f);   // behind the nose, out of its flight path
            rock.Spawn();
            return rock;
        }

        private void SpawnSupplyDrop(Vector3 ground)
        {
            Vector3 pos = ground + Vector3.up * DropHeight;
            BaseEntity drop = GameManager.server.CreateEntity(PrefabSupplyDrop, pos, Quaternion.identity);
            if (drop == null)
            {
                PrintWarning($"Could not spawn supply drop prefab: {PrefabSupplyDrop}");
                return;
            }
            drop.Spawn();
        }

        /// Plays the effects at `center` and damages players within `radius`, falling off with distance.
        private void Detonate(Vector3 center, float radius, float damage, BasePlayer caller, List<string> effects)
        {
            foreach (string fx in effects)
                Effect.server.Run(fx, center);

            if (radius <= 0f)
                return;

            foreach (BasePlayer victim in BasePlayer.activePlayerList)
            {
                if (victim == null || victim.IsDead())
                {
                    continue;
                }

                float distance = Vector3.Distance(victim.transform.position, center);
                if (distance > radius)
                {
                    continue;
                }

                float falloff = 1f - distance / radius;
                victim.Hurt(damage * falloff, DamageType.Explosion, caller, false);
            }
        }

        private void SpawnFire(Vector3 position)
        {
            BaseEntity fire = GameManager.server.CreateEntity(PrefabFireBall, position + Vector3.up * 0.3f, Quaternion.identity);
            if (fire != null)
                fire.Spawn();
        }

        #endregion

        #region Helpers

        /// Checks permission and cooldown, then aims where the player is looking.
        /// The cooldown only starts once a toy really fires.
        private bool TryStartUse(BasePlayer player, out Vector3 target)
        {
            target = Vector3.zero;

            if (!permission.UserHasPermission(player.UserIDString, PermUse))
            {
                player.ChatMessage("You don't have permission to use fun toys.");
                return false;
            }

            bool isAdmin = permission.UserHasPermission(player.UserIDString, PermAdmin);
            if (!isAdmin && lastUsed.TryGetValue(player.userID, out float last))
            {
                float remaining = last + config.CooldownSeconds - Time.realtimeSinceStartup;
                if (remaining > 0f)
                {
                    player.ChatMessage($"Toys are on cooldown for {Mathf.CeilToInt(remaining)} more seconds.");
                    return false;
                }
            }

            if (!Physics.Raycast(player.eyes.HeadRay(), out RaycastHit hit, config.MaxRange, LayerMask.GetMask("Terrain", "World", "Default")))
            {
                player.ChatMessage("Aim at the ground within range first.");
                return false;
            }

            target = hit.point;
            lastUsed[player.userID] = Time.realtimeSinceStartup;
            return true;
        }

        private bool TryParseCount(string[] args, int fallback, int max, BasePlayer player, out int count)
        {
            count = fallback;
            if (args.Length == 0)
            {
                return true;
            }
            if (!int.TryParse(args[0], out count) || count < 1 || count > max)
            {
                player.ChatMessage($"Give a count between 1 and {max}.");
                return false;
            }
            return true;
        }

        private static Vector3 RandomPointInDisc(Vector3 center, float radius)
        {
            Vector2 offset = UnityEngine.Random.insideUnitCircle * radius;
            return new Vector3(center.x + offset.x, center.y, center.z + offset.y);
        }

        // Keeps configured effects that exist on this server. If none do (and the list may not be
        // empty), looks one up in the game's prefab list by keyword, then uses the generic explosion.
        private List<string> ResolveEffects(string toy, List<string> configured, bool allowEmpty, params string[] keywords)
        {
            var found = new List<string>();
            foreach (string path in configured ?? new List<string>())
            {
                if (IsKnownPrefab(path))
                    found.Add(path);
                else
                    PrintWarning($"{toy}: effect '{path}' doesn't exist on this server.");
            }
            if (found.Count > 0 || (allowEmpty && (configured == null || configured.Count == 0)))
                return found;

            foreach (string keyword in keywords)
            {
                string match = FindPrefab(keyword, true);
                if (match == null)
                    continue;
                Puts($"{toy}: using effect '{match}'. Put it (or another effect) in the config to choose your own.");
                found.Add(match);
                return found;
            }

            PrintWarning($"{toy}: no matching effect found; using the generic explosion.");
            found.Add(FxExplosion);
            return found;
        }

        // Returns the configured entity prefab if it exists, otherwise the shortest prefab matching
        // a keyword, otherwise null. Logs any substitution.
        private string ResolvePrefab(string label, string configured, params string[] keywords)
        {
            if (IsKnownPrefab(configured))
                return configured;

            foreach (string keyword in keywords)
            {
                string match = FindPrefab(keyword, false);
                if (match == null)
                    continue;
                PrintWarning($"{label}: '{configured}' doesn't exist on this server; using '{match}'.");
                return match;
            }

            PrintWarning($"{label}: '{configured}' doesn't exist on this server and nothing similar was found.");
            return null;
        }

        private static bool IsKnownPrefab(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            try
            {
                return StringPool.toNumber.ContainsKey(path);
            }
            catch
            {
                return true;   // can't check on this build; trust the config
            }
        }

        // Effects live under effects/fx folders; entities are everything else.
        private static string FindPrefab(string keyword, bool effect)
        {
            try
            {
                return StringPool.toNumber.Keys
                    .Where(k => k.EndsWith(".prefab") && k.Contains(keyword))
                    .Where(k => (k.Contains("/effects/") || k.Contains("/fx/")) == effect)
                    .OrderBy(k => k.Length)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }
}
