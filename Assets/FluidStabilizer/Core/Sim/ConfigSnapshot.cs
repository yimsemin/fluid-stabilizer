using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FluidStabilizer.Contracts;

namespace FluidStabilizer.Core.Sim
{
    // Public config fields only: no Unity serializer, properties or code-default dependency on replay.
    public static class ConfigSnapshot
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
        public static SortedDictionary<string, string> Capture(SimulationConfig config)
        {
            var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
            Walk(config, "", result);
            return result;
        }

        private static void Walk(object obj, string prefix, IDictionary<string, string> values)
        {
            foreach (var f in obj.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                string key = prefix + f.Name;
                object v = f.GetValue(obj);
                if (v is Double3 d)
                    values[key] = d.X.ToString("R", Ic) + "|" + d.Y.ToString("R", Ic) + "|" + d.Z.ToString("R", Ic);
                else if (f.FieldType.IsPrimitive)
                    values[key] = v is double n ? n.ToString("R", Ic) : Convert.ToString(v, Ic);
                else Walk(v, key + ".", values);
            }
        }

        public static SimulationConfig Restore(IReadOnlyDictionary<string, string> values)
        {
            var config = new SimulationConfig();
            foreach (var entry in values)
            {
                object target = config;
                string[] path = entry.Key.Split('.');
                for (int i = 0; i < path.Length; i++)
                {
                    var f = target.GetType().GetField(path[i], BindingFlags.Instance | BindingFlags.Public);
                    if (f == null) throw new ArgumentException("Unknown config field: " + entry.Key);
                    if (i < path.Length - 1) { target = f.GetValue(target); continue; }
                    object value;
                    if (f.FieldType == typeof(Double3))
                    {
                        var xyz = entry.Value.Split('|');
                        value = new Double3(double.Parse(xyz[0], Ic), double.Parse(xyz[1], Ic), double.Parse(xyz[2], Ic));
                    }
                    else value = Convert.ChangeType(entry.Value, f.FieldType, Ic);
                    if (value is double n && (double.IsNaN(n) || double.IsInfinity(n)))
                        throw new ArgumentException("Non-finite config: " + entry.Key);
                    f.SetValue(target, value);
                }
            }
            config.Validate();
            return config;
        }

        public static string Encode(SimulationConfig c) => string.Join(";", Capture(c).Select(kv => kv.Key + "=" + kv.Value));
        public static string Hash(string text)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
        public static string FileHash(string path)
        {
            using var stream = System.IO.File.OpenRead(path);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
