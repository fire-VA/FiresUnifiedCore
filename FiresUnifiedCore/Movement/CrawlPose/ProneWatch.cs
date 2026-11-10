using UnityEngine;

namespace FiresCore.Movement.CrawlPose
{
    /// <summary>
    /// 0.2.1190 (Fire 11-06: Core holds the crawl API, APM decides; lead: one "prone changed" event whoever draws the crawl): on every peer's
    /// copy of each player, turns a flip of the synced prone flag (Core's FiresCore_Prone or VikHavn's VikHavnCombat_Prone, read only) into
    /// <see cref="CrawlPose.ProneChanged"/>. VikHavn is never patched or referenced; Core learns VikHavn's own prone (its Crouch hold) from
    /// the ZDO it already syncs. Cost: one data-revision compare a frame per loaded player; the two flags are read only after the ZDO changed.
    /// </summary>
    internal sealed class ProneWatch : MonoBehaviour
    {
        private Player _player;
        private ZNetView _view;
        private ZDO _zdo;
        private uint _rev;
        private bool _prone, _seen;

        private void Awake() { _player = GetComponent<Player>(); _view = GetComponent<ZNetView>(); }

        private void Update()
        {
            if (_player == null) return;
            // lead 11-06: read the two flags only when the ZDO's data changed (one uint compare a frame otherwise)
            ZDO zdo = _view != null ? _view.GetZDO() : CrawlPose.ZdoOf(_player);
            if (zdo == null) return;
            if (zdo == _zdo && zdo.DataRevision == _rev) return;
            _zdo = zdo; _rev = zdo.DataRevision;
            bool now = CrawlPose.IsProne(_player);
            if (!_seen) { _seen = true; _prone = now; return; }   // first frame: the starting state, no event
            if (now == _prone) return;
            _prone = now;
            CrawlPose.RaiseProneChanged(_player, now);
        }
    }
}
