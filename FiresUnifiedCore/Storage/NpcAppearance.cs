using System.Globalization;

namespace FiresCore.Storage
{
    /// <summary>
    /// What an NPC looks like, for
    /// <see cref="UI.UIMannequinPreview.SetNpcAppearance"/>. A human NPC is a <see cref="PlayerAppearance"/> plus its model scale
    /// and dyes; a creature NPC names its prefab in <see cref="ModelOverridePrefab"/>. Contract: add-only.
    /// </summary>
    public class NpcAppearance
    {
        /// <summary>Model, colours, hair/beard and every equip slot (+ variants), as a human StaticNpc wears them.</summary>
        public PlayerAppearance Base = new PlayerAppearance();

        /// <summary>The NPC's model scale (m_visual localScale), 1 = default.</summary>
        public float Scale = 1f;

        /// <summary>"" = the companion human body (the PlayerAppearance path); else a prefab name, built as a stripped visual-only clone.</summary>
        public string ModelOverridePrefab = "";

        /// <summary>The item dye block per slot as FiresDyesApi stores it ("" = none); applied after dressing when FiresDyes is loaded.</summary>
        public string DyeJson = "";

        /// <summary>Base signature + Scale (2 dp) + ModelOverridePrefab + DyeJson, stable-hashed. The preview rebuilds only when it changes.</summary>
        public int GetSignature() => string.Join("|", new[]
        {
            (Base ?? new PlayerAppearance()).GetSignatureString(),
            Scale.ToString("0.00", CultureInfo.InvariantCulture),
            ModelOverridePrefab ?? "",
            DyeJson ?? "",
        }).GetStableHashCode();
    }
}
