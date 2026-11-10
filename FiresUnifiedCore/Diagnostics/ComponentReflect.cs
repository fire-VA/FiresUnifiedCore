// ComponentReflect ([visual] 10-30 drop-in for Core, lead: "the shared core goes in CORE; Core = the one implementation").
// LIFTED AS-IS from FDT's Inspector/ComponentInspector/FiresComponentInspectorWindow.cs (Member, Members, SkipProps, IsNumeric,
// TryParseNum, TryParseColor) so FDT's component window and FGC's /v1/components + component.set read and write components the SAME way.
// Added (no behaviour change to the lifted part): Kind (the panel's type word), Format / Parse (one string form both callers share),
// Describe (a component's members as plain rows) and Set (parse + write + old value). Read-only rule = the inspector's (no setter).
// Public, add-only, no Unity UI code. Owner after the freeze: [wishbone] (Core); callers: FDT (window), FGC (endpoints).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace FiresCore.Diagnostics
{
    public static class ComponentReflect
    {
        // ---------------------------------------------------------------- member reflection (fields + properties) - lifted
        public sealed class Member
        {
            public string Name;
            public Type Type;
            public Func<object, object> Get;
            public Action<object, object> Set;   // null = read-only
        }

        private static readonly Dictionary<Type, Member[]> _memberCache = new Dictionary<Type, Member[]>();

        // Property getters that INSTANTIATE on read (leaking material/mesh copies) - use the shared* variants.
        private static readonly HashSet<string> SkipProps = new HashSet<string>(StringComparer.Ordinal)
        { "material", "materials", "mesh" };

        public static Member[] Members(Type t)
        {
            if (_memberCache.TryGetValue(t, out var cached)) return cached;
            var list = new List<Member>();

            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var ff = f;
                list.Add(new Member { Name = ff.Name, Type = ff.FieldType, Get = ff.GetValue, Set = ff.SetValue });
            }

            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                if (SkipProps.Contains(p.Name)) continue;
                if (p.GetGetMethod() == null) continue;
                if (Attribute.IsDefined(p, typeof(ObsoleteAttribute), true)) continue;
                var pp = p;
                Action<object, object> set = null;
                if (pp.CanWrite && pp.GetSetMethod() != null) set = (c, v) => pp.SetValue(c, v, null);
                list.Add(new Member { Name = pp.Name, Type = pp.PropertyType, Get = c => pp.GetValue(c, null), Set = set });
            }

            var arr = list.ToArray();
            _memberCache[t] = arr;
            return arr;
        }

        public static bool IsNumeric(Type t)
            => t == typeof(int) || t == typeof(float) || t == typeof(double) || t == typeof(long)
            || t == typeof(short) || t == typeof(byte) || t == typeof(uint) || t == typeof(ushort);

        public static bool TryParseNum(string s, Type t, out object value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (t == typeof(float) || t == typeof(double))
            {
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return false;
                value = Convert.ChangeType(d, t); return true;
            }
            if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return false;
            try { value = Convert.ChangeType(l, t); return true; } catch { return false; }
        }

        public static bool TryParseColor(string s, out Color c)
        {
            c = Color.white;
            var p = s.Split(',');
            if (p.Length < 3) return false;
            if (!float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r)) return false;
            if (!float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g)) return false;
            if (!float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b)) return false;
            float a = 1f;
            if (p.Length >= 4) float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out a);
            c = new Color(r, g, b, a); return true;
        }

        // ---------------------------------------------------------------- added: one string form for both callers
        /// <summary>The panel's type word: float | int | bool | string | vector3 | color | enum | object.</summary>
        public static string Kind(Type t)
        {
            if (t == typeof(float) || t == typeof(double)) return "float";
            if (IsNumeric(t)) return "int";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(string)) return "string";
            if (t == typeof(Vector3)) return "vector3";
            if (t == typeof(Color)) return "color";
            if (t.IsEnum) return "enum";
            return "object";
        }

        /// <summary>A value as the one string both the window and the Studio show / send back ("x,y,z", "r,g,b,a", enum name ...).</summary>
        public static string Format(object val)
        {
            switch (val)
            {
                case null: return "null";
                case UnityEngine.Object o: return o != null ? o.name : "null";
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case bool b: return b ? "true" : "false";
                case Vector3 v: return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", v.x, v.y, v.z);
                case Color c: return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", c.r, c.g, c.b, c.a);
                case IFormattable fm: return fm.ToString(null, CultureInfo.InvariantCulture);
                default: return val.ToString();
            }
        }

        /// <summary>Parse the shared string form into t. Only the editable kinds (not "object").</summary>
        public static bool TryParse(string s, Type t, out object value)
        {
            value = null; if (s == null) return false;
            if (IsNumeric(t)) return TryParseNum(s, t, out value);
            if (t == typeof(bool)) { if (bool.TryParse(s, out bool b)) { value = b; return true; } return false; }
            if (t == typeof(string)) { value = s; return true; }
            if (t == typeof(Color)) { if (TryParseColor(s, out Color c)) { value = c; return true; } return false; }
            if (t == typeof(Vector3))
            {
                var p = s.Split(',');
                if (p.Length == 3 && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                { value = new Vector3(x, y, z); return true; }
                return false;
            }
            if (t.IsEnum) { try { value = Enum.Parse(t, s, true); return true; } catch { return false; } }
            return false;
        }

        // ---------------------------------------------------------------- hierarchy (Fire: "open up the inspector hierarchy and component panel")
        // The same walk as FDT's FiresHierarchyWindow.DrawNode (every child in order, activeSelf = the "(off)" mark), as data: paths are
        // relative to the root like Transform.Find ("" = the root). Capped so a huge prefab can't stall a frame.
        public sealed class Node { public string Name, Path; public bool Active; public List<string> Components = new List<string>(); public List<Node> Children = new List<Node>(); }

        public static Node Tree(Transform root, int maxNodes = 2000)
        {
            int left = maxNodes;
            Node Walk(Transform t, string path)
            {
                var n = new Node { Name = t.gameObject.name, Path = path, Active = t.gameObject.activeSelf };
                foreach (var c in t.GetComponents<Component>()) if (c != null && !(c is Transform)) n.Components.Add(c.GetType().Name);
                for (int i = 0; i < t.childCount && --left > 0; i++)
                {
                    var ch = t.GetChild(i);
                    n.Children.Add(Walk(ch, path.Length == 0 ? ch.name : path + "/" + ch.name));
                }
                return n;
            }
            return root == null ? null : Walk(root, "");
        }

        /// <summary>The child at a Tree path ("" = root); Transform.Find, which also sees inactive children.</summary>
        public static Transform Find(Transform root, string path) => root == null ? null : string.IsNullOrEmpty(path) ? root : root.Find(path);

        public sealed class Row { public string Name, Kind, Value; public bool ReadOnly; public string[] Options; }

        /// <summary>A component's members as rows (the inspector's member list). A getter that throws shows "(error)".</summary>
        public static List<Row> Describe(Component comp)
        {
            var rows = new List<Row>();
            if (comp == null) return rows;
            foreach (var m in Members(comp.GetType()))
            {
                string val; try { val = Format(m.Get(comp)); } catch { val = "(error)"; }
                string kind = Kind(m.Type);
                rows.Add(new Row { Name = m.Name, Kind = kind, Value = val, ReadOnly = m.Set == null || kind == "object", Options = m.Type.IsEnum ? Enum.GetNames(m.Type) : null });
            }
            return rows;
        }

        /// <summary>Set one member from the shared string form; old = the value before (string form). False + why on refusal.</summary>
        public static bool Set(Component comp, string member, string value, out string old, out string why)
        {
            old = null; why = null;
            if (comp == null) { why = "no component"; return false; }
            Member m = null; foreach (var x in Members(comp.GetType())) if (x.Name == member) { m = x; break; }
            if (m == null) { why = "no member " + member; return false; }
            if (m.Set == null || Kind(m.Type) == "object") { why = member + " is read-only"; return false; }
            if (!TryParse(value, m.Type, out object v)) { why = "cannot read '" + value + "' as " + Kind(m.Type); return false; }
            try { old = Format(m.Get(comp)); m.Set(comp, v); return true; }
            catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; return false; }
        }
    }
}
