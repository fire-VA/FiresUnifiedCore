// The switch for periodic diagnostic lines ([Debug] PeriodicDiagnostics, default OFF).
namespace FiresCore.Logging
{
    public static class Diag
    {
        /// <summary>True when periodic timing / census lines may be logged (they also need the Core log level at Debug).</summary>
        public static bool Periodic;
    }
}