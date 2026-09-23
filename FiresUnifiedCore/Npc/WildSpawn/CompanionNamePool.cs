using System;
using System.Collections.Generic;

namespace FiresCore.Npc.WildSpawn
{
    /// <summary>
    /// Every companion name, split by gender. The gender is rolled first and the name drawn from that gender's pool,
    /// so the body always matches the name; <see cref="IsFemaleName"/> answers from these same pools for names that
    /// were assigned before (saved companions, static NPCs).
    /// </summary>
    internal static class CompanionNamePool
    {
        public static string RollFaction(CompanionFaction faction, bool isFemale, Random rng)
        {
            string[] pool = FactionPool(faction, isFemale);
            return pool[rng.Next(pool.Length)];
        }

        public static string RollViking(bool isGiant, bool isDwarf, bool isFemale, Func<int, int> next)
        {
            string[] names;
            string[] epithets;
            if (isGiant) { names = isFemale ? GiantFemale : GiantMale; epithets = GiantEpithets; }
            else if (isDwarf) { names = isFemale ? DwarfFemale : DwarfMale; epithets = DwarfEpithets; }
            else { names = isFemale ? VikingFemale : VikingMale; epithets = StandardEpithets; }

            string name = names[next(names.Length)];
            string epithet = epithets[next(epithets.Length)];
            return string.IsNullOrEmpty(epithet) ? name : $"{name} {epithet}";
        }

        /// <summary>True when the first word of <paramref name="name"/> is in any female pool.</summary>
        public static bool IsFemaleName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return FemaleNames.Contains(name.Split(' ')[0]);
        }

        private static string[] FactionPool(CompanionFaction faction, bool isFemale)
        {
            switch (faction)
            {
                case CompanionFaction.Bandit:  return Bandit;
                case CompanionFaction.Cultist: return isFemale ? CultistFemale : CultistMale;
                default:                       return isFemale ? NeutralFemale : NeutralMale;
            }
        }

        // Neutral: pastoral / Norse, recruitable wanderers.
        private static readonly string[] NeutralMale =
        {
            "Bjorn", "Finnr", "Halvor", "Jarl", "Leif", "Magnus", "Olaf", "Ulf", "Vali", "Wulf", "Brand", "Cedric",
            "Eyvind", "Haakon", "Ivar", "Ketill", "Mikael", "Njal", "Oddr", "Snorri", "Uffe", "Vanya", "Birger",
            "Floki", "Gest", "Ivarr", "Ormr", "Steinar", "Torvald"
        };

        private static readonly string[] NeutralFemale =
        {
            "Astrid", "Eira", "Gunna", "Ingrid", "Kari", "Nanna", "Ragnhild", "Sigrun", "Thora", "Yrsa", "Aesa",
            "Dagny", "Freyja", "Gudrun", "Jorunn", "Liv", "Ragna", "Tyra", "Estrid", "Heidrun", "Lofn"
        };

        // Bandit: harsh nicknames that fit either gender.
        private static readonly string[] Bandit =
        {
            "Grimjaw", "Skullsplit", "Blackmaw", "Redhand", "Boneripper", "Ashfang", "Sootbeard", "Ironjaw", "Cragg",
            "Thresh", "Gorm", "Hrolf", "Kraaken", "Vulg", "Brak", "Rask", "Skarn", "Urd", "Varg", "Ygg", "Knar",
            "Flay", "Sneer", "Spite", "Groth", "Morg", "Durn", "Crag", "Hask", "Pyke", "Skor", "Rook", "Gutt", "Bonn",
            "Tuskr", "Rend", "Slash", "Vex", "Grell", "Drog", "Brigg", "Hagr", "Morrok", "Snarl", "Throg", "Vorn",
            "Wurm", "Xok", "Ymir", "Zarn"
        };

        // Cultist: archaic / occult, reserved for future content.
        private static readonly string[] CultistMale =
        {
            "Azareth", "Balthys", "Corven", "Daemorn", "Ephrian", "Fenriss", "Gormath", "Hethras", "Jorvath",
            "Kaldris", "Lysarn", "Morgaeth", "Pyrrhus", "Quorath", "Rhovanion", "Ulfgar", "Varryn", "Wessenar",
            "Zarethos", "Aelfric", "Cassimir", "Drevanth", "Ebonmere", "Gaunther", "Iorveth", "Kelthren", "Mordain",
            "Nessarr", "Oakenveil", "Primavis", "Quentris", "Raevolur", "Thyrmond", "Ulwyss", "Wyncarr", "Xaermith"
        };

        private static readonly string[] CultistFemale =
        {
            "Isolde", "Nyxandra", "Orsolai", "Sibyll", "Thessra", "Xylandra", "Yrminne", "Brynhild", "Faelinn",
            "Hespera", "Jassamyn", "Liliath", "Sonnerai", "Velmeri"
        };

        // Generic Viking pools for companions without a wild faction (hammer-placed, static NPCs).
        private static readonly string[] VikingMale =
        {
            "Bjorn", "Erik", "Ragnar", "Leif", "Harald", "Olaf", "Gunnar", "Ivar", "Sigurd", "Thorsten", "Ulf",
            "Vidar", "Knut", "Sven", "Magnus", "Haldor", "Asmund", "Torbjorn", "Hakon", "Rolf", "Eirik", "Fenrir",
            "Odin", "Baldr", "Freyr", "Tyr", "Bragi", "Njord", "Heimdall", "Hodr", "Vali", "Vidir", "Agnar",
            "Arnfinn", "Birger", "Dag", "Egil", "Finn", "Gorm", "Halfdan", "Ingvar", "Jarl", "Ketil", "Leifr",
            "Magni", "Njal", "Orm", "Peder"
        };

        private static readonly string[] VikingFemale =
        {
            "Astrid", "Freya", "Ingrid", "Sigrid", "Helga", "Thora", "Brynhild", "Gudrun", "Ragnhild", "Solveig",
            "Eira", "Liv", "Saga", "Ylva", "Asa", "Hilda", "Sif", "Frigg", "Idunn", "Skuld", "Verdandi", "Urd", "Ran",
            "Skadi", "Gerd", "Sigyn", "Nanna", "Eir", "Var", "Vor", "Snotra", "Fulla", "Alfhild", "Bothild", "Dagny",
            "Embla", "Gunnhild", "Hervor", "Jorunn", "Kara"
        };

        private static readonly string[] GiantMale =
        {
            "Thrym", "Skrymir", "Utgard", "Hrungnir", "Thiazi", "Ymir", "Surtr", "Mimir", "Geirrod", "Vafthrudnir",
            "Hymir", "Bergelmir", "Farbauti", "Gymir", "Bolthorn", "Hrimthurs", "Hraudung", "Gilling", "Baugi",
            "Suttung", "Thjazi", "Fjalar", "Galar", "Mokkurkalfi", "Grimnir"
        };

        private static readonly string[] GiantFemale =
        {
            "Angrboda", "Gunnlod", "Gerdr", "Grid", "Jarnsaxa", "Gjalp", "Greip", "Hyrrokkin", "Bestla", "Rind",
            "Skadi", "Gefjon", "Elli", "Fenja", "Menja", "Sinmara"
        };

        private static readonly string[] DwarfMale =
        {
            "Brokk", "Sindri", "Eitri", "Dvalin", "Durin", "Nyi", "Nordri", "Sudri", "Austri", "Vestri", "Alvis",
            "Andvari", "Fafnir", "Hreidmar", "Regin", "Otr", "Litr", "Nain", "Nidi", "Nori", "Ori", "Bifur", "Bofur",
            "Bombur", "Fili", "Kili", "Dori", "Gloin", "Thrain", "Thror", "Thorin", "Balin"
        };

        private static readonly string[] DwarfFemale =
        {
            "Disa", "Dufa", "Nott", "Dagrun", "Gullveig", "Hlif", "Hrund", "Svanhild", "Thorvi", "Vigdis", "Asny",
            "Bergdis", "Grimhild", "Oddny", "Steinunn", "Thorunn"
        };

        private static readonly string[] StandardEpithets =
        {
            "", "", "", "", "",
            "the Bold", "the Brave", "the Swift", "the Strong", "the Wise", "the Fearless", "the Wanderer",
            "the Hunter", "the Shield", "the Axe", "Ironside", "Bloodaxe", "Fairhair", "Bluetooth", "Forkbeard",
            "the Red", "the Black", "the White", "the Grey", "the Silent"
        };

        private static readonly string[] GiantEpithets =
        {
            "the Colossal", "the Mighty", "the Towering", "the Thunderous", "Mountain-Born", "the Enormous",
            "the Titanic", "Stone-Crusher", "the Immense", "World-Shaker", "the Vast", "Cliff-Strider", "the Hulking",
            "the Tremendous", "Giant-Blood"
        };

        private static readonly string[] DwarfEpithets =
        {
            "the Stout", "Iron-Forger", "Stone-Carver", "the Crafty", "Gold-Finder", "the Cunning", "Gem-Seeker",
            "the Delver", "Deep-Walker", "the Artificer", "Anvil-Born", "the Stubborn", "Ore-Master", "the Ingenious",
            "Cave-Dweller"
        };

        private static readonly HashSet<string> FemaleNames = BuildFemaleNames();

        private static HashSet<string> BuildFemaleNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pool in new[] { NeutralFemale, CultistFemale, VikingFemale, GiantFemale, DwarfFemale })
                names.UnionWith(pool);
            return names;
        }
    }
}
