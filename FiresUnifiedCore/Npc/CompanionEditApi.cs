using System;
using System.Globalization;
using System.Text;
using FiresCore.Npc.Archetypes;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Studio God panel companion editor (Fire 10-30: "raise or lower even vanilla skills, raise or lower companion skills...
    /// full adjustment over the character for testing"). Add-only, string in / JSON out, for FGC reflection. Runs on the
    /// machine that OWNS the companion's ZDO (the owning player's client); anywhere else it refuses ("not owner").
    /// Fire 10-30: "There are no limits ... We are a god." Every field takes ANY value, up or down, and is saved, through the
    /// companion's own setters (CompanionProgression.SetLevel / SetXp / SetAttributeValue / SetUnspentAttributePoints,
    /// CompanionSkills.SetSkillLevel, ArchetypeSkillSystem.SetSkillLevel, ArchetypeController.SetArchetypeForced), which save their own ZDO keys.
    /// Companion = ZDOID string "userId:id".
    ///   Get(zdoid)               -> {"ok":true,"name","archetype","level","xp","xpNext","points","attributes":{..},"skills":{..},
    ///                                "archetypeSkills":{"Skill":{"level","xp"}},"hp","maxHp","stamina","maxStamina","eitr","maxEitr"}
    ///   Set(zdoid, field, value) -> {"ok":true,"field","old","new","state":{Get}} or {"ok":false,"refused":"why"}
    ///     field: "level" | "xp" | "points" (unspent) | "archetype" (ArchetypeClass name) | "attr:Strength" (Speed/Health/Endurance/Intelligence) | "skill:Swords" (vanilla
    ///     Skills.SkillType) | "arch:Tank_Taunt" (ArchetypeSkill). "old" is the value before, so the caller can undo with Set(old).
    /// </summary>
    public static class CompanionEditApi
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Get(string zdoid)
        {
            var c = Resolve(zdoid, out string why);
            return c == null ? Refuse(why) : State(c);
        }

        public static string Set(string zdoid, string field, string value)
        {
            var c = Resolve(zdoid, out string why);
            if (c == null) return Refuse(why);
            if (string.IsNullOrEmpty(field)) return Refuse("no field");
            string old, now;
            if (field == "archetype")
            {
                // ArchetypeController.SetArchetypeForced: the radial menu's own saved override key, held whatever the gear and
                // after a reload; "None" clears it (the equipment decides again)
                var ac = c.GetArchetypeController();
                if (ac == null) return Refuse("no ArchetypeController");
                if (!Enum.TryParse(value, true, out ArchetypeClass want)) return Refuse("unknown archetype " + value);
                old = Q(ac.CurrentArchetypeClass.ToString());
                if (!ac.SetArchetypeForced(want, out why)) return Refuse(why);
                now = Q(ac.CurrentArchetypeClass.ToString());
                FiresLogger.LogDebug($"[CompanionEditApi] {c.companionName}: archetype {old} -> {now} ({why})");
                return "{\"ok\":true,\"field\":\"archetype\",\"old\":" + old + ",\"new\":" + now + ",\"state\":" + State(c) + "}";
            }
            if (!float.TryParse(value, NumberStyles.Float, Inv, out float v)) return Refuse("value is not a number: " + value);
            try
            {
                if (!Apply(c, field, v, out old, out now, out why)) return Refuse(why);
            }
            catch (Exception ex) { return Refuse(field + " threw: " + ex.Message); }
            FiresLogger.LogDebug($"[CompanionEditApi] {c.companionName}: {field} {old} -> {now}");
            return "{\"ok\":true,\"field\":" + Q(field) + ",\"old\":" + old + ",\"new\":" + now + ",\"state\":" + State(c) + "}";
        }

        private static bool Apply(CompanionController c, string field, float v, out string old, out string now, out string why)
        {
            old = now = "0"; why = "";
            var prog = c.GetComponent<CompanionProgression>();
            int sep = field.IndexOf(':');
            string kind = sep < 0 ? field : field.Substring(0, sep), name = sep < 0 ? "" : field.Substring(sep + 1);
            switch (kind)
            {
                case "level":
                    if (prog == null) { why = "no CompanionProgression"; return false; }
                    old = I(prog.Level); prog.SetLevel(Mathf.RoundToInt(v)); now = I(prog.Level); return true;
                case "xp":
                    if (prog == null) { why = "no CompanionProgression"; return false; }
                    old = F(prog.CurrentXp); prog.SetXp(v); now = F(prog.CurrentXp); return true;
                case "points":
                    if (prog == null) { why = "no CompanionProgression"; return false; }
                    old = I(prog.UnspentAttributePoints); prog.SetUnspentAttributePoints(Mathf.RoundToInt(v)); now = I(prog.UnspentAttributePoints); return true;
                case "attr":
                {
                    if (prog == null) { why = "no CompanionProgression"; return false; }
                    if (!Enum.TryParse(name, true, out CompanionProgression.AttributeType t)) { why = "unknown attribute " + name; return false; }
                    old = I(prog.GetAttributeValue(t)); prog.SetAttributeValue(t, Mathf.RoundToInt(v)); now = I(prog.GetAttributeValue(t)); return true;
                }
                case "skill":
                {
                    var sk = c.GetComponent<CompanionSkills>();
                    if (sk == null) { why = "no CompanionSkills"; return false; }
                    if (!Enum.TryParse(name, true, out Skills.SkillType t)) { why = "unknown skill " + name; return false; }
                    old = F(sk.GetSkillLevel(t)); sk.SetSkillLevel(t, v); now = F(sk.GetSkillLevel(t)); return true;
                }
                case "arch":
                {
                    var ss = c.GetComponent<ArchetypeSkillSystem>();
                    if (ss == null) { why = "no ArchetypeSkillSystem"; return false; }
                    if (!Enum.TryParse(name, true, out ArchetypeSkillSystem.ArchetypeSkill t)) { why = "unknown archetype skill " + name; return false; }
                    old = I(ss.GetSkillLevel(t));
                    if (!ss.SetSkillLevel(t, Mathf.RoundToInt(v))) { why = name + " is not tracked on this companion"; return false; }
                    now = I(ss.GetSkillLevel(t)); return true;
                }
            }
            why = "unknown field " + field + " (level, xp, attr:<Name>, skill:<SkillType>, arch:<Skill>)";
            return false;
        }

        private static string State(CompanionController c)
        {
            var sb = new StringBuilder(2048);
            sb.Append("{\"ok\":true,\"name\":").Append(Q(c.companionName));
            var ac = c.GetArchetypeController();
            var cls = ac != null ? ac.CurrentArchetypeClass : ArchetypeClass.None;
            sb.Append(",\"archetype\":").Append(Q(cls.ToString())).Append(",\"archetypeName\":").Append(Q(ac != null && cls != ArchetypeClass.None ? ac.GetArchetypeDisplayName() : ""));
            var prog = c.GetComponent<CompanionProgression>();
            if (prog != null)
            {
                sb.Append(",\"level\":").Append(I(prog.Level)).Append(",\"maxLevel\":").Append(I(prog.maxLevel))
                  .Append(",\"xp\":").Append(F(prog.CurrentXp)).Append(",\"xpNext\":").Append(F(prog.XpForNextLevel))
                  .Append(",\"points\":").Append(I(prog.UnspentAttributePoints)).Append(",\"attributes\":{");
                bool first = true;
                foreach (CompanionProgression.AttributeType t in Enum.GetValues(typeof(CompanionProgression.AttributeType)))
                { if (!first) sb.Append(','); first = false; sb.Append(Q(t.ToString())).Append(':').Append(I(prog.GetAttributeValue(t))); }
                sb.Append('}');
            }
            var sk = c.GetComponent<CompanionSkills>();
            if (sk != null)
            {
                sb.Append(",\"skills\":{");
                bool first = true;
                foreach (var kv in sk.GetAllSkills())
                { if (!first) sb.Append(','); first = false; sb.Append(Q(kv.Key.ToString())).Append(':').Append(F(kv.Value)); }
                sb.Append('}');
            }
            var ss = c.GetComponent<ArchetypeSkillSystem>();
            if (ss != null && cls != ArchetypeClass.None)
            {
                sb.Append(",\"archetypeSkills\":{");
                bool first = true;
                foreach (var d in ss.GetSkillsForArchetype(cls.ToString()))
                { if (!first) sb.Append(','); first = false; sb.Append(Q(d.Skill.ToString())).Append(":{\"level\":").Append(I(d.Level)).Append(",\"xp\":").Append(F(d.CurrentXP)).Append('}'); }
                sb.Append('}');
            }
            var st = c.GetComponent<CompanionStats>();
            if (st != null)
                sb.Append(",\"hp\":").Append(F(st.CurrentHealth)).Append(",\"maxHp\":").Append(F(st.MaxHealth))
                  .Append(",\"stamina\":").Append(F(st.CurrentStamina)).Append(",\"maxStamina\":").Append(F(st.MaxStamina))
                  .Append(",\"eitr\":").Append(F(st.CurrentEitr)).Append(",\"maxEitr\":").Append(F(st.MaxEitr));
            return sb.Append('}').ToString();
        }

        private static CompanionController Resolve(string zdoid, out string why)
        {
            why = "";
            if (ZNetScene.instance == null) { why = "no world loaded"; return null; }
            int sep = zdoid != null ? zdoid.IndexOf(':') : -1;
            if (sep < 0 || !long.TryParse(zdoid.Substring(0, sep), NumberStyles.Integer, Inv, out long user)
                || !uint.TryParse(zdoid.Substring(sep + 1), NumberStyles.Integer, Inv, out uint id)) { why = "bad zdoid '" + zdoid + "' (userId:id)"; return null; }
            var go = ZNetScene.instance.FindInstance(new ZDOID(user, id));
            var c = go != null ? go.GetComponent<CompanionController>() : null;
            if (c == null) { why = go == null ? "not loaded here" : "not a companion"; return null; }
            var nv = go.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid() || !nv.IsOwner()) { why = "not owner"; return null; }
            return c;
        }

        private static string Refuse(string why)
        {
            FiresLogger.LogDebug("[CompanionEditApi] refused: " + why);
            return "{\"ok\":false,\"refused\":" + Q(why) + "}";
        }

        private static string I(int v) => v.ToString(Inv);
        private static string F(float v) => v.ToString("0.###", Inv);
        private static string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
