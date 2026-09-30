using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Compat
{
    internal enum HdTextureSize { Full, Half, Quarter }

    // Fire (16:5x popup: "Add Full/Half/Quarter, test Half"). HDValheimTextures' ~3,000 textures are 18.7 GB, more than a
    // 16 GB card holds (R36: 18.11 GB of textures current), so the driver backs the rest with system memory. HD has no size
    // option, and its textures ignore Unity's mipmap limit (created with a default MipmapLimitDescriptor, which can't be
    // switched once they're non-readable). At Half or Quarter the spread load builds each bundle texture itself from the
    // entry's stored mip chain minus its top one or two levels: a quarter or a sixteenth of the memory, and of the disk read.
    // At Full nothing here runs and HD's own loader does every texture, exactly as before. Half is the default (Fire, 18:1x
    // popup, after R38 Half: 27 s menu load vs 60 s, 24.5 GB private in world vs 37; R40 Full paged hard on the 31 GB rig).
    //
    // Written from the data, not from HD's code: an entry holds Unity's raw DXT5 chain for a Width x Height texture (every
    // level, largest first, each level's side halved and clamped at 1, stored in 4x4 blocks of 16 bytes). Its colour space is
    // the entry's Linear flag inverted, which is how the textures look in game today. Dropping the top levels leaves exactly
    // the chain of a texture half (or a quarter) the size, so that tail is read and uploaded as the whole of a smaller texture.
    // An entry this can't read that way (not a power of two, a chain of another length) goes to HD's loader unchanged.
    internal static class HdTexturesSize
    {
        private const string Section = "HD Textures";
        private const string Key = "Size";
        private const string Description =
            "Resolution HDValheimTextures' textures load at on this machine (not synced). Full = as HD ships them. Half = each side " +
            "halved, a quarter of the memory. Quarter = a sixteenth. Takes effect at the next game start.";
        private const int BlockSide = 4;
        private const int BlockBytes = 16;
        private const int Anisotropy = 16;

        internal static ConfigEntry<HdTextureSize> Size;

        private static PropertyInfo s_name, s_width, s_height, s_linear, s_offset, s_length;
        private static MethodInfo s_readBytes;

        // Levels dropped from the top of every chain: Full 0, Half 1, Quarter 2.
        internal static int Drop => Size == null ? 0 : (int)Size.Value;

        internal static void Bind(ConfigFile config) => Size = config.Bind(Section, Key, HdTextureSize.Half, Description);

        // An entry's texture name, for the finish line's slowest texture.
        internal static string NameOf(object entry) => s_name?.GetValue(entry) as string;

        // The entry's members and the stream's ranged read; the first one missing is returned.
        internal static string Resolve(Type entry, Type stream)
        {
            if (entry == null) return "CBDirectoryItem";
            s_name = AccessTools.Property(entry, "Name");
            s_width = AccessTools.Property(entry, "Width");
            s_height = AccessTools.Property(entry, "Height");
            s_linear = AccessTools.Property(entry, "Linear");
            s_offset = AccessTools.Property(entry, "Offset");
            s_length = AccessTools.Property(entry, "Length");
            s_readBytes = AccessTools.Method(stream, "ReadBytes", new[] { typeof(long), typeof(int) });
            var members = new (string name, object found)[]
            {
                ("CBDirectoryItem.Name", s_name), ("CBDirectoryItem.Width", s_width), ("CBDirectoryItem.Height", s_height),
                ("CBDirectoryItem.Linear", s_linear), ("CBDirectoryItem.Offset", s_offset), ("CBDirectoryItem.Length", s_length),
                ("BundleStream.ReadBytes", s_readBytes),
            };
            return members.FirstOrDefault(m => m.found == null).name;
        }

        // True when it built the texture into HD's own set; false hands the entry to HD's loader.
        internal static bool TryLoad(object entry, object stream, IDictionary textures)
        {
            int width = (int)s_width.GetValue(entry);
            int height = (int)s_height.GetValue(entry);
            if (!Mathf.IsPowerOfTwo(width) || !Mathf.IsPowerOfTwo(height)) return false;
            int levels = Levels(width, height);
            int drop = Math.Min(Drop, levels - 1);
            while (drop > 0 && Math.Min(width, height) >> drop < BlockSide) drop--;
            if (drop == 0) return false;

            long skipped = 0;
            for (int level = 0; level < drop; level++) skipped += LevelBytes(width, height, level);
            long kept = 0;
            for (int level = drop; level < levels; level++) kept += LevelBytes(width, height, level);
            long length = (long)s_length.GetValue(entry);
            if (skipped + kept != length) return false;

            var data = (byte[])s_readBytes.Invoke(stream, new object[] { (long)s_offset.GetValue(entry) + skipped, (int)kept });
            var texture = new Texture2D(width >> drop, height >> drop, TextureFormat.DXT5, levels - drop, !(bool)s_linear.GetValue(entry));
            try
            {
                texture.LoadRawTextureData(data);
                texture.anisoLevel = Anisotropy;
                texture.filterMode = FilterMode.Trilinear;
                texture.Apply(false, true);
            }
            catch
            {
                UnityEngine.Object.Destroy(texture);
                throw;
            }
            string name = (string)s_name.GetValue(entry);
            texture.name = name;
            textures[name] = texture;
            return true;
        }

        // A full chain: levels until the larger side reaches 1 (counted by halving, not a float log, which can land just short).
        private static int Levels(int width, int height)
        {
            int levels = 1;
            for (int side = Math.Max(width, height); side > 1; side >>= 1) levels++;
            return levels;
        }

        private static long LevelBytes(int width, int height, int level)
        {
            long blocksX = (Math.Max(1, width >> level) + BlockSide - 1) / BlockSide;
            long blocksY = (Math.Max(1, height >> level) + BlockSide - 1) / BlockSide;
            return blocksX * blocksY * BlockBytes;
        }
    }
}
