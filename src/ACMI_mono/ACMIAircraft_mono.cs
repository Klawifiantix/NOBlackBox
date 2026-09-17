using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace NOBlackBox
{
    internal class ACMIAircraft_mono : ACMIUnit_mono
    {
        private bool lastGear = false;
        private bool lastRadar = false;
        private float lastAGL = float.NaN;
        private float lastTAS = float.NaN;
        private float lastAOA = float.NaN;
        private float lastThrottle = float.NaN;
        private float lastPitch = float.NaN;
        private float lastYaw = float.NaN;
        private float lastRoll = float.NaN;
        private float lastThrust = float.NaN;
        private float avgThrust = float.NaN;

        private Vector3 lastHead = Vector3.zero;

        Aircraft aircraft;
        Aircraft localAircraft = null;

        private string? steamId;
        private string? lastEmittedPilot;

        private static readonly object CacheLock = new();
        private static Dictionary<string, string> nameCache = new();
        private static float cacheTimer;
        private static string? cachePath;

        public virtual void Init(Aircraft aircraft)
        {
            GameManager.GetLocalAircraft(out localAircraft);
            if (localAircraft && localAircraft.persistentID == aircraft.persistentID) { base.unit = localAircraft; } else { base.unit = aircraft; }
            this.aircraft = (Aircraft)base.unit;
            base.unitId = aircraft.persistentID.Id;
            base.tacviewId = aircraft.persistentID.Id + 1;
            base.destroyedEvent = true;
            base.canTarget = true;
            lastState = aircraft.unitState;
            Faction? faction = this.unit.NetworkHQ?.faction;
            string[] info = { "Default", "Air" };
            if (Plugin.NOBlackBoxUnitInfo["aircraft"].ContainsKey(aircraft.definition.code))
            {
                info = Plugin.NOBlackBoxUnitInfo["aircraft"][unit.definition.code];
            }
            props = new Dictionary<string, string>()
            {
                { "Name", this.unit.definition.unitName },
                { "Coalition", faction?.factionName ?? "Neutral" },
                { "Color", faction == null ? "Green" : (faction.factionName == "Boscali" ? "Blue" : "Red") },
                { "Debug", lastState.ToString()},
                { "Type", info[1]},
                { "CallSign", $"{aircraft.definition.code} {tacviewId:X}" }
            };

            if (aircraft.Player != null)
            {
                steamId = aircraft.Player.SteamID.ToString();
                if (Configuration.RecordSteamID.Value == true)
                    props["Registration"] = steamId;

                ApplyPilotProps(force: true);
            }

            Plugin.recorderMono.GetComponent<Recorder_mono>().invokeWriterUpdate(this);
            props = [];
            this.enabled = true;
            base.enabled = true;
        }

        public override void Update()
        {
            base.destroyedEvent = !aircraft.IsLanded();
            if (!this.enabled || unit.disabled)
                return;

            timer += Time.deltaTime;
            cacheTimer += Time.deltaTime;

            if (cacheTimer >= 2f)
            {
                cacheTimer = 0f;
                RefreshSharedCache();
            }

            if (aircraft.Player != null)
            {
                steamId ??= aircraft.Player.SteamID.ToString();
                RememberNameFromThisProcess();
                ApplyPilotProps(force: false);
            }

            if (timer < Configuration.aircraftUpdateDelta.Value)
                return;

            UpdatePose();
            UpdateAircraft();
            UpdateState();
            UpdateTargets();
            Plugin.recorderMono.GetComponent<Recorder_mono>().invokeWriterUpdate(this);
            props = [];
            timer = 0;
        }

        internal override void UpdateTargets()
        {
            targets = aircraft.weaponManager.GetTargetList().ToArray();
            if (targets.Any() && targets != lastTargets)
            {
                lastTargets = targets;
                int max = targets.Length;
                if (max > 10)
                    max = 10;
                if (targets.Length > 1)
                {
                    for (int i = 0; i < max; i++)
                    {
                        lockedTargetString = i == 0 ? "LockedTarget" : $"LockedTarget{i:X}";
                        props.Add(lockedTargetString, $"{GetTacviewIdOfUnit(targets[i].persistentID.Id):X}");
                    }
                }
            }
        }

        private float getAvgThrust()
        {
            float avgThrust = float.NaN;
            int count = aircraft.engines.Count;
            for (int i = 0; i < aircraft.engines.Count; i++)
            {
                float thrust = aircraft.engines[i].GetThrust();
                if (thrust != 0f)
                    avgThrust = avgThrust + thrust;
                else
                    count = count - 1;
            }
            if (avgThrust != 0f)
                avgThrust /= count;
            return avgThrust;
        }

        void UpdateAircraft()
        {
            if (aircraft.speed != lastTAS && Configuration.RecordSpeed.Value == true)
            {
                props.Add("TAS", aircraft.speed.ToString("0.##", CultureInfo.InvariantCulture));
                props.Add("Mach", (aircraft.speed / 340).ToString("0.###", CultureInfo.InvariantCulture));
                lastTAS = aircraft.speed;
            }

            Vector3 vector3 = aircraft.cockpit.transform.InverseTransformDirection(aircraft.cockpit.rb.velocity);
            float num = MathF.Round(Mathf.Atan2(vector3.y, vector3.z) * -57.29578f, 2);

            if (num != lastAOA && Configuration.RecordAOA.Value == true)
            {
                props.Add("AOA", num.ToString("0.##"));
                lastAOA = num;
            }

            if (aircraft.radarAlt != lastAGL && Configuration.RecordAGL.Value == true)
            {
                props.Add("AGL", Mathf.Max(0, aircraft.radarAlt).ToString("0.##", CultureInfo.InvariantCulture));
                lastAGL = aircraft.radarAlt;
            }

            if (aircraft.gearDeployed != lastGear && Configuration.RecordLandingGear.Value == true)
            {
                props.Add("LandingGear", aircraft.gearDeployed ? "1" : "0");
                lastGear = aircraft.gearDeployed;
            }

            if (aircraft.radar != lastRadar && Configuration.RecordRadarMode.Value == true)
            {
                props.Add("RadarMode", aircraft.radar.activated ? "1" : "0");
                lastRadar = aircraft.radar;
            }

            if (localAircraft && localAircraft.persistentID == aircraft.persistentID && CameraStateManager.cameraMode == CameraMode.cockpit && Configuration.RecordPilotHead.Value == true)
            {
                Camera camera = CameraStateManager.i.mainCamera;
                Vector3 rot = camera.transform.localEulerAngles;
                Vector3 newRot = new(MathF.Round(rot.x, 2), MathF.Round(rot.y, 2), MathF.Round(rot.z, 2));
                if (newRot != lastHead)
                {
                    if (!Mathf.Approximately(newRot.x, lastHead.x))
                    {
                        float adjusted_pitch = newRot.x > 180.0f ? 360 - newRot.x : -newRot.x;
                        props.Add("PilotHeadPitch", adjusted_pitch.ToString("0.##", CultureInfo.InvariantCulture));
                    }
                    if (!Mathf.Approximately(newRot.y, lastHead.y))
                    {
                        props.Add("PilotHeadYaw", newRot.y.ToString("0.##", CultureInfo.InvariantCulture));
                    }
                    lastHead = newRot;
                }
            }

            if (Configuration.RecordExtraTelemetry.Value == true)
            {
                if (lastThrottle != aircraft.GetInputs().throttle)
                {
                    props.Add("Throttle", aircraft.GetInputs().throttle.ToString("0.##", CultureInfo.InvariantCulture));
                    lastThrottle = aircraft.GetInputs().throttle;
                }
                if (lastRoll != aircraft.GetInputs().roll)
                {
                    props.Add("RollControlInput", aircraft.GetInputs().roll.ToString("0.##", CultureInfo.InvariantCulture));
                    lastRoll = aircraft.GetInputs().roll;
                }
                if (lastPitch != aircraft.GetInputs().pitch)
                {
                    props.Add("PitchControlInput", aircraft.GetInputs().pitch.ToString("0.##", CultureInfo.InvariantCulture));
                    lastPitch = aircraft.GetInputs().pitch;
                }
                if (lastYaw != aircraft.GetInputs().yaw)
                {
                    props.Add("YawControlInput", aircraft.GetInputs().yaw.ToString("0.##", CultureInfo.InvariantCulture));
                    lastYaw = aircraft.GetInputs().yaw;
                }
                if (lastThrust != getAvgThrust())
                {
                    avgThrust = getAvgThrust();
                    props.Add("EngineRPM", avgThrust.ToString("0.##", CultureInfo.InvariantCulture));
                    lastThrust = avgThrust;
                }
            }
        }

        private void ApplyPilotProps(bool force)
        {
            string pilot = ResolvePilotName();
            if (!force && pilot == lastEmittedPilot)
                return;

            lastEmittedPilot = pilot;
            props["CallSign"] = $"{aircraft.definition.code} ({EscapeAcmi(pilot)})";
        }

        private void RememberNameFromThisProcess()
        {
            if (aircraft.Player == null || string.IsNullOrEmpty(steamId))
                return;

            string? n = TrySteamPersona(aircraft.Player) ?? TryDirectPlayerName(aircraft.Player);
            if (IsUselessName(n))
                return;

            lock (CacheLock)
            {
                if (!nameCache.TryGetValue(steamId, out var old) || old != n)
                {
                    nameCache[steamId] = n!;
                    SaveCacheUnlocked();
                }
            }
        }

        private string ResolvePilotName()
        {
            if (!string.IsNullOrEmpty(steamId))
            {
                lock (CacheLock)
                {
                    if (nameCache.TryGetValue(steamId, out var cached) && !IsUselessName(cached))
                        return cached;
                }
            }

            if (aircraft.Player != null)
            {
                string? live = TrySteamPersona(aircraft.Player) ?? TryDirectPlayerName(aircraft.Player);
                if (!IsUselessName(live))
                    return live!;
            }

            return steamId ?? "Unknown";
        }

        private static string CacheFile()
        {
            if (cachePath != null)
                return cachePath;

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
                "Shockfront", "NuclearOption");
            try { Directory.CreateDirectory(dir); } catch { }
            cachePath = Path.Combine(dir, "noblackbox_names.json");
            return cachePath;
        }

        private static void RefreshSharedCache()
        {
            try
            {
                string path = CacheFile();
                if (!File.Exists(path))
                    return;

                string raw;
                lock (CacheLock)
                    raw = File.ReadAllText(path);

                var parsed = ParseSimpleJsonMap(raw);
                lock (CacheLock)
                {
                    foreach (var kv in parsed)
                    {
                        if (!IsUselessName(kv.Value))
                            nameCache[kv.Key] = kv.Value;
                    }
                }
            }
            catch { }
        }

        private static void SaveCacheUnlocked()
        {
            try
            {
                File.WriteAllText(CacheFile(), ToSimpleJsonMap(nameCache));
            }
            catch { }
        }

        private static string? TryDirectPlayerName(object player)
        {
            var bucket = new List<string>();
            HarvestObject(player, bucket);
            return bucket.FirstOrDefault(c => !Regex.IsMatch(c, @"^\d{1,17}$"));
        }

        private static string? TrySteamPersona(object player)
        {
            try
            {
                object? steamIdObj = player.GetType().GetProperty("SteamID")?.GetValue(player);
                if (steamIdObj == null)
                    return null;

                ulong sid = Convert.ToUInt64(steamIdObj);

                Type? friends =
                    FindType("Steamworks.SteamFriends") ??
                    FindType("Steamworks.SteamFriends, Assembly-CSharp");
                Type? cidType =
                    FindType("Steamworks.CSteamID") ??
                    FindType("Steamworks.CSteamID, Assembly-CSharp");
                if (friends == null)
                    return null;

                object idArg = steamIdObj;
                if (cidType != null)
                {
                    try { idArg = Activator.CreateInstance(cidType, sid)!; } catch { }
                }

                friends.GetMethod("RequestUserInformation")?.Invoke(null, new[] { idArg, true });
                object? raw = friends.GetMethod("GetFriendPersonaName")?.Invoke(null, new[] { idArg });
                string? name = raw?.ToString();
                if (IsUselessName(name) || name!.Equals("[unknown]", StringComparison.OrdinalIgnoreCase))
                    return null;
                return name;
            }
            catch
            {
                return null;
            }
        }

        private static Type? FindType(string name)
        {
            Type? t = Type.GetType(name);
            if (t != null) return t;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    t = a.GetType(name.Split(',')[0], false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static string EscapeAcmi(string value) => (value ?? "").Replace(",", "\\,");

        private static bool IsUselessName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            string v = value.Trim();
            if (v.Contains("PlayerName")) return true;
            if (Regex.IsMatch(v, @"^Player(\s*\(\d+\))?$", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(v, @"^\d{1,3}$")) return true;
            return false;
        }

        private static void Consider(List<string> bucket, object? value)
        {
            if (value == null) return;
            string tn = value.GetType().FullName ?? "";
            if (tn.Contains("PlayerName") && value is not string) return;
            string text = value.ToString();
            if (!IsUselessName(text))
                bucket.Add(text.Trim());
        }

        private static void HarvestObject(object? obj, List<string> bucket, int depth = 0)
        {
            if (obj == null || depth > 2) return;
            Type type = obj.GetType();
            if (type == typeof(string) || type.IsPrimitive)
            {
                Consider(bucket, obj);
                return;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (PropertyInfo prop in type.GetProperties(flags))
            {
                if (prop.GetIndexParameters().Length > 0) continue;
                if (!Regex.IsMatch(prop.Name, "name|player|pilot|steam|nick|display|persona", RegexOptions.IgnoreCase))
                    continue;
                try
                {
                    object? val = prop.GetValue(obj);
                    if (val != null && (val.GetType().FullName ?? "").Contains("PlayerName"))
                        HarvestObject(val, bucket, depth + 1);
                    else
                        Consider(bucket, val);
                }
                catch { }
            }
        }

        private static Dictionary<string, string> ParseSimpleJsonMap(string raw)
        {
            var d = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(raw, "\"(\\d{17})\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\""))
                d[m.Groups[1].Value] = Regex.Unescape(m.Groups[2].Value);
            return d;
        }

        private static string ToSimpleJsonMap(Dictionary<string, string> map)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (var kv in map)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(kv.Key).Append("\":\"");
                sb.Append(kv.Value.Replace("\\", "\\\\").Replace("\"", "\\\""));
                sb.Append('"');
            }
            sb.Append('}');
            return sb.ToString();
        }
    }
}