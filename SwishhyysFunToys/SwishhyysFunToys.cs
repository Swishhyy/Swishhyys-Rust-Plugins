using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SwishhyysFunToys", "Swishhyy", "0.2.0")]
    [Description("Fun toys: cargo plane airstrikes, mass airdrops, meteor showers and lightning storms.")]
    public class SwishhyysFunToys : RustPlugin
    {
        private const string PermUse = "swishhyysfuntoys.use";
        private const string PermAdmin = "swishhyysfuntoys.admin";

        // Prefab paths. Confirm these against your server's build if an entity fails to spawn.
        private const string PrefabCargoPlane = "assets/prefabs/npc/cargo plane/cargo_plane.prefab";
        private const string PrefabSupplyDrop = "assets/prefabs/misc/supply drop/supply_drop.prefab";
        private const string FxExplosion = "assets/bundled/prefabs/fx/explosions/explosion_01.prefab";   // last-resort fallback
        private const string PrefabFireBall = "assets/bundled/prefabs/oilfireballsmall.prefab";

        // Effects checked against the game's prefab list at startup.
        private List<string> airstrikeFx = new List<string> { FxExplosion };
        private List<string> meteorFx = new List<string> { FxExplosion };
        private List<string> lightningFx = new List<string> { FxExplosion };
        private bool meteorFireOk;

        private const float DropHeight = 100f;
        private const float AirdropSpacing = 0.4f;

        private Configuration config;
        private readonly Dictionary<ulong, float> lastUsed = new Dictionary<ulong, float>();

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

            [JsonProperty("Airstrike: explosion effects (all play together)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> AirstrikeEffects = new List<string>
            {
                "assets/prefabs/npc/patrol helicopter/effects/heli_explosion.prefab",
                "assets/prefabs/tools/c4/effects/c4_explosion.prefab"
            };

            [JsonProperty("Meteor shower: impact effects", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> MeteorEffects = new List<string>
            {
                "assets/prefabs/weapons/rocketlauncher/effects/rocket_explosion.prefab"
            };

            [JsonProperty("Meteor shower: leave a small fire at each impact")]
            public bool MeteorFire = true;

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
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);
        }

        private void OnServerInitialized()
        {
            airstrikeFx = ResolveEffects("Airstrike", config.AirstrikeEffects, "heli_explosion", "c4_explosion");
            meteorFx = ResolveEffects("Meteor shower", config.MeteorEffects, "rocket_explosion", "c4_explosion");
            lightningFx = ResolveEffects("Lightning storm", config.LightningEffects, "lightning", "tesla");

            meteorFireOk = config.MeteorFire && IsKnownPrefab(PrefabFireBall);
            if (config.MeteorFire && !meteorFireOk)
                PrintWarning($"Meteor fire prefab '{PrefabFireBall}' doesn't exist on this server; meteors won't leave fire.");
        }

        // ---------- Chat commands ----------

        [ChatCommand("airstrike")]
        private void CmdAirstrike(BasePlayer player, string command, string[] args)
        {
            if (!TryStartUse(player, out Vector3 target))
            {
                return;
            }

            PrintToChat($"Cargo plane inbound for {player.displayName}. Clear the area!");
            SpawnCargoPlane(target);
            timer.Once(config.AirstrikeImpactDelay, () => Detonate(target, config.AirstrikeRadius, config.AirstrikeDamage, player, airstrikeFx));
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
                timer.Once(delay, () =>
                {
                    Detonate(spot, config.MeteorBlastRadius, config.MeteorDamage, player, meteorFx);
                    if (meteorFireOk)
                        SpawnFire(spot);
                });
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

        // ---------- Helpers ----------

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

        private void SpawnCargoPlane(Vector3 target)
        {
            // The plane starts behind the target and flies over it, so the bomb matches its pass.
            Vector3 start = target + new Vector3(-300f, 150f, 0f);

            BaseEntity plane = GameManager.server.CreateEntity(PrefabCargoPlane, start, Quaternion.identity);
            if (plane == null)
            {
                PrintWarning($"Could not spawn cargo plane prefab: {PrefabCargoPlane}");
                return;
            }
            plane.Spawn();

            // The stock cargo plane flies its own route and can drop a free supply crate.
            // Remove it once the strike is over so it's only the visual.
            timer.Once(config.AirstrikeImpactDelay + 2f, () =>
            {
                if (plane != null && !plane.IsDestroyed)
                    plane.Kill();
            });
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

        /// Plays the toy's effects at `center` and damages players within `radius`, falling off with distance.
        private void Detonate(Vector3 center, float radius, float damage, BasePlayer caller, List<string> effects)
        {
            foreach (string fx in effects)
                Effect.server.Run(fx, center);

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

        // ---------- Effects ----------

        // Keeps configured effects that exist on this server. If none do, looks for one in the game's
        // prefab list by keyword, and as a last resort uses the generic explosion. Logs what it picked.
        private List<string> ResolveEffects(string toy, List<string> configured, params string[] keywords)
        {
            var found = new List<string>();
            foreach (string path in configured ?? new List<string>())
            {
                if (IsKnownPrefab(path))
                    found.Add(path);
                else
                    PrintWarning($"{toy}: effect '{path}' doesn't exist on this server.");
            }
            if (found.Count > 0)
                return found;

            foreach (string keyword in keywords)
            {
                string match = FindEffect(keyword);
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

        private static string FindEffect(string keyword)
        {
            try
            {
                return StringPool.toNumber.Keys
                    .Where(k => k.EndsWith(".prefab") && k.Contains(keyword) && (k.Contains("/effects/") || k.Contains("/fx/")))
                    .OrderBy(k => k.Length)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private void SpawnFire(Vector3 position)
        {
            BaseEntity fire = GameManager.server.CreateEntity(PrefabFireBall, position + Vector3.up * 0.3f, Quaternion.identity);
            if (fire != null)
                fire.Spawn();
        }
    }
}
