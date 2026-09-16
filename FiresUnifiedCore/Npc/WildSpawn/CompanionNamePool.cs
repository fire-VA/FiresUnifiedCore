using System;

namespace FiresCore.Npc.WildSpawn
{
    /// <summary>
    /// Hand-authored name pools per <see cref="CompanionFaction"/>. The
    /// dresser picks one deterministically using the ZDO-seeded RNG so a
    /// given wild companion keeps their name across reloads.
    /// </summary>
    internal static class CompanionNamePool
    {
        public static string Roll(CompanionFaction faction, Random rng)
        {
            string[] pool = GetPool(faction);
            if (pool == null || pool.Length == 0) return "Wanderer";
            return pool[rng.Next(pool.Length)];
        }

        private static string[] GetPool(CompanionFaction faction)
        {
            switch (faction)
            {
                case CompanionFaction.Neutral: return _neutral;
                case CompanionFaction.Bandit:  return _bandit;
                case CompanionFaction.Cultist: return _cultist;
                default:                       return _neutral;
            }
        }

        // Neutral: pastoral / Norse, recruitable wanderers.
        private static readonly string[] _neutral = new[]
        {
            "Astrid",  "Bjorn",    "Eira",     "Finnr",    "Gunna",    "Halvor",
            "Ingrid",  "Jarl",     "Kari",     "Leif",     "Magnus",   "Nanna",
            "Olaf",    "Ragnhild", "Sigrun",   "Thora",    "Ulf",      "Vali",
            "Wulf",    "Yrsa",     "Aesa",     "Brand",    "Cedric",   "Dagny",
            "Eyvind",  "Freyja",   "Gudrun",   "Haakon",   "Ivar",     "Jorunn",
            "Ketill",  "Liv",      "Mikael",   "Njal",     "Oddr",     "Ragna",
            "Snorri",  "Tyra",     "Uffe",     "Vanya",    "Birger",   "Estrid",
            "Floki",   "Gest",     "Heidrun",  "Ivarr",    "Lofn",     "Ormr",
            "Steinar", "Torvald"
        };

        // Bandit: harsh / guttural, hostile raiders.
        private static readonly string[] _bandit = new[]
        {
            "Grimjaw",    "Skullsplit", "Blackmaw",  "Redhand",    "Boneripper",
            "Ashfang",    "Sootbeard",  "Ironjaw",   "Cragg",      "Thresh",
            "Gorm",       "Hrolf",      "Kraaken",   "Vulg",       "Brak",
            "Rask",       "Skarn",      "Urd",       "Varg",       "Ygg",
            "Knar",       "Flay",       "Sneer",     "Spite",      "Groth",
            "Morg",       "Durn",       "Crag",      "Hask",       "Pyke",
            "Skor",       "Rook",       "Gutt",      "Bonn",       "Tuskr",
            "Rend",       "Slash",      "Vex",       "Grell",      "Drog",
            "Brigg",      "Hagr",       "Morrok",    "Snarl",      "Throg",
            "Vorn",       "Wurm",       "Xok",       "Ymir",       "Zarn"
        };

        // Cultist: archaic / occult, reserved for future content.
        private static readonly string[] _cultist = new[]
        {
            "Azareth",    "Balthys",   "Corven",    "Daemorn",   "Ephrian",
            "Fenriss",    "Gormath",   "Hethras",   "Isolde",    "Jorvath",
            "Kaldris",    "Lysarn",    "Morgaeth",  "Nyxandra",  "Orsolai",
            "Pyrrhus",    "Quorath",   "Rhovanion", "Sibyll",    "Thessra",
            "Ulfgar",     "Varryn",    "Wessenar",  "Xylandra",  "Yrminne",
            "Zarethos",   "Aelfric",   "Brynhild",  "Cassimir",  "Drevanth",
            "Ebonmere",   "Faelinn",   "Gaunther",  "Hespera",   "Iorveth",
            "Jassamyn",   "Kelthren",  "Liliath",   "Mordain",   "Nessarr",
            "Oakenveil",  "Primavis",  "Quentris",  "Raevolur",  "Sonnerai",
            "Thyrmond",   "Ulwyss",    "Velmeri",   "Wyncarr",   "Xaermith"
        };
    }
}
