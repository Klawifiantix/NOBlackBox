using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace NOBlackBox
{
    internal class ACMIGunBurst_mono : ACMIObject_mono
    {
        static int _next;
        readonly Dictionary<int, Burst> _open = new();
        Gun[] _guns = Array.Empty<Gun>();
        float _scan;
        FieldInfo? _ticks;
        FieldInfo? _muzzles;
        FieldInfo? _rate;
        FieldInfo? _muzzleVel;
        FieldInfo? _spread;
        FieldInfo? _info;

        void Awake()
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _ticks = typeof(Gun).GetField("ticksSinceTriggerPull", flags);
            _muzzles = typeof(Gun).GetField("muzzles", flags);
            _rate = typeof(Gun).GetField("fireRate", flags);
            _muzzleVel = typeof(Gun).GetField("muzzleVelocity", flags);
            _spread = typeof(Gun).GetField("bulletSpread", flags);
            _info = typeof(Weapon).GetField("info", flags);
        }

        void Update()
        {
            _scan += Time.deltaTime;
            if (_scan > 0.5f)
            {
                _scan = 0f;
                _guns = UnityEngine.Object.FindObjectsOfType<Gun>();
            }
            var recorder = Plugin.recorderMono.GetComponent<Recorder_mono>();
            float now = Time.time;
            var seen = new HashSet<int>();
            for (int i = 0; i < _guns.Length; i++)
            {
                var gun = _guns[i];
                if (gun == null || gun.attachedUnit == null) continue;
                int id = gun.GetInstanceID();
                seen.Add(id);
                bool firing = _ticks?.GetValue(gun) is int ticks && ticks < 3;
                if (!_open.TryGetValue(id, out var burst))
                {
                    if (!firing) continue;
                    burst = Begin(gun, recorder);
                    if (burst == null) continue;
                    _open[id] = burst;
                }
                if (firing)
                {
                    if (now - burst.LastAim >= 0.1f)
                        WriteAim(burst, gun);
                }
                else if (now - burst.LastAim > 0.15f)
                {
                    End(burst);
                    _open.Remove(id);
                }
            }
            var stale = new List<int>();
            foreach (var pair in _open)
                if (!seen.Contains(pair.Key)) stale.Add(pair.Key);
            for (int i = 0; i < stale.Count; i++)
            {
                End(_open[stale[i]]);
                _open.Remove(stale[i]);
            }
        }

        Burst? Begin(Gun gun, Recorder_mono recorder)
        {
            var unit = gun.attachedUnit;
            if (unit == null) return null;
            long parent = unit.persistentID.Id + 1;
            if (recorder.unitObjects.TryGetValue(unit.persistentID.Id, out var go) && go != null)
            {
                var acmi = go.GetComponent<ACMIUnit_mono>();
                if (acmi != null) parent = acmi.tacviewId;
            }
            var info = _info?.GetValue(gun);
            float rpm = _rate?.GetValue(gun) is float r ? r : 0f;
            float muzzle = _muzzleVel?.GetValue(gun) is float m ? m : 0f;
            float spread = _spread?.GetValue(gun) is float s ? s : 0f;
            float drag = Field(info, "dragCoef");
            float grav = Field(info, "gravMult");
            if (grav == 0f) grav = 1f;
            var burst = new Burst
            {
                Host = gameObject.AddComponent<ACMIObject_mono>(),
                Gun = gun
            };
            burst.Host.unitId = (long)System.Threading.Interlocked.Increment(ref _next) | (1L << 34);
            burst.Host.tacviewId = burst.Host.unitId;
            burst.Host.props = new Dictionary<string, string>
            {
                { "Type", "Misc+GunBurst" },
                { "Parent", parent.ToString("X", CultureInfo.InvariantCulture) },
                { "Name", unit.definition != null ? unit.definition.unitName : unit.name },
                { "Rpm", rpm.ToString("0.##", CultureInfo.InvariantCulture) },
                { "Muzzle", muzzle.ToString("0.##", CultureInfo.InvariantCulture) },
                { "Drag", drag.ToString("0.####", CultureInfo.InvariantCulture) },
                { "Grav", grav.ToString("0.##", CultureInfo.InvariantCulture) },
                { "Spread", spread.ToString("0.###", CultureInfo.InvariantCulture) }
            };
            recorder.invokeWriterUpdate(burst.Host);
            burst.Host.props = new Dictionary<string, string>();
            WriteAim(burst, gun);
            return burst;
        }

        void WriteAim(Burst burst, Gun gun)
        {
            var muzzles = _muzzles?.GetValue(gun) as Transform[];
            Transform? muzzle = muzzles != null && muzzles.Length > 0 ? muzzles[0] : gun.transform;
            if (muzzle == null) return;
            Vector3 p = muzzle.position;
            Vector3 f = muzzle.forward;
            if (f.sqrMagnitude < 0.01f) f = gun.transform.forward;
            float yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
            (float lat, float lon) = Helpers.CartesianToGeodetic(p.x, p.z);
            burst.Host.props["T"] = string.Join("|",
                lon.ToString(CultureInfo.InvariantCulture),
                lat.ToString(CultureInfo.InvariantCulture),
                p.y.ToString("0.##", CultureInfo.InvariantCulture),
                "0",
                (-pitch).ToString("0.##", CultureInfo.InvariantCulture),
                yaw.ToString("0.##", CultureInfo.InvariantCulture),
                p.x.ToString("0.##", CultureInfo.InvariantCulture),
                p.z.ToString("0.##", CultureInfo.InvariantCulture),
                yaw.ToString("0.##", CultureInfo.InvariantCulture));
            Plugin.recorderMono.GetComponent<Recorder_mono>().invokeWriterUpdate(burst.Host);
            burst.Host.props = new Dictionary<string, string>();
            burst.LastAim = Time.time;
        }

        void End(Burst burst)
        {
            burst.Host.props["Visible"] = "0";
            burst.Host.props["Type"] = "";
            var recorder = Plugin.recorderMono.GetComponent<Recorder_mono>();
            recorder.invokeWriterUpdate(burst.Host);
            recorder.invokeWriterRemove(burst.Host);
            UnityEngine.Object.Destroy(burst.Host);
        }

        static float Field(object? obj, string name)
        {
            if (obj == null) return 0f;
            var f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return f?.GetValue(obj) is float v ? v : 0f;
        }

        sealed class Burst
        {
            public ACMIObject_mono Host = null!;
            public Gun Gun = null!;
            public float LastAim = -1f;
        }
    }
}