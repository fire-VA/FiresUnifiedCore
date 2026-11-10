using System.Diagnostics;
using System.Globalization;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// 0.2.828 (Fire 10-26, "the FPS drops in the F1 menu are horrendous"): [wishbone]'s PerfTop shows the window open at
    /// ~105 fps and falling to ~10 fps a few seconds later while no mod's Update grows, so the time is in IMGUI or the
    /// render. Once a second while the window is open, at Core's Debug level only, one line: OnGUI ms split by Layout /
    /// Repaint / other events, events per frame, rows drawn vs culled, font atlas rebuilds (a dynamic font re-rasterising
    /// its atlas every frame costs exactly between the camera and OnGUI) and gen-0 collections.
    /// </summary>
    public partial class ConfigPanel
    {
        private readonly Stopwatch _probeClock = new Stopwatch();
        private double _probeLayoutMs, _probeRepaintMs, _probeOtherMs;
        private int _probeEvents, _probeRowsDrawn, _probeRowsCulled, _probeFrames, _probeLastFrame = -1;
        private int _probeRebuildsFrom, _probeGcFrom;
        private float _probeFrom = -1f;

        private void ProbeBegin()
        {
            if (!FiresCore.Logging.FiresLogger.DebugEnabled) { _probeFrom = -1f; return; }
            HookFontRebuilds();
            if (_probeFrom < 0f) ProbeReset();
            if (Time.frameCount != _probeLastFrame) { _probeLastFrame = Time.frameCount; _probeFrames++; }
            _probeEvents++;
            _probeClock.Restart();
        }

        private void ProbeEnd(EventType type)
        {
            if (_probeFrom < 0f) return;
            double ms = _probeClock.Elapsed.TotalMilliseconds;
            if (type == EventType.Layout) _probeLayoutMs += ms;
            else if (type == EventType.Repaint) _probeRepaintMs += ms;
            else _probeOtherMs += ms;
            float span = Time.unscaledTime - _probeFrom;
            if (span < 1f) return;
            int frames = Mathf.Max(1, _probeFrames);
            FiresConfigUI.Log.LogDebug(string.Format(CultureInfo.InvariantCulture,
                "[CfgWin] {0:0.0} fps; OnGUI {1:0.0} ms/frame (layout {2:0.0}, repaint {3:0.0}, other {4:0.0}), {5:0.0} events/frame, "
                + "rows drawn {6} culled {7} per frame, font atlas rebuilds {8}, gen0 GCs {9}, mod '{10}', advanced {11}, all mods {12}",
                frames / span, (_probeLayoutMs + _probeRepaintMs + _probeOtherMs) / frames, _probeLayoutMs / frames,
                _probeRepaintMs / frames, _probeOtherMs / frames, (float)_probeEvents / frames, _probeRowsDrawn / frames,
                _probeRowsCulled / frames, s_fontRebuilds - _probeRebuildsFrom, System.GC.CollectionCount(0) - _probeGcFrom,
                _mod, FiresConfigUI.ShowAdvanced, !CfgDiscovery.FiresOnly));
            ProbeReset();
        }

        // 0.2.832: per Repaint, the body items drawn and the ones stood in for by the two spaces.
        private void ProbeRows(int drawn, int culled)
        {
            if (_probeFrom < 0f || Event.current.type != EventType.Repaint) return;
            _probeRowsDrawn += drawn;
            _probeRowsCulled += culled;
        }

        private void ProbeReset()
        {
            _probeFrom = Time.unscaledTime;
            _probeLayoutMs = _probeRepaintMs = _probeOtherMs = 0;
            _probeEvents = _probeRowsDrawn = _probeRowsCulled = _probeFrames = 0;
            _probeRebuildsFrom = s_fontRebuilds;
            _probeGcFrom = System.GC.CollectionCount(0);
        }
    }
}
