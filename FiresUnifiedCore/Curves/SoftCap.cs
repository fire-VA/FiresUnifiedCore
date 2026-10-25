namespace FiresCore.Curves
{
    /// <summary>
    /// The ONE soft curve RPGClasses, FRC/FLO and FMO share.
    /// Public, add-only contract. Both curves are the identity up to <c>knee</c> and leave it with slope 1, so a value that never passes
    /// the knee is unchanged.
    /// </summary>
    public static class SoftCap
    {
        /// <summary>Grows forever, ever slower: raw up to knee, then knee + knee * ln(1 + (raw - knee) / knee).</summary>
        public static float Soft(float raw, float knee)
        {
            if (knee <= 0f || raw <= knee) return raw;
            return knee + knee * (float)System.Math.Log(1.0 + (raw - knee) / knee);
        }

        /// <summary>Approaches <paramref name="limit"/>: raw up to knee, then limit - (limit - knee) * exp(-(raw - knee) / (limit - knee)).</summary>
        public static float Asymptote(float raw, float knee, float limit)
        {
            if (raw <= knee || limit <= knee) return raw <= knee ? raw : knee;
            float span = limit - knee;
            return limit - span * (float)System.Math.Exp(-(raw - knee) / span);
        }
    }
}
