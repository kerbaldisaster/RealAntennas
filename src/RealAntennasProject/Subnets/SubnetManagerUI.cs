// =========================
// SubnetManagerUI.cs
// =========================

using ClickThroughFix;
using EdyCommonTools;
using KSP.UI.Screens;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace RealAntennas
{
    // =========================================================================
    #region Launcher & Window bootstrap
    // =========================================================================

    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    internal class SubnetManagerLauncher : MonoBehaviour
    {
        private const string icon = "RealAntennas/dish";

        // IMPORTANT: keep this static so the UI can sync the toolbar state from anywhere
        internal static ApplicationLauncherButton Button;

        private void Awake()
        {
            GameEvents.onGUIApplicationLauncherReady.Add(OnGuiAppLauncherReady);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);
        }

        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(OnGuiAppLauncherReady);
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);

            if (Button != null && ApplicationLauncher.Instance != null)
            {
                ApplicationLauncher.Instance.RemoveModApplication(Button);
                Button = null;
            }
        }

        private void OnSceneChange(GameScenes scene) => SubnetManagerWindow.Hide();

        private void OnGuiAppLauncherReady()
        {
            if (Button != null || ApplicationLauncher.Instance == null) return;

            try
            {
                Button = ApplicationLauncher.Instance.AddModApplication(
                    () => SubnetManagerWindow.Show(),
                    () => SubnetManagerWindow.Hide(),
                    null, null, null, null,
                    ApplicationLauncher.AppScenes.ALWAYS & ~ApplicationLauncher.AppScenes.MAINMENU,
                    GameDatabase.Instance.GetTexture(icon, false)
                );
            }
            catch (Exception ex)
            {
                Debug.LogError("[SubnetManagerUI] failed to register toolbar button");
                Debug.LogException(ex);
            }
        }

        // Force the toolbar toggle to match the window visibility without re-invoking callbacks.
        internal static void SetToolbar(bool on)
        {
            if (Button == null) return;
            if (on) Button.SetTrue(false);
            else Button.SetFalse(false);
        }
    }

    internal static class SubnetManagerWindow
    {
        private static GameObject windowGO;

        public static bool IsVisible
        {
            get
            {
                var ui = windowGO != null ? windowGO.GetComponent<SubnetManagerUI>() : null;
                return ui != null && ui.Visible;
            }
        }

        public static SubnetManagerUI Acquire()
        {
            if (windowGO == null)
            {
                windowGO = new GameObject("RealAntennas.SubnetManager");
                UnityEngine.Object.DontDestroyOnLoad(windowGO);
                windowGO.AddComponent<SubnetManagerUI>();
            }
            return windowGO.GetComponent<SubnetManagerUI>();
        }

        public static void Show()
        {
            var ui = Acquire();
            ui.ResetSelection();
            ui.Visible = true;
            SubnetManagerLauncher.SetToolbar(true);
        }

        public static void Hide()
        {
            if (windowGO == null) return;
            var ui = windowGO.GetComponent<SubnetManagerUI>();
            if (ui != null) ui.Visible = false;
            SubnetManagerLauncher.SetToolbar(false);
        }
    }

    #endregion

    // =========================================================================
    #region Scenario persistence
    // =========================================================================

    [KSPScenario(ScenarioCreationOptions.AddToAllGames | ScenarioCreationOptions.AddToAllMissionGames,
        new[] { GameScenes.FLIGHT, GameScenes.TRACKSTATION, GameScenes.SPACECENTER, GameScenes.EDITOR })]
    internal class SubnetManagerScenario : ScenarioModule
    {
        public static SubnetManagerScenario Instance { get; private set; }

        private readonly Dictionary<string, uint> groundStationAntennaOverrides = new Dictionary<string, uint>();
        private readonly Dictionary<int, string> subnetNames = new Dictionary<int, string>();
        private readonly List<int> subnetPriority = new List<int>();
        private readonly Dictionary<int, Color> subnetColors = new Dictionary<int, Color>();

        public IReadOnlyDictionary<int, string> Subnets => subnetNames;
        public IReadOnlyList<int> SubnetPriority => subnetPriority;
        public bool HasSubnet(int bit) => subnetNames.ContainsKey(bit);
        public void AddSubnet(int bit, string name)
        {
            subnetNames[bit] = name;
            if (bit > 0 && bit < RASubnets.MaxSubnets && !subnetPriority.Contains(bit))
                subnetPriority.Add(bit);
        }
        public void RenameSubnet(int bit, string name)
        {
            if (subnetNames.ContainsKey(bit)) subnetNames[bit] = name;
        }

        public IEnumerable<KeyValuePair<int, string>> SubnetsByPriority()
        {
            RepairPriorityList();
            for (int i = 0; i < subnetPriority.Count; i++)
            {
                int bit = subnetPriority[i];
                if (subnetNames.TryGetValue(bit, out string name))
                    yield return new KeyValuePair<int, string>(bit, name);
            }
        }

        public bool MoveSubnetPriority(int bit, int delta)
        {
            RepairPriorityList();
            int idx = subnetPriority.IndexOf(bit);
            if (idx < 0) return false;
            int nidx = idx + delta;
            if (nidx < 0 || nidx >= subnetPriority.Count) return false;
            subnetPriority.RemoveAt(idx);
            subnetPriority.Insert(nidx, bit);
            RequestNetworkRefreshDeferred();
            return true;
        }

        private void RepairPriorityList()
        {
            subnetPriority.RemoveAll(b => b <= 0 || b >= RASubnets.MaxSubnets || !subnetNames.ContainsKey(b));
            if (subnetPriority.Count == 0)
            {
                foreach (var bit in subnetNames.Keys.OrderBy(x => x))
                {
                    if (bit <= 0 || bit >= RASubnets.MaxSubnets) continue;
                    subnetPriority.Add(bit);
                }
                return;
            }
            foreach (var bit in subnetNames.Keys.OrderBy(x => x))
            {
                if (bit <= 0 || bit >= RASubnets.MaxSubnets) continue;
                if (!subnetPriority.Contains(bit)) subnetPriority.Add(bit);
            }
        }

        public bool TryGetSubnetColor(int bit, out Color c) => subnetColors.TryGetValue(bit, out c);
        public void SetSubnetColor(int bit, Color c)
        {
            if (bit <= 0 || bit >= RASubnets.MaxSubnets) return;
            c.a = 1f;
            subnetColors[bit] = c;
            RequestNetworkRefreshDeferred();
        }
        public void ClearSubnetColor(int bit)
        {
            if (subnetColors.Remove(bit))
                RequestNetworkRefreshDeferred();
        }

        private static bool TryParseHexRGB(string s, out Color c)
        {
            c = Color.white;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.Length != 6) return false;
            if (!int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int rgb)) return false;
            c = new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
            return true;
        }
        private static string ToHexRGB(Color c)
        {
            int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
            int g = Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
            int b = Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
            return $"#{r:X2}{g:X2}{b:X2}";
        }


        private static bool refreshScheduled;

        public override void OnAwake()
        {
            base.OnAwake();
            Instance = this;
            refreshScheduled = false;
        }

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            groundStationAntennaOverrides.Clear();
            if (node == null) return;

            foreach (ConfigNode child in node.GetNodes("GroundStationAntennaSubnetOverride"))
            {
                string key = child.GetValue("key") ?? string.Empty;
                string maskVal = child.GetValue("mask") ?? "0";
                if (string.IsNullOrEmpty(key)) continue;
                if (!TryParseMask(maskVal, out uint mask)) continue;
                groundStationAntennaOverrides[key] = RASubnets.NormalizeMask(mask);
            }

            subnetNames.Clear();
            foreach (ConfigNode sn in node.GetNodes("SUBNET"))
            {
                if (!int.TryParse(sn.GetValue("bit"), out int bit)) continue;
                string name = sn.GetValue("name");
                if (!string.IsNullOrEmpty(name)) subnetNames[bit] = name;
            }


            // Persist subnet priority ordering
            RepairPriorityList();
            for (int i = 0; i < subnetPriority.Count; i++)
            {
                var pn = node.AddNode("SUBNET_PRIORITY");
                pn.AddValue("bit", subnetPriority[i]);
            }

            // Persist subnet custom colors
            foreach (var kvp in subnetColors)
            {
                var cn = node.AddNode("SUBNET_COLOR");
                cn.AddValue("bit", kvp.Key);
                cn.AddValue("hex", ToHexRGB(kvp.Value));
            }

            var ui = SubnetManagerWindow.Acquire();

            // Preferred: normalized position survives resolution/UI scale changes.
            if (node.HasValue("windowNormPos"))
            {
                string[] parts = node.GetValue("windowNormPos").Split(',');
                if (parts.Length == 2 &&
                    float.TryParse(parts[0], out float nx) &&
                    float.TryParse(parts[1], out float ny))
                {
                    ui?.LoadWindowNormPos(nx, ny);
                }
            }
            else if (node.HasValue("windowRect"))
            {
                // Back-compat: use x/y from legacy saved rect, ignore saved w/h.
                string[] parts = node.GetValue("windowRect").Split(',');
                if (parts.Length == 4 &&
                    float.TryParse(parts[0], out float wx) &&
                    float.TryParse(parts[1], out float wy))
                {
                    ui?.LoadWindowPixelPos(wx, wy);
                }
            }


            // Load subnet priority ordering (optional)
            subnetPriority.Clear();
            foreach (ConfigNode pn in node.GetNodes("SUBNET_PRIORITY"))
            {
                if (int.TryParse(pn.GetValue("bit"), out int bit))
                {
                    if (bit > 0 && bit < RASubnets.MaxSubnets && !subnetPriority.Contains(bit))
                        subnetPriority.Add(bit);
                }
            }
            RepairPriorityList();

            // Load subnet custom colors (optional)
            subnetColors.Clear();
            foreach (ConfigNode cn in node.GetNodes("SUBNET_COLOR"))
            {
                if (!int.TryParse(cn.GetValue("bit"), out int bit)) continue;
                string hex = cn.GetValue("hex") ?? string.Empty;
                if (TryParseHexRGB(hex, out Color col)) subnetColors[bit] = col;
            }

            ApplyRuntimeOverrides();
        }

        public override void OnSave(ConfigNode node)
        {
            base.OnSave(node);
            if (node == null) return;

            foreach (var kvp in groundStationAntennaOverrides)
            {
                var child = node.AddNode("GroundStationAntennaSubnetOverride");
                child.AddValue("key", kvp.Key);
                child.AddValue("mask", kvp.Value);
            }

            foreach (var kvp in subnetNames)
            {
                var sn = node.AddNode("SUBNET");
                sn.AddValue("bit", kvp.Key);
                sn.AddValue("name", kvp.Value);
            }

            var ui = SubnetManagerWindow.Acquire();
            if (ui != null)
            {
                // Save normalized position (top-left) for robust persistence.
                Vector2 np = ui.GetWindowNormPos();
                node.SetValue("windowNormPos", np.x + "," + np.y, true);

                // Keep legacy field too (harmless) so older builds can still load position.
                Rect r = ui.GetWindowRect();
                node.SetValue("windowRect", r.x + "," + r.y + "," + r.width + "," + r.height, true);
            }
        }

        public bool TryGetGroundStationAntennaOverride(string stationName, int antennaIndex, out uint mask)
            => groundStationAntennaOverrides.TryGetValue(BuildGroundStationKey(stationName, antennaIndex), out mask);

        public void SetGroundStationAntennaOverride(string stationName, int antennaIndex, uint mask)
        {
            groundStationAntennaOverrides[BuildGroundStationKey(stationName, antennaIndex)] = RASubnets.NormalizeMask(mask);
            ApplyRuntimeOverrides();
            RequestNetworkRefreshDeferred();
        }

        public void ApplyRuntimeOverrides()
        {
            if (RACommNetScenario.GroundStations == null) return;
            foreach (var kvp in RACommNetScenario.GroundStations)
            {
                var station = kvp.Value;
                if (station?.Comm == null) continue;
                for (int i = 0; i < station.Comm.RAAntennaList.Count; i++)
                    if (TryGetGroundStationAntennaOverride(station.nodeName, i, out uint mask))
                        station.Comm.RAAntennaList[i].SubnetMask = mask;
            }
        }

        // Delete a subnet and ensure antennas that were only on that subnet revert to Public.
        // Also repairs ground-station override entries so runtime overrides can't re-apply deleted bits.
        public void DeleteSubnet(int bit)
        {
            uint deletedBitMask = (1u << bit);
            uint clearMask = ~deletedBitMask;

            // Repair ground station override entries referencing this subnet.
            var keysToUpdate = new List<string>();
            foreach (var kvp in groundStationAntennaOverrides)
            {
                if ((kvp.Value & deletedBitMask) != 0u)
                    keysToUpdate.Add(kvp.Key);
            }

            for (int i = 0; i < keysToUpdate.Count; i++)
            {
                string k = keysToUpdate[i];
                uint m = RASubnets.NormalizeMask(groundStationAntennaOverrides[k] & clearMask);
                if ((m & ~RASubnets.PublicBit) == 0u) m = RASubnets.PublicBit;
                groundStationAntennaOverrides[k] = RASubnets.NormalizeMask(m);
            }

            // Vessels
            foreach (var v in FlightGlobals.Vessels)
            {
                if (v?.Connection is RACommNetVessel cnv && cnv.Comm is RACommNode node)
                {
                    foreach (var ant in node.RAAntennaList)
                    {
                        uint m = RASubnets.NormalizeMask(ant.SubnetMask & clearMask);
                        if ((m & ~RASubnets.PublicBit) == 0u) m = RASubnets.PublicBit;
                        ant.SubnetMask = RASubnets.NormalizeMask(m);
                    }
                    cnv.DiscoverAntennas();
                }
            }

            // Ground stations runtime
            if (RACommNetScenario.GroundStations != null)
            {
                foreach (var kvp in RACommNetScenario.GroundStations)
                {
                    var station = kvp.Value;
                    if (station?.Comm == null) continue;
                    for (int i = 0; i < station.Comm.RAAntennaList.Count; i++)
                    {
                        uint m = RASubnets.NormalizeMask(station.Comm.RAAntennaList[i].SubnetMask & clearMask);
                        if ((m & ~RASubnets.PublicBit) == 0u) m = RASubnets.PublicBit;
                        station.Comm.RAAntennaList[i].SubnetMask = RASubnets.NormalizeMask(m);
                    }
                }
            }
            subnetPriority.Remove(bit);
            subnetColors.Remove(bit);
            subnetNames.Remove(bit);
            RequestNetworkRefreshDeferred();
        }

        public static void RequestNetworkRefresh()
        {
            if (RACommNetScenario.Instance is RACommNetScenario scen && scen.Network != null)
                scen.Network.InvalidateCache();
            if (FlightGlobals.ActiveVessel != null)
                GameEvents.onVesselWasModified.Fire(FlightGlobals.ActiveVessel);
        }

        public static void RequestNetworkRefreshDeferred()
        {
            if (refreshScheduled) return;
            refreshScheduled = true;
            Instance.StartCoroutine(DeferredNetworkRefresh());
        }

        private static IEnumerator DeferredNetworkRefresh()
        {
            yield return null;
            try
            {
                if (RACommNetScenario.Instance is RACommNetScenario scen && scen.Network != null)
                    scen.Network.InvalidateCache();
                if (FlightGlobals.ActiveVessel != null)
                    GameEvents.onVesselWasModified.Fire(FlightGlobals.ActiveVessel);
            }
            finally { refreshScheduled = false; }
        }

        public static bool TryParseMask(string value, out uint mask)
        {
            mask = 0u;
            if (string.IsNullOrEmpty(value)) return false;
            value = value.Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(value.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out mask);
            return uint.TryParse(value, out mask);
        }

        public static string BuildGroundStationKey(string stationName, int antennaIndex)
            => stationName + ":" + antennaIndex;
    }

    #endregion

    internal class SubnetManagerUI : MonoBehaviour
    {
        // =====================================================================
        #region Enums and row data types
        // =====================================================================

        private enum TopTab { Vessels, GroundStations }
        private enum VesselSortMode { Alphabetical, VesselType, RFBand, Subnet }
        private enum StationSortMode { Alphabetical, RFBand, Subnet }

        private static string VesselSortLabel(VesselSortMode m)
        {
            switch (m)
            {
                case VesselSortMode.VesselType: return "Vessel Type";
                case VesselSortMode.RFBand: return "RF Band";
                case VesselSortMode.Subnet: return "Subnet";
                default: return "Vessel";
            }
        }

        private static string StationSortLabel(StationSortMode m)
        {
            switch (m)
            {
                case StationSortMode.RFBand: return "RF Band";
                case StationSortMode.Subnet: return "Subnet";
                default: return "Station";
            }
        }

        // Static panel width layout (ratios) (percent of usable interior width). Antennas take the remainder.
        private const float Static_LeftRatio = 0.30f;

        // Reduce the right (Antennas/Subnets) column relative to the original layout
        private const float RightShrinkFactor = 0.67f;
        // Hard clamps (scaled)
        private float MinLeftW => SF(280f);
        private float MaxLeftW => SF(900f);
        private float MinAntW => SF(360f);
        private float MaxAntW => SF(1200f);

        private class BodyNode
        {
            public CelestialBody Body;
            public int Depth;
            public int Count;
        }

        private class VesselRow
        {
            public Vessel Vessel;
            public RACommNode Node;
            public string BandSummary;
            public string SubnetSummary;
        }

        private class StationRow
        {
            public Network.RACommNetHome Station;
            public string Name;
            public int AntCount;
            public string BandSummary;
            public string SubnetSummary;
        }

        private class AntennaRow
        {
            public string Key;
            public RealAntenna Antenna;
            public int StationIndex = -1;
        }

        #endregion

        // =====================================================================
        #region Layout constants + scaling
        // =====================================================================

        // Common column widths / gaps (scaled) — base values are constants to avoid accidental recursion
        private const float CheckboxPx = 26f;
        private const float ActionBtnPx = 220f;
        private const float MoreBtnPx = 34f; // compact per-row overflow (⋮)
        private const float TargetColPx = 120f;
        private const float SmallGapPx = 2f;
        private const float GapPx = 6f;

        private float CheckboxW => SF(CheckboxPx);
        private float ActionBtnW => SF(ActionBtnPx);
        private float MoreBtnW => SF(MoreBtnPx);
        private float TargetColW => SF(TargetColPx);
        private float SmallGap => SF(SmallGapPx);
        private float Gap => SF(GapPx);

        // =====================================================================

        private const float BaseRowH = 24f;
        private const float BasePanelBarH = 40f; // Taller panel headers for consistent alignment
        private const float BaseBodyIcon = 20f;
        private const float BaseRowIcon = 20f;
        private const float BaseSmallIcon = 22f;
        // Header/control height (taller than table rows for better ergonomics)
        private const float BaseControlH = 32f;
        // Subnet panel fixed status block height (base px, scaled via SF)
        private const float SubnetStatusBaseH = 64f; // Compact status box
        // KSP scale x multiplier
        private bool useKspUiScale = true;
        private float uiScaleMultiplier = 1.0f; // Increase to make everything bigger.

        private float uiScale = 1.0f;

        private float RowH => BaseRowH * uiScale;
        private float PanelBarH => BasePanelBarH * uiScale;
        private float BodyIcon => BaseBodyIcon * uiScale;
        private float RowIcon => BaseRowIcon * uiScale;
        private float SmallIcon => BaseSmallIcon * uiScale;
        private float ControlH => BaseControlH * uiScale;
        private float SubnetStatusH => SubnetStatusBaseH * uiScale;
        private int S(int px) => Mathf.Max(1, Mathf.RoundToInt(px * uiScale));
        private float SF(float px) => px * uiScale;
        private RectOffset SO(int l, int r, int t, int b) => new RectOffset(S(l), S(r), S(t), S(b));

        // Spacing helpers (scale-aware)
        private float CheckW => CheckboxW;
        private float SwatchW => SF(12f);
        private float GapW => SF(12f);

        #endregion

        // =====================================================================
        #region UI state & caches
        // =====================================================================

        private SubnetManagerScenario Registry => SubnetManagerScenario.Instance;
        private IEnumerable<KeyValuePair<int, string>> ActiveSubnets =>
            Registry != null
                ? Registry.SubnetsByPriority()
                : Enumerable.Empty<KeyValuePair<int, string>>();

        private bool _visible;
        public bool Visible
        {
            get { return _visible; }
            set
            {
                if (_visible == value) return;
                _visible = value;
                SubnetManagerLauncher.SetToolbar(value);
                if (!value) _cachedPanelH = -1f; // reset on hide so re-open recomputes
            }
        }

        // Window sizing
        private Rect windowRect = Rect.zero;
        private bool windowRectInitialized;

        // Panel sizing
        private float leftPanelWidth, antennaPanelWidth, subnetPanelWidth;
        private float lastWindowWidth;
        private float _lastPanelWidthScale = -1f;
        // Remember dragged position (normalized screen coords)
        private float _windowNormX = 0.5f; // 0..1
        private float _windowNormY = 0.5f; // 0..1

        // Track screen/scale for deterministic resize
        private float _lastScreenW = -1f;
        private float _lastScreenH = -1f;
        private float _lastWindowScale = -1f;

        // Scroll positions
        private Vector2 bodyScroll, vesselScroll, stationScroll, antennaScroll, subnetScroll, subnetFilterScroll;

        // Tab & sort state
        private TopTab tab = TopTab.Vessels;
        private VesselSortMode vesselSortMode = VesselSortMode.Alphabetical;
        private StationSortMode stationSortMode = StationSortMode.Alphabetical;

        // Filter state
        private string searchText = string.Empty;
        private CelestialBody selectedBody;
        private bool bodyDropdownOpen;
        private bool subnetFilterDropdownOpen;
        private readonly Dictionary<string, bool> rfBandFilterStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, bool> subnetFilterStates = new Dictionary<int, bool>();

        // Selection state
        private Vessel selectedVessel;
        private RACommNode selectedVesselNode;
        private Network.RACommNetHome selectedStation;
        private readonly HashSet<string> selectedAntennaKeys = new HashSet<string>();

        // Subnet assignment state
        private uint workingMask = RASubnets.PublicBit;
        private uint baselineMask = RASubnets.PublicBit;
        private bool hasPendingChanges;
        private bool baselineMixed;

        // Inline rename state
        private int renamingBit = -1;
        private string renameBuffer = string.Empty;
        private bool renameFocusPending;

        // Inline delete-confirm state
        private int deleteConfirmBit = -1;
        // Overflow actions (⋮) menu state for progressive disclosure
        private int actionsMenuBit = -1;
        // Cached anchor + context for overlay popovers (updated while drawing rows)
        private Rect actionsMenuAnchorRect = Rect.zero;
        private string actionsMenuName = string.Empty;
        private bool actionsMenuCanUp = false;
        private bool actionsMenuCanDown = false;
        private bool actionsMenuVisibleThisFrame = false;

        // Popover boundary handling (viewport in scroll-content coordinates)
        private float actionsMenuViewportW = 0f;
        private float actionsMenuViewportH = 0f;
        private float actionsMenuViewportScrollY = 0f;
        // Inline color editor state
        private int colorEditBit = -1;
        private float colorR = 0.25f, colorG = 0.50f, colorB = 0.75f;
        private string colorHexBuffer = string.Empty;
        // Overlay color popover anchor + visibility (updated while drawing the open row)
        private Rect colorPopoverAnchorRect = Rect.zero;
        private bool colorPopoverVisibleThisFrame = false;


        // External config window (Map UI)
        private GameObject netUIConfigWindowGO = null;

        // Body icon caches
        private Texture2D texStar, texPlanet, texMoon;
        private readonly Dictionary<string, Texture2D> bodyIconCache = new Dictionary<string, Texture2D>(64);
        private Dictionary<string, string> kopernicusIconPathCache;

        // UI icon caches
        private Texture2D icoPlan, icoDish, icoOmni, icoPencil, icoPaint, icoTrash;
        private GUIContent gcPlan, gcDish, gcOmni, gcPencil, gcPaint, gcTrash;

        // Color swatch caches
        private readonly Dictionary<int, Texture2D> swatchTexCache = new Dictionary<int, Texture2D>();
        private readonly Dictionary<uint, Texture2D> maskSwatchCache = new Dictionary<uint, Texture2D>();
        private Texture2D publicOutlineSwatch;

        // Cached textures
        private Texture2D dotDirtyTex;
        private Texture2D dotCleanTex;
        private Texture2D selectedRowTex;
        private Texture2D flashRowTex;

        // Cached row lists — rebuilt only when filters/state change
        private List<VesselRow> _cachedVesselRows;
        private List<StationRow> _cachedStationRows;
        private bool _vesselCacheDirty = true;
        private bool _stationCacheDirty = true;

        // Cached height reserved for the subnet footer warning line
        private float subnetFooterExtraH = 0f;
        private float lastSubnetFooterWidth = -1f;
        private float lastSubnetFooterScale = -1f;

        // Cached static GUIContent
        private GUIContent _gcEmpty;
        private GUIContent _gcPublicLabel;

        // Apply feedback flash
        private float _lastApplyFlashT = -999f;
        private readonly HashSet<string> _lastAppliedKeys = new HashSet<string>();

        private void InitStaticGUIContent()
        {
            _gcEmpty = new GUIContent(string.Empty);
            _gcPublicLabel = new GUIContent("Public (Default)", "Fallback subnet used when no private subnet is selected.");
        }

        private bool debugLayout = true;
        private float _measuredHeaderH;
        private float _measuredFooterH;
        private float _measuredContentH;

        private void InvalidateVesselCache() => _vesselCacheDirty = true;
        private void InvalidateStationCache() => _stationCacheDirty = true;

        // =====================================================================
        // Chrome accounting (AUTHORITATIVE for window sizing)
        // =====================================================================
        private float ComputeHorizontalChrome()
        {
            float chrome = 0f;
            var wp = HighLogic.Skin.window?.padding;
            if (wp != null) chrome += wp.left + wp.right;
            var box = HighLogic.Skin.box;
            if (box != null)
            {
                if (box.padding != null) chrome += box.padding.left + box.padding.right;
                if (box.margin != null) chrome += box.margin.left + box.margin.right;
            }
            return chrome;
        }

        #endregion

        // =====================================================================
        #region Styles
        // =====================================================================

        private GUIStyle panelBar;
        private GUIStyle panelBgNav, panelBgContent, panelBgAction;
        private GUIStyle subnetAssignedLabel, subnetUnassignedLabel;
        private GUIStyle footerBtnPrimary;
        private GUIStyle dividerLine;
        private GUIStyle subnetStatusBox;
        private Texture2D panelBgNavTex, panelBgContentTex, panelBgActionTex, dividerTex;
        private readonly Color accentColor = new Color(0.22f, 0.55f, 0.95f, 1f);

        private GUIStyle panelTitle;
        private GUIStyle panelRow;
        private GUIStyle colHeader;
        private GUIStyle rowBtn;
        private GUIStyle rowLabel;
        private GUIStyle publicRowLabel; // bold label for Public row
        private GUIStyle mutedLabel;
        private GUIStyle tinyMutedLabel;
        private GUIStyle selectedRow;
        private GUIStyle flashRow;
        private GUIStyle warningText;
        private GUIStyle willApplyLabel; // emphasized 'Will apply' in status strip
        private GUIStyle swatchStyle;

        private GUIStyle tbBtn;         // compact toolbar
        private GUIStyle filterBtn;    // RF + subnet chips

        private GUIStyle rfFilterBtn; // RF band chips
        private GUIStyle footerBtn;  // Apply/Discard/Close  // Apply/Discard/Close
        private GUIStyle tabBtn;     // Tabs (inactive)
        private GUIStyle tabBtnActive; // Tabs (active/selected)
        private GUIStyle actionBtn;  // pencil / action icon buttons
        private GUIStyle scaledTextField;
        private GUIStyle tooltipBox;

        private bool stylesReady;
        private float lastStyleScale = -1f;

        private void EnsureStyles()
        {
            if (stylesReady && Mathf.Approximately(uiScale, lastStyleScale)) return;

            // Destroy previous dynamic textures to avoid leaks when scale changes
            if (stylesReady)
            {
                SafeDestroy(ref dotDirtyTex);
                SafeDestroy(ref dotCleanTex);
                SafeDestroy(ref selectedRowTex);
                SafeDestroy(ref flashRowTex);
                SafeDestroy(ref publicOutlineSwatch);
                SafeDestroy(ref panelBgNavTex);
                SafeDestroy(ref panelBgContentTex);
                SafeDestroy(ref panelBgActionTex);
                SafeDestroy(ref dividerTex);
                // Swatch caches hold textures; clear and recreate lazily.
                swatchTexCache.Clear();
                maskSwatchCache.Clear();
            }

            stylesReady = true;
            lastStyleScale = uiScale;

            // panelBar is for the underlying panel container of each list
            panelBar = new GUIStyle(HighLogic.Skin.box)
            {
                padding = SO(6, 6, 4, 4),
                //margin = new RectOffset(0, 0, 0, 0),
                normal = { background = MakeSolidTex(new Color(0.15f, 0.15f, 0.15f, 0.95f)) }
            };

            //panelTitle is for the title label at the top of each panel
            panelTitle = new GUIStyle(HighLogic.Skin.label)
            {
                fontSize = S(14),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                padding = SO(6, 6, 3, 3),
                margin = new RectOffset(0, 0, 0, 0)
            };

            // panelRow is for the underlying panel row container of each list.
            // It has no padding or margins, ensuring a tight grid.
            panelRow = new GUIStyle(HighLogic.Skin.box)
            {
                padding = SO(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0)
            };

            //colHeader is for the column headers in the lists.
            //Left-pad must match the rowBtn/rowLabel/mutedLabel to ensure content aligns in the table.
            //margin = 0 to avoid interfering with panelBar padding when measuring available width for dynamic column sizing.
            colHeader = new GUIStyle(HighLogic.Skin.label)
            {
                fontSize = S(14),
                padding = SO(6, 6, 3, 3),
                margin = SO(0, 0, 0, 0),
                wordWrap = false
            };
            colHeader.normal.textColor = new Color(0.55f, 0.55f, 0.55f, 1f);

            //rowBtn is for the invisible button over each row that captures clicks for selection,
            //popover anchors, etc. It has no padding so the entire row rect is clickable.
            rowBtn = new GUIStyle(HighLogic.Skin.button)
            {
                fontSize = S(14),
                alignment = TextAnchor.MiddleLeft,
                padding = SO(6, 6, 3, 3),
                margin = SO(0, 0, 0, 0),
                wordWrap = false
            };

            //rowLabel is for the main label of each row (vessel/station name, subnet name, etc).
            //It has left padding to separate from the rowBtn's click area on the left edge.
            rowLabel = new GUIStyle(HighLogic.Skin.label)
            {
                fontSize = S(14),
                alignment = TextAnchor.MiddleLeft,
                padding = SO(6, 6, 3, 3),
                margin = SO(0, 0, 0, 0),
                wordWrap = false
            };



            // Subnet label hierarchy: assigned uses accent + bold, unassigned is slightly muted.
            subnetAssignedLabel = new GUIStyle(rowLabel) { fontStyle = FontStyle.Bold };
            subnetAssignedLabel.normal.textColor = accentColor;
            subnetUnassignedLabel = new GUIStyle(rowLabel);
            subnetUnassignedLabel.normal.textColor = new Color(0.85f, 0.85f, 0.85f, 0.92f);

            //publicRowLabel is a variant of rowLabel with bold font and lighter color to make
            //the "Public (Default)" row visually distinctive.
            publicRowLabel = new GUIStyle(rowLabel) { fontStyle = FontStyle.Bold };
            publicRowLabel.normal.textColor = new Color(0.80f, 0.85f, 0.95f, 1f);

            //mutedLabel is for secondary info in the rows (antenna details, RF band, subnet list, etc) that should be
            //visually de-emphasized compared to the main rowLabel. It uses a lighter gray color.
            mutedLabel = new GUIStyle(HighLogic.Skin.label)
            {
                fontSize = S(14),
                alignment = TextAnchor.MiddleLeft,
                padding = SO(6, 6, 2, 2),
                //margin = SO(0, 0, 0, 0),
                wordWrap = true
            };
            mutedLabel.normal.textColor = new Color(0.55f, 0.55f, 0.55f, 1f);

            //tinyMutedLabel is for tertiary info that should be even more visually de-emphasized than mutedLabel.
            //It uses an even lighter gray and smaller font size.
            tinyMutedLabel = new GUIStyle(HighLogic.Skin.label)
            {
                fontSize = S(12),
                alignment = TextAnchor.MiddleLeft,
                padding = SO(0, 0, 0, 0),
                //margin = S(0, 0, 0, 0),
                wordWrap = true
            };
            tinyMutedLabel.normal.textColor = new Color(0.60f, 0.60f, 0.60f, 1f);

            //selectedRow is a variant of panelRow and is for the background of selected rows in the lists.
            //It's a semi-transparent blue to provide clear feedback without obscuring the text.
            selectedRow = new GUIStyle(panelRow);
            selectedRowTex = MakeSolidTex(new Color(0.25f, 0.50f, 0.75f, 0.30f));
            selectedRow.normal.background = selectedRowTex;

            //flashRowTex is for the brief highlight flash when applying changes to show which rows were affected.
            //It's a brighter blue with lower opacity than selectedRowTex so it stands out even on already-selected rows,
            //but still subtle enough to not be distracting.
            flashRowTex = MakeSolidTex(new Color(0.25f, 0.85f, 0.40f, 0.22f));
            flashRow = new GUIStyle(HighLogic.Skin.box);
            flashRow.normal.background = flashRowTex;

            //warningText is for the warning message in the subnet panel footer about mixed selection.
            //It's a smaller font size and orange color to visually differentiate it from the main content.
            warningText = new GUIStyle(HighLogic.Skin.label)
            {
                fontSize = S(12),
                wordWrap = true
            };
            warningText.normal.textColor = new Color(1f, 0.75f, 0.1f, 1f);



            // willApplyLabel emphasizes the pending assignment summary
            willApplyLabel = new GUIStyle(HighLogic.Skin.label)
            {
                fontSize = S(12),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                padding = SO(0, 0, 0, 0),
                wordWrap = false
            };
            willApplyLabel.normal.textColor = accentColor;
            // tooltipBox renders GUIContent.tooltips in a compact hover popup.
            tooltipBox = new GUIStyle(HighLogic.Skin.box)
            {
                fontSize = S(12),
                alignment = TextAnchor.UpperLeft,
                wordWrap = true,
                padding = SO(6, 6, 4, 4),
                margin = SO(0, 0, 0, 0)
            };
            tooltipBox.normal.textColor = new Color(0.92f, 0.92f, 0.92f, 1f);


            //swatchStyle is for the color swatches used in the subnet rows and the color editor popover.
            //It has no background or borders (GUIStyle.none) so only the swatch texture is visible,
            //and padding/margin are set to position the swatch correctly within the row.
            swatchStyle = new GUIStyle(GUIStyle.none)
            {
                margin = SO(6, 6, 2, 2),
                padding = new RectOffset(0, 0, 0, 0)
            };

            //tbBtn is for toolbar buttons
            //and centered text alignment for a balanced look.
            tbBtn = new GUIStyle(HighLogic.Skin.button)
            {
                fontSize = S(14),
                alignment = TextAnchor.MiddleCenter,
                padding = SO(8, 8, 4, 4),
                margin = SO(2, 2, 0, 0),
                fixedHeight = Mathf.Max(ControlH, PanelBarH - SF(2f)) // slightly less than panel bar height to fit nicely within it  
            };

            //filterBtn is for the RF band and subnet filter chips/buttons.
            filterBtn = new GUIStyle(HighLogic.Skin.button)
            {
                fontSize = S(14),
                alignment = TextAnchor.MiddleCenter,
                imagePosition = ImagePosition.ImageLeft,
                padding = SO(8, 8, 4, 4),
                margin = SO(2, 2, 0, 0)
            };
            rfFilterBtn = new GUIStyle(filterBtn);
            rfFilterBtn.imagePosition = ImagePosition.TextOnly;
            rfFilterBtn.alignment = TextAnchor.MiddleCenter;

            //footerBtn is for the Apply/Discard/Close buttons in the subnet panel footer.
            footerBtn = new GUIStyle(HighLogic.Skin.button)
            {
                fontSize = S(16),
                alignment = TextAnchor.MiddleCenter,
                padding = SO(8, 8, 4, 4),
                margin = SO(2, 2, 0, 0)
            };

            //tabBtn is for the top-level tabs to switch between Vessels and Ground Stations.
            tabBtn = new GUIStyle(HighLogic.Skin.button)
            {
                fontSize = S(16),
                alignment = TextAnchor.MiddleCenter,
                padding = SO(8, 8, 4, 4),
                //margin = SO(0, 0, 0, 0),
                stretchWidth = true
            };

            // Active/selected tab: bold text + pressed-state background for visual prominence
            tabBtnActive = new GUIStyle(tabBtn)
            {
                fontStyle = FontStyle.Bold
            };
            tabBtnActive.normal = tabBtn.active;
            tabBtnActive.normal.textColor = accentColor;
            tabBtnActive.hover.textColor = accentColor;
            tabBtnActive.active.textColor = accentColor;
            // use the "pressed" look to signal current selection

            dotDirtyTex = MakeSolidTex(new Color(1.00f, 0.65f, 0.10f));
            dotCleanTex = MakeSolidTex(new Color(0.25f, 0.80f, 0.30f));

            //actionBtn is for the inline pencil icon button to trigger renaming a subnet, and the ⋮ overflow 
            //button to show more actions.
            actionBtn = new GUIStyle(HighLogic.Skin.button)
            {
                fontSize = S(18),
                alignment = TextAnchor.MiddleCenter,
                padding = SO(4, 4, 3, 3),
                margin = SO(1, 1, 0, 0)
            };

            //scaledTextField is for the inline text field when renaming a subnet.
            //It has padding to position the text nicely within the field,
            scaledTextField = new GUIStyle(HighLogic.Skin.textField)
            {
                fontSize = S(14),
                padding = SO(4, 4, 3, 3),
                margin = SO(0, 0, 0, 0),
                alignment = TextAnchor.MiddleCenter,
            };


            // --- BUILD_ROLE_TINT_STYLES ---
            // Role-based panel tinting + section dividers + primary action emphasis
            // (kept subtle to preserve HighLogic.Skin look-and-feel)
            panelBgNavTex = MakeSolidTex(new Color(0.18f, 0.18f, 0.18f, 0.92f));
            panelBgContentTex = MakeSolidTex(new Color(0.21f, 0.21f, 0.21f, 0.92f));
            panelBgActionTex = MakeSolidTex(new Color(0.16f, 0.20f, 0.28f, 0.92f));
            panelBgNav = new GUIStyle(panelRow) { normal = { background = panelBgNavTex }, padding = SO(4, 4, 4, 4), margin = new RectOffset(0, 0, 0, 0) };
            panelBgContent = new GUIStyle(panelRow) { normal = { background = panelBgContentTex }, padding = SO(4, 4, 4, 4), margin = new RectOffset(0, 0, 0, 0) };
            panelBgAction = new GUIStyle(panelRow) { normal = { background = panelBgActionTex }, padding = SO(4, 4, 4, 4), margin = new RectOffset(0, 0, 0, 0) };

            dividerTex = MakeSolidTex(new Color(1f, 1f, 1f, 0.12f));
            dividerLine = new GUIStyle(HighLogic.Skin.box) { normal = { background = dividerTex }, margin = SO(0, 0, 0, 0), padding = SO(0, 0, 0, 0) };

            subnetAssignedLabel = new GUIStyle(rowLabel) { fontStyle = FontStyle.Bold };
            subnetAssignedLabel.normal.textColor = accentColor;
            subnetUnassignedLabel = new GUIStyle(rowLabel);
            subnetUnassignedLabel.normal.textColor = new Color(0.85f, 0.85f, 0.85f, 0.92f);

            footerBtnPrimary = new GUIStyle(footerBtn) { fontStyle = FontStyle.Bold };
            footerBtnPrimary.normal.textColor = accentColor;
            footerBtnPrimary.hover.textColor = accentColor;
            footerBtnPrimary.active.textColor = accentColor;

            subnetStatusBox = new GUIStyle(panelBar)
            {
                normal = { background = MakeSolidTex(new Color(0.13f, 0.16f, 0.22f, 0.97f)) },
                padding = SO(8, 8, 6, 6),
                margin = SO(0, 0, 0, 0)
            };

            tabBtnActive.normal.textColor = accentColor;
            tabBtnActive.hover.textColor = accentColor;
            tabBtnActive.active.textColor = accentColor;
            
            // Style changes invalidate cached widths
            InvalidateVesselCache();
            InvalidateStationCache();
        }

        private void EnsureSubnetFooterHeight(float usableWidth)
        {
            if (warningText == null) return;
            float w = usableWidth;
            if (w <= 0f) return;

            if (Mathf.Approximately(w, lastSubnetFooterWidth) && Mathf.Approximately(uiScale, lastSubnetFooterScale))
                return;

            lastSubnetFooterWidth = w;
            lastSubnetFooterScale = uiScale;

            const string mixedMsg = "\u26A0 Mixed selection \u2014 apply overwrites all.";

            float avail = Mathf.Max(TargetColW, w); //- SF(20f)
            subnetFooterExtraH = warningText.CalcHeight(new GUIContent(mixedMsg), avail);
            subnetFooterExtraH = Mathf.Max(subnetFooterExtraH, SF(16f));
        }

        #endregion

        // =====================================================================
        #region Lifecycle
        // =====================================================================

        private void Awake()
        {
            Targeting.TextureTools.Initialize();
            EnsureBodyIcons();
            InitStaticGUIContent();
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);
        }

        private void OnDestroy() => GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);

        private void OnSceneChange(GameScenes scene)
        {
            Visible = false;
            if (netUIConfigWindowGO is GameObject)
            {
                Destroy(netUIConfigWindowGO.GetComponent<MapUI.NetUIConfigurationWindow>());
                netUIConfigWindowGO.DestroyGameObject();
                netUIConfigWindowGO = null;
            }
        }

        private void UpdateUIScale()
        {
            if (!useKspUiScale) return;

            float s = 1f;
            try
            {
                var t = typeof(GameSettings);
                foreach (string name in new[] { "UI_SCALE", "UI_SCALE_APPS" })
                {
                    var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (f != null && f.FieldType == typeof(float)) { s = (float)f.GetValue(null); break; }
                    var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (p != null && p.PropertyType == typeof(float)) { s = (float)p.GetValue(null, null); break; }
                }
            }
            catch { }

            uiScale = Mathf.Clamp(s * uiScaleMultiplier, 0.85f, 3.5f);
        }

        public void ResetSelection()
        {
            CommitRename();
            deleteConfirmBit = -1;
            actionsMenuBit = -1;
            CloseColorEdit();
            selectedAntennaKeys.Clear();
            baselineMask = workingMask = RASubnets.PublicBit;
            baselineMixed = hasPendingChanges = false;
        }

        public void LoadWindowNormPos(float nx, float ny)
        {
            _windowNormX = Mathf.Clamp01(nx);
            _windowNormY = Mathf.Clamp01(ny);
            windowRectInitialized = false;
        }

        public void LoadWindowPixelPos(float x, float y)
        {
            _windowNormX = x / Mathf.Max(1f, Screen.width);
            _windowNormY = y / Mathf.Max(1f, Screen.height);
            windowRectInitialized = false;
        }

        public Vector2 GetWindowNormPos() => new Vector2(_windowNormX, _windowNormY);
        public Rect GetWindowRect() => windowRect;

        private void EnsureWindowRect()
        {
            float targetW = Mathf.Clamp(Screen.width * 0.85f, SF(900f), SF(1600f));
            float targetH = Mathf.Clamp(Screen.height * 0.80f, SF(600f), SF(1000f));

            bool first = !windowRectInitialized;
            bool screenChanged = !Mathf.Approximately(Screen.width, _lastScreenW) || !Mathf.Approximately(Screen.height, _lastScreenH);
            bool scaleChanged = !Mathf.Approximately(uiScale, _lastWindowScale);

            if (first)
            {
                float x, y;
                if (Mathf.Approximately(_windowNormX, 0.5f) && Mathf.Approximately(_windowNormY, 0.5f))
                {
                    x = (Screen.width - targetW) * 0.5f;
                    y = (Screen.height - targetH) * 0.5f;
                }
                else
                {
                    x = _windowNormX * Screen.width;
                    y = _windowNormY * Screen.height;
                }

                x = Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - targetW));
                y = Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - targetH));

                windowRect = new Rect(Mathf.Round(x), Mathf.Round(y), targetW, targetH);
                windowRectInitialized = true;
            }
            else if (screenChanged || scaleChanged)
            {
                float x = _windowNormX * Screen.width;
                float y = _windowNormY * Screen.height;
                x = Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - targetW));
                y = Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - targetH));
                windowRect = new Rect(Mathf.Round(x), Mathf.Round(y), targetW, targetH);
            }

            _lastScreenW = Screen.width;
            _lastScreenH = Screen.height;
            _lastWindowScale = uiScale;
        }


        private void EnsurePanelWidths()
        {

            // Two-column layout: Left navigation + Right stacked (Antennas over Subnets).
            // Cache by window width + UI scale only.
            if (Mathf.Approximately(windowRect.width, lastWindowWidth) &&
                Mathf.Approximately(uiScale, _lastPanelWidthScale))
                return;
            lastWindowWidth = windowRect.width;
            _lastPanelWidthScale = uiScale;

            float chrome = ComputeHorizontalChrome();
            float usable = Mathf.Max(windowRect.width - chrome, SF(400f));

            // Baseline allocation.
            float lBase = Mathf.Clamp(Mathf.Floor(usable * Static_LeftRatio), MinLeftW, MaxLeftW);
            float rBase = Mathf.Clamp(Mathf.Floor(usable - lBase), MinAntW, MaxAntW);

            // Shrink the right column relative to baseline. Freed space flows to the left.
            float r = Mathf.Clamp(Mathf.Floor(rBase * RightShrinkFactor), MinAntW, MaxAntW);
            float l = Mathf.Clamp(Mathf.Floor(usable - r), MinLeftW, MaxLeftW);

            // Recompute right after clamping left so the sum fits.
            r = Mathf.Clamp(Mathf.Floor(usable - l), MinAntW, MaxAntW);
            l = Mathf.Clamp(Mathf.Floor(usable - r), MinLeftW, MaxLeftW);

            // Final safety: if clamps still overshoot, reduce right then left.
            float total = l + r;
            if (total > usable)
            {
                float over = total - usable;
                if (r > MinAntW)
                {
                    float take = Mathf.Min(over, r - MinAntW);
                    r -= take;
                    over -= take;
                }
                if (over > 0f && l > MinLeftW)
                {
                    float take = Mathf.Min(over, l - MinLeftW);
                    l -= take;
                    over -= take;
                }
            }
            else if (total < usable)
            {
                // Distribute extra space to the left first (vessel list readability), then right.
                float under = usable - total;
                if (l < MaxLeftW)
                {
                    float add = Mathf.Min(under, MaxLeftW - l);
                    l += add;
                    under -= add;
                }
                if (under > 0f && r < MaxAntW)
                {
                    float add = Mathf.Min(under, MaxAntW - r);
                    r += add;
                    under -= add;
                }
            }

            leftPanelWidth = l;
            antennaPanelWidth = r;
            subnetPanelWidth = r;
        }

        #endregion

        // =====================================================================
        #region Top-level drawing
        // =====================================================================

        private string _lastSearchText = string.Empty;
        private CelestialBody _lastSelectedBody;
        private TopTab _lastTab;
        private VesselSortMode _lastVesselSort;
        private StationSortMode _lastStationSort;
        
        private void CheckFilterStateForCacheInvalidation()
        {
            if (searchText != _lastSearchText ||
                selectedBody != _lastSelectedBody ||
                tab != _lastTab ||
                vesselSortMode != _lastVesselSort ||
                stationSortMode != _lastStationSort)
            {
                _lastSearchText = searchText;
                _lastSelectedBody = selectedBody;
                _lastTab = tab;
                _lastVesselSort = vesselSortMode;
                _lastStationSort = stationSortMode;
                InvalidateVesselCache();
                InvalidateStationCache();
                _cachedPanelH = -1f;
            }
        }

        private void OnGUI()
        {
            if (!Visible) return;

            GUI.skin = HighLogic.Skin;

            UpdateUIScale();
            EnsureStyles();
            EnsureWindowRect();
            EnsurePanelWidths();
            EnsureUIIcons();
            float statusInnerW = subnetPanelWidth;
            if (subnetStatusBox != null) statusInnerW -= subnetStatusBox.padding.horizontal;
            if (panelBgAction != null) statusInnerW -= panelBgAction.padding.horizontal;
            EnsureSubnetFooterHeight(statusInnerW);
            //EnsureSubnetFooterHeight(subnetPanelWidth);
            EnsurePublicOutlineSwatch();
            CheckFilterStateForCacheInvalidation();

            int winId = GetInstanceID();

            Rect _windRect = ClickThruBlocker.GUILayoutWindow(
                winId,
                windowRect,
                DrawWindow,
                "Subnet Manager",
                HighLogic.Skin.window);
            //windowRect = _windRect;
            windowRect.x = _windRect.x;
            windowRect.y = _windRect.y;

            if (Event.current.type == EventType.Repaint)
            {

                _windowNormX = windowRect.x / Mathf.Max(1f, Screen.width);
                _windowNormY = windowRect.y / Mathf.Max(1f, Screen.height);
            }
        }

        // Cached panel content height
        private float _cachedPanelH = -1f;
        private float _lastPanelHWindowH = -1f;
        private float _lastPanelHScale = -1f;

        private float GetPanelH()
        {
            if (Mathf.Approximately(windowRect.height, _lastPanelHWindowH) &&
                Mathf.Approximately(uiScale, _lastPanelHScale) &&
                _cachedPanelH > 0f)
                return _cachedPanelH;

            _lastPanelHWindowH = windowRect.height;
            _lastPanelHScale = uiScale;

            float headerH = (_measuredHeaderH > 0f) ? _measuredHeaderH : EstimateHeaderHeight();
            float footerH = (_measuredFooterH > 0f) ? _measuredFooterH : SF(40f);
            float dragH = SF(18f);
            float boxChromeV = (HighLogic.Skin.box?.padding?.vertical ?? 0f) + (HighLogic.Skin.box?.margin?.vertical ?? 0f);
            float spacing = (_measuredHeaderH > 0f) ? boxChromeV : (SmallGap + boxChromeV);
            float winChromeV = HighLogic.Skin.window?.padding?.vertical ?? 0f;
            
            _cachedPanelH = windowRect.height - winChromeV - dragH - headerH - spacing - footerH - SF(10f);
            _cachedPanelH = Mathf.Max(_cachedPanelH, SF(280f));
            
            return _cachedPanelH;
        }


        private void DrawWindow(int id)
        {
            // Drag strip
            var dragRect = GUILayoutUtility.GetRect(1f, SF(18f), GUILayout.ExpandWidth(true));
            GUI.Box(dragRect, GUIContent.none);
            GUI.DragWindow(new Rect(0, 0, 10000, dragRect.height));

            float yAfterDrag = 0f;
            if (Event.current.type == EventType.Repaint)
                yAfterDrag = GUILayoutUtility.GetLastRect().yMax;

            DrawHeader();
            GUILayout.Space(SmallGap);

            if (Event.current.type == EventType.Repaint)
            {
                float yAfterHeader = GUILayoutUtility.GetLastRect().yMax;
                _measuredHeaderH = yAfterHeader - yAfterDrag;
            }

            float panelH = Mathf.Max(SF(160f), GetPanelH());
            float minScrollH = SF(80f);

            float panelBgNavPadV = panelBgNav?.padding?.vertical ?? 0f;
            float leftScrollH = Mathf.Max(panelH - panelBgNavPadV - PanelBarH - RowH - SF(1f), minScrollH);

            ComputeStackedRightPanelHeights(panelH,
                out float antPanelH, out float antScrollH,
                out float subPanelH, out float subScrollH);

            GUILayout.BeginHorizontal(HighLogic.Skin.box, GUILayout.ExpandWidth(false));
            DrawLeftPanel(panelH, leftScrollH);
            DrawRightPanel(panelH, antPanelH, antScrollH, subPanelH, subScrollH);
            GUILayout.EndHorizontal();

            if (Event.current.type == EventType.Repaint)
                _measuredContentH = GUILayoutUtility.GetLastRect().yMax;

            float yBeforeFooter = 0f;
            if (Event.current.type == EventType.Repaint)
                yBeforeFooter = GUILayoutUtility.GetLastRect().yMax;

            DrawFooter();
            DrawTooltip();

            if (Event.current.type == EventType.Repaint && debugLayout)
            {
                float yAfterFooter = GUILayoutUtility.GetLastRect().yMax;
                _measuredFooterH = yAfterFooter - yBeforeFooter;
            }

        }

        private void ComputeStackedRightPanelHeights(float rightPanelH,
            out float antPanelH, out float antScrollH,
            out float subPanelH, out float subScrollH)
        {

            float minA = SF(80f);
            float minS = SF(80f);
            float hardMin = SF(50f);

            float panelBgContentPadV = panelBgContent?.padding?.vertical ?? 0f;
            float antennaFixed = panelBgContentPadV + PanelBarH + RowH + SF(1f);
            float statusStripH = RowH + SF(6f);
            float warningH = (baselineMixed ? subnetFooterExtraH : 0f);
            float subnetFixed = panelBgContentPadV + PanelBarH + RowH + statusStripH + warningH;

            float dividerH = SF(1f) + SmallGap;
            float remainingScrollable = rightPanelH - (antennaFixed + subnetFixed + dividerH);
            if (remainingScrollable < 0f) remainingScrollable = 0f;

            if (remainingScrollable < (minA + minS))
            {
                float half = remainingScrollable * 0.5f;
                minA = Mathf.Max(hardMin, half);
                minS = Mathf.Max(hardMin, remainingScrollable - minA);
            }

            // Antennas/Subnets = 45/55 (favor subnets)
            float desiredS = remainingScrollable * 0.55f;
            float maxS = Mathf.Max(minS, remainingScrollable - minA);
            subScrollH = Mathf.Clamp(desiredS, minS, maxS);
            antScrollH = Mathf.Max(minA, remainingScrollable - subScrollH);

            float sum = antScrollH + subScrollH;
            if (sum > remainingScrollable)
            {
                float over = sum - remainingScrollable;
                float takeA = Mathf.Min(over, Mathf.Max(0f, antScrollH - minA));
                antScrollH -= takeA;
                over -= takeA;
                if (over > 0f)
                {
                    float takeS = Mathf.Min(over, Mathf.Max(0f, subScrollH - minS));
                    subScrollH -= takeS;
                    over -= takeS;
                }
            }

            antPanelH = antennaFixed + antScrollH;
            subPanelH = subnetFixed + subScrollH;

            // Allocate rounding drift to Antennas scroll.
            float used = antPanelH + dividerH + subPanelH;
            float delta = rightPanelH - used;
            if (Mathf.Abs(delta) > 0.01f)
            {
                float newAntScroll = Mathf.Max(minA, antScrollH + delta);
                antPanelH += (newAntScroll - antScrollH);
                antScrollH = newAntScroll;
            }
        }

        private void DrawRightPanel(float rightH, float antPanelH, float antScrollH, float subPanelH, float subScrollH)
        {
            GUILayout.BeginVertical(GUILayout.Width(antennaPanelWidth), GUILayout.Height(rightH));

            DrawAntennaPanel(antPanelH, antScrollH, includeFooterSpacer: false);

            GUILayout.Box(GUIContent.none, dividerLine, GUILayout.ExpandWidth(true), GUILayout.Height(SF(1f)));
            GUILayout.Space(SmallGap);

            DrawSubnetPanel(subPanelH, subScrollH, includeFooterSpacer: false);

            GUILayout.EndVertical();
        }

        private float EstimateHeaderHeight()
        {
            float panelBarPadV = panelBar?.padding?.vertical ?? 0f;

            float h = SF(38f) + panelBarPadV   // tab buttons + panelBar padding
                    + SmallGap                  // explicit Space() between tabs and controls
                    + ControlH + panelBarPadV   // controls bar + panelBar padding
                    + SF(22f) + panelBarPadV;   // filter summary: calibrated text(22) + panelBar padding

            if (tab == TopTab.Vessels)
                h += SF(34f);                   // vessel filter strip (box.padding.V = 0, explicit Height)

            // SmallGap after DrawHeader() is measured as part of _measuredHeaderH
            // and accounted for as `spacing` in GetPanelH — do not add it here
            if (bodyDropdownOpen) h += SF(180f);
            if (subnetFilterDropdownOpen)
            {
                int snCount = Registry != null ? Registry.Subnets.Count : 0;
                h += SF(24f) + snCount * (RowH + SmallGap);
            }
            return h;
        }

        #endregion

        // =====================================================================
        #region Header drawing
        // =====================================================================

        private void DrawHeader()
        {
            bool inEditor = HighLogic.LoadedSceneIsEditor;

            // Tabs — active tab uses tabBtnActive (bold + pressed look) so it reads as selected
            GUILayout.BeginHorizontal(panelBar);
            if (GUILayout.Button(inEditor ? "Vessels (flight only)" : "Vessels",
                tab == TopTab.Vessels ? tabBtnActive : tabBtn,
                GUILayout.ExpandWidth(true), GUILayout.Height(SF(38f))))
            {
                if (!inEditor && tab != TopTab.Vessels)
                    AttemptChangeContext(
                        () => { tab = TopTab.Vessels; subnetFilterDropdownOpen = false; ClearSelection(keepBodyFilter: true); },
                        "Switch to Vessels tab?");
            }
            if (GUILayout.Button("Ground Stations",
                tab == TopTab.GroundStations ? tabBtnActive : tabBtn,
                GUILayout.ExpandWidth(true), GUILayout.Height(SF(38f))))
            {
                if (tab != TopTab.GroundStations)
                    AttemptChangeContext(
                        () => { tab = TopTab.GroundStations; subnetFilterDropdownOpen = false; ClearSelection(keepBodyFilter: true); },
                        "Switch to Ground Stations tab?");
            }

            GUILayout.EndHorizontal();
            GUILayout.Space(SmallGap);

            // Controls
            GUILayout.BeginHorizontal(panelBar);
            GUILayout.Label("Body", mutedLabel, GUILayout.Width(SF(40f)), GUILayout.Height(ControlH));
            string bodyLabel = (selectedBody != null ? GetDisplayBodyName(selectedBody) : "All") + (bodyDropdownOpen ? " \u25B2" : " \u25BC"); // ▲▼
            if (GUILayout.Button(bodyLabel, tbBtn, GUILayout.Width(SF(140f)), GUILayout.Height(ControlH)))
                bodyDropdownOpen = !bodyDropdownOpen;

            GUILayout.Space(SF(8f));
            GUILayout.Label("Search", mutedLabel, GUILayout.Width(SF(60f)));
            searchText = GUILayout.TextField(searchText, scaledTextField, GUILayout.Width(SF(240f)), GUILayout.Height(ControlH));

            GUILayout.Space(SF(8f));
            GUILayout.Label("Sort", mutedLabel, GUILayout.Width(SF(40f)), GUILayout.Height(ControlH));
            string sortLabel = "Sort: " + (tab == TopTab.Vessels ? VesselSortLabel(vesselSortMode) : StationSortLabel(stationSortMode)) + " \u2195"; //↕
            if (GUILayout.Button(sortLabel, tbBtn, GUILayout.Width(SF(160f)), GUILayout.Height(ControlH)))
            {
                if (tab == TopTab.Vessels)
                    vesselSortMode = (VesselSortMode)(((int)vesselSortMode + 1) % Enum.GetValues(typeof(VesselSortMode)).Length);
                else
                    stationSortMode = (StationSortMode)(((int)stationSortMode + 1) % Enum.GetValues(typeof(StationSortMode)).Length);
            }

            GUILayout.FlexibleSpace();
            // Right-justified small controls
            GUILayout.FlexibleSpace();
            Texture2D wrenchIcon = GameDatabase.Instance.GetTexture("Squad/PartList/SimpleIcons/R&D_node_icon_generic", false);
            if (GUILayout.Button(wrenchIcon, tbBtn, GUILayout.Width(SF(38f))))
                ToggleNetUIConfigWindow();
            GUILayout.EndHorizontal();

            // Filter summary line (always visible)
            GUILayout.BeginHorizontal(panelBar);
            GUILayout.Label(BuildFilterSummary(), tinyMutedLabel, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            // Body dropdown
            if (bodyDropdownOpen)
            {
                List<BodyNode> bodies = BuildBodyTree();
                bodyScroll = GUILayout.BeginScrollView(bodyScroll, GUILayout.Height(SF(180f)));
                if (GUILayout.Button("All", rowBtn))
                    AttemptChangeContext(() => { selectedBody = null; bodyDropdownOpen = false; }, "Change body filter?");

                for (int i = 0; i < bodies.Count; i++)
                {
                    BodyNode n = bodies[i];
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(IconForBody(n.Body), GUILayout.Width(BodyIcon), GUILayout.Height(BodyIcon));
                    string indent = n.Depth == 0 ? "" : new string(' ', n.Depth * 3) + "\u2514\u2500 ";
                    if (GUILayout.Button(indent + GetDisplayBodyName(n.Body) + " (" + n.Count + ")", filterBtn, GUILayout.Height(RowH)))
                    {
                        CelestialBody b = n.Body;
                        AttemptChangeContext(() => { selectedBody = b; bodyDropdownOpen = false; }, "Change body filter?");
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
                // If the selected row scrolled out of view, close popovers.
                if (actionsMenuBit != -1 && !actionsMenuVisibleThisFrame) actionsMenuBit = -1;
                if (colorEditBit != -1 && !colorPopoverVisibleThisFrame) CloseColorEdit();
            }

            // Vessels-only filter strip
            if (tab == TopTab.Vessels)
            {
                List<VesselRow> basis = GetVisibleVessels(includeRfSubnetFiltering: false);
                EnsureFilterStateDictionaries(basis);

                GUILayout.BeginHorizontal(HighLogic.Skin.box);
                GUILayout.Label("Type", mutedLabel, GUILayout.Width(SF(40f)));
                foreach (VesselType vt in Targeting.TextureTools.vesselTypes)
                {
                    if (!Targeting.TextureTools.filterTextures.TryGetValue(vt, out Texture2D tex)) continue;
                    if (!Targeting.TextureTools.filterStates.ContainsKey(vt))
                        Targeting.TextureTools.filterStates[vt] = true;
                    if (!(vt == VesselType.EVA) && !(vt == VesselType.Flag) && !(vt == VesselType.SpaceObject) && !(vt == VesselType.Unknown))
                    {
                        bool prevState = Targeting.TextureTools.filterStates[vt];
                        Targeting.TextureTools.filterStates[vt] = GUILayout.Toggle(
                            Targeting.TextureTools.filterStates[vt], tex,
                            filterBtn, GUILayout.Width(SF(34f)), GUILayout.Height(SF(34f)));
                        string vtinfo = vt.Description();
                        if (Targeting.TextureTools.filterStates[vt] != prevState) InvalidateVesselCache();
                    }
                }
                GUILayout.Space(SF(8f));

                GUILayout.Label("RF", mutedLabel, GUILayout.Width(SF(28f)));
                if (GUILayout.Button("All", rfFilterBtn, GUILayout.Width(SF(70f)), GUILayout.Height(ControlH))) { SetAllRfBandFilters(false); InvalidateVesselCache(); }
                foreach (var kv in rfBandFilterStates)
                {
                    bool next = GUILayout.Toggle(kv.Value, kv.Key, rfFilterBtn, GUILayout.Width(SF(70f)), GUILayout.Height(ControlH));
                    if (next != kv.Value) { rfBandFilterStates[kv.Key] = next; InvalidateVesselCache(); }
                }

                GUILayout.FlexibleSpace();
                bool sdOpen = subnetFilterDropdownOpen;
                if (GUILayout.Button("Subnet: " + GetSubnetFilterSummary() + (sdOpen ? " \u25B2" : " \u25BC"),
                        rfFilterBtn, GUILayout.Width(SF(160f)), GUILayout.Height(ControlH)))
                    subnetFilterDropdownOpen = !subnetFilterDropdownOpen;

                GUILayout.EndHorizontal();

                if (subnetFilterDropdownOpen)
                    DrawSubnetFilterDropdown();
            }
        }

        private string BuildFilterSummary()
        {
            // Keep this short and scannable.
            string body = selectedBody != null ? GetDisplayBodyName(selectedBody) : "All bodies";
            string srch = string.IsNullOrEmpty(searchText) ? "" : $" | Search: \"{searchText}\"";

            string rf = "";
            if (AnyRfBandFilterOn())
            {
                var on = rfBandFilterStates.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
                rf = on.Count == 0
                    ? ""
                    : (" | RF: " + (on.Count <= 2 ? string.Join(",", on) : (on[0] + ", +" + (on.Count - 1))));
            }

            string sn = "";
            if (AnySubnetFilterOn())
                sn = " | Subnet: " + GetSubnetFilterSummary();

            return "Filters: " + body + rf + sn + srch;
        }
        private void DrawSubnetFilterDropdown()
        {
            GUILayout.BeginVertical(HighLogic.Skin.box);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Subnet filter", mutedLabel, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            float stripH = RowH + SF(10f);
            subnetFilterScroll = GUILayout.BeginScrollView(subnetFilterScroll, false, false, GUILayout.Height(stripH));
            GUILayout.BeginHorizontal();

            if (Registry != null)
            {
                DrawSubnetChip(0, "Public", null);
                foreach (var kvp in ActiveSubnets)
                    DrawSubnetChip(kvp.Key, kvp.Value, GetSubnetSwatch(kvp.Key));
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Clear", rfFilterBtn, GUILayout.Width(SF(80f)), GUILayout.Height(ControlH))) SetAllSubnetFilters(false);
            GUILayout.EndHorizontal();
            GUILayout.EndScrollView();

            GUILayout.EndVertical();
        }

        private void DrawSubnetChip(int bit, string label, Texture2D swatch)
        {
            bool cur = GetSubnetFilter(bit);

            // Encode subnet color via the button background (more compact than a separate chip).
            Color tint;
            if (bit == 0)
            {
                tint = new Color(0.22f, 0.22f, 0.22f, 1f);
            }
            else
            {
                Color c;
                if (Registry != null && Registry.TryGetSubnetColor(bit, out c)) {
                    /* use custom */
                }
                else c = RASubnets.SubnetColor(bit, 1f);
                tint = c;
            }

            Color prevBg = GUI.backgroundColor;
            Color prevContent = GUI.contentColor;
            GUI.backgroundColor = tint;
            float lum = 0.2126f * tint.r + 0.7152f * tint.g + 0.0722f * tint.b;
            GUI.contentColor = (lum > 0.55f) ? Color.black : Color.white;

            bool next = GUILayout.Toggle(cur, label, filterBtn, GUILayout.Height(ControlH), GUILayout.Width(SF(96f)));

            GUI.backgroundColor = prevBg;
            GUI.contentColor = prevContent;

            if (next != cur)
            {
                SetSubnetFilter(bit, next);
                InvalidateVesselCache();
                InvalidateStationCache();
            }

            GUILayout.Space(Gap);
        }


        private void ToggleNetUIConfigWindow()
        {
            try
            {
                if (netUIConfigWindowGO is GameObject)
                {
                    Destroy(netUIConfigWindowGO.GetComponent<MapUI.NetUIConfigurationWindow>());
                    netUIConfigWindowGO.DestroyGameObject();
                    netUIConfigWindowGO = null;
                }
                else
                {
                    netUIConfigWindowGO = new GameObject("RealAntennas.NetUIConfigWindow");
                    DontDestroyOnLoad(netUIConfigWindowGO);
                    netUIConfigWindowGO.AddComponent<MapUI.NetUIConfigurationWindow>();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[SubnetManagerUI] Failed to toggle NetUIConfigurationWindow");
                Debug.LogException(ex);
            }
        }
        #endregion

        // =====================================================================
        #region Left panel
        // =====================================================================

        private void DrawLeftPanel(float panelH, float scrollH)
        {
            GUILayout.BeginVertical(panelBgNav, GUILayout.Width(leftPanelWidth), GUILayout.Height(panelH));
            if (tab == TopTab.Vessels) DrawVesselListPanel(scrollH);
            else DrawGroundStationListPanel(scrollH);
            GUILayout.EndVertical();
        }

        private void DrawVesselListPanel(float scrollH)
        {
            GUILayout.BeginHorizontal(panelBar, GUILayout.Height(PanelBarH));
            if (HighLogic.LoadedSceneIsEditor)
            {
                GUILayout.Label("Vessels", panelTitle);
                GUILayout.EndHorizontal();
                GUILayout.Label("Vessel subnets are configured in flight via the part action window.", mutedLabel);
                GUILayout.FlexibleSpace();
                DrawPanelFooterSpacer();
                return;
            }

            List<VesselRow> vessels = GetCachedVessels(includeRfSubnetFiltering: true);
            bool compactCols = false;
            GUILayout.Label("Vessels", panelTitle, GUILayout.ExpandWidth(true));
            DrawListSummary(vessels.Count);
            GUILayout.EndHorizontal();

            float iconW = RowIcon;
            float antW = compactCols ? 0f : Mathf.Max(SF(34f), mutedLabel.CalcSize(new GUIContent("999")).x + SF(14f));
            float bandW = compactCols ? 0f : ComputeColumnWidthFromVessels(vessels, true, SF(44f), TargetColW);
            float snW = compactCols ? 0f : ComputeColumnWidthFromVessels(vessels, false, SF(44f), TargetColW);
            float panelPadH = panelBgNav?.padding?.horizontal ?? 0f;
            float rowPadH = rowLabel?.padding?.horizontal ?? 0f;
            float scrollbarW = GUI.skin.verticalScrollbar?.fixedWidth ?? SF(16f);
            
            float nameWMax = compactCols
                ? (leftPanelWidth - iconW - SF(16f))
                : (leftPanelWidth - panelPadH - rowPadH - scrollbarW - iconW - antW - bandW - snW);
            float nameW = Mathf.Max(SF(180f), Mathf.Min(nameWMax,
                ComputeColumnWidthFromVessels(vessels, false, SF(80f), SF(360f), useNames: true)));

            GUILayout.BeginHorizontal(panelRow, GUILayout.Height(RowH));
            // Use an empty label (not GUILayout.Space) so margins match the icon label in data rows.
            GUILayout.Label(_gcEmpty, GUILayout.Width(iconW));
            GUILayout.Label("Vessel", colHeader, GUILayout.Width(nameW));
            if (!compactCols)
            {
                if (!compactCols)
                {
                    GUILayout.Label("A", colHeader, GUILayout.Width(antW));
                    GUILayout.Label("RF", colHeader, GUILayout.Width(bandW));
                    GUILayout.Label("SN", colHeader, GUILayout.Width(snW));
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Box(GUIContent.none, dividerLine, GUILayout.ExpandWidth(true), GUILayout.Height(SF(1f)));
            vesselScroll = GUILayout.BeginScrollView(vesselScroll, false, false, GUILayout.Height(scrollH));
            for (int i = 0; i < vessels.Count; i++)
            {
                VesselRow row = vessels[i];
                bool isSel = row.Vessel == selectedVessel;
                GUILayout.BeginHorizontal(isSel ? selectedRow : rowLabel, GUILayout.Height(RowH));

                if (Targeting.TextureTools.filterTextures.TryGetValue(row.Vessel.vesselType, out Texture2D vTex))
                    GUILayout.Label(vTex, GUILayout.Width(iconW), GUILayout.Height(iconW));
                else
                    GUILayout.Label(_gcEmpty, GUILayout.Width(iconW));

                if (GUILayout.Button(row.Vessel.vesselName, rowLabel, GUILayout.Width(nameW), GUILayout.Height(RowH)))
                {
                    Vessel v = row.Vessel; 
                    AttemptChangeContext(() => SelectVessel(v, null), "You have unapplied subnet changes.");
                }

                if (!compactCols)
                {
                    GUILayout.Label(row.Node.RAAntennaList.Count.ToString(), mutedLabel, GUILayout.Width(antW));
                    GUILayout.Label(row.BandSummary, mutedLabel, GUILayout.Width(bandW));
                    GUILayout.Label(row.SubnetSummary, mutedLabel, GUILayout.Width(snW));
                }
                GUILayout.EndHorizontal();
            }
            if (vessels.Count == 0)
                GUILayout.Label("No vessels match the current filters.", mutedLabel);
            GUILayout.EndScrollView();
        }

        private void DrawGroundStationListPanel(float scrollH)
        {
            List<StationRow> rows = GetCachedStations();
            bool compactCols = false;

            GUILayout.BeginHorizontal(panelBar, GUILayout.Height(PanelBarH));
            GUILayout.Label("Ground Stations", panelTitle, GUILayout.ExpandWidth(true));
            DrawListSummary(rows.Count);
            GUILayout.EndHorizontal();

            float antW = compactCols ? 0f : Mathf.Max(SF(34f), mutedLabel.CalcSize(new GUIContent("999")).x + SF(14f));
            float bandW = compactCols ? 0f : ComputeColumnWidthFromStations(rows, true, SF(44f), TargetColW);
            float snW = compactCols ? 0f : ComputeColumnWidthFromStations(rows, false, SF(44f), TargetColW);
            float panelPadH = panelBgNav?.padding?.horizontal ?? 0f;
            float rowPadH = rowLabel?.padding?.horizontal ?? 0f; 
            float scrollbarW = GUI.skin.verticalScrollbar?.fixedWidth ?? SF(16f);
            // Station (no iconW in rows):
            float nameWMax = compactCols ? (leftPanelWidth - SF(16f)) : (leftPanelWidth - panelPadH - rowPadH - scrollbarW - antW - bandW - snW);
            float nameW = Mathf.Max(SF(180f), Mathf.Min(nameWMax,
                ComputeColumnWidthFromStations(rows, false, SF(80f), SF(360f), useNames: true)));

            GUILayout.BeginHorizontal(panelRow, GUILayout.Height(RowH));
            GUILayout.Label("Station", colHeader, GUILayout.Width(nameW));
            if (!compactCols)
            {
                GUILayout.Label("A", colHeader, GUILayout.Width(antW));
                GUILayout.Label("RF", colHeader, GUILayout.Width(bandW));
                GUILayout.Label("SN", colHeader, GUILayout.Width(snW));
            }
            GUILayout.EndHorizontal();

            GUILayout.Box(GUIContent.none, dividerLine, GUILayout.ExpandWidth(true), GUILayout.Height(SF(1f)));
            stationScroll = GUILayout.BeginScrollView(stationScroll, true, true, GUILayout.Height(scrollH));
            for (int i = 0; i < rows.Count; i++)
            {
                StationRow r = rows[i];
                bool isSel = selectedStation == r.Station;
                GUILayout.BeginHorizontal(isSel ? selectedRow : panelRow, GUILayout.Height(RowH));

                if (GUILayout.Button(r.Name, rowBtn, GUILayout.Width(nameW), GUILayout.Height(RowH)))
                {
                    Network.RACommNetHome st = r.Station;
                    AttemptChangeContext(() => SelectStation(st), "You have unapplied subnet changes.");
                }

                if (!compactCols)
                {
                    GUILayout.Label(r.AntCount.ToString(), mutedLabel, GUILayout.Width(antW));
                    GUILayout.Label(r.BandSummary, mutedLabel, GUILayout.Width(bandW));
                    GUILayout.Label(r.SubnetSummary, mutedLabel, GUILayout.Width(snW));
                }
                GUILayout.EndHorizontal();
            }
            if (rows.Count == 0)
                GUILayout.Label("No stations match the current filters.", mutedLabel);
            GUILayout.EndScrollView();
        }

        #endregion

        // =====================================================================
        #region Antenna panel
        // =====================================================================

        private void DrawAntennaPanel(float panelH, float scrollH, bool includeFooterSpacer = true)
        {

            GUILayout.BeginVertical(panelBgContent, GUILayout.Width(antennaPanelWidth), GUILayout.Height(panelH));
            List<AntennaRow> rows = (selectedVessel != null || selectedStation != null)
                ? GetCurrentAntennaRows() : null;
            int selCount = selectedAntennaKeys.Count;
            string ctxName = selectedVessel != null ? selectedVessel.vesselName
                : (selectedStation != null
                    ? (selectedStation.displaynodeName ?? selectedStation.nodeName)
                    : null);

            // Header
            GUILayout.BeginHorizontal(panelBar, GUILayout.Height(PanelBarH));
            GUILayout.Label("Antennas", panelTitle, GUILayout.Width(SF(90f)));
            /*
            if (ctxName != null)
                GUILayout.Label("— " + ctxName, mutedLabel, GUILayout.ExpandWidth(true));
            else
                GUILayout.FlexibleSpace();
            if (selCount > 0)
                GUILayout.Label(selCount + " sel", mutedLabel, GUILayout.Width(SF(60f)));
            */
            if (rows != null && rows.Count > 0)
            {
                if (GUILayout.Button("All", filterBtn, GUILayout.Width(SF(70f)), GUILayout.Height(ControlH)))
                    AttemptChangeContext(SelectAllAntennas, "You have unapplied subnet changes.");
                if (GUILayout.Button("None", filterBtn, GUILayout.Width(SF(70f)), GUILayout.Height(ControlH)))
                    AttemptChangeContext(() =>
                    {
                        selectedAntennaKeys.Clear();
                        baselineMask = ComputeBaselineMask(out baselineMixed);
                        workingMask = baselineMask;
                        hasPendingChanges = false;
                    }, "You have unapplied subnet changes.");
                if (DrawIconButtonLayout(icoPlan, filterBtn, ControlH, ControlH, tooltip: "Analyze link quality / signal strength", insetPx: 2f, scaleMode: ScaleMode.ScaleToFit))                
                    OpenPlannerForContext(rows);
            }
            GUILayout.EndHorizontal();

            // Column sizing
            float checkW = CheckW;
            float iconW = SmallIcon;
            float swW = SwatchW;
            float bandW = SF(60f); // Always show band
            float snW = SF(130f);
            float targetW = TargetColW;

            // Enforce UX rule: Band always visible; Subnet/Target are first to go.
            bool showTarget = antennaPanelWidth >= (checkW + iconW + swW + bandW + snW + targetW + SF(30f) + SF(140f));
            bool showSubnet = showTarget && antennaPanelWidth >= (checkW + iconW + swW + bandW + snW + SF(20f) + SF(140f));
            if (!showTarget) showSubnet = false;

            float fixedW = checkW + GapW + iconW + SF(4f) + swW + bandW;
            if (showSubnet) fixedW += snW;
            if (showTarget) fixedW += targetW;
            float panelPadH = panelBgContent?.padding?.horizontal ?? 0f;               // 8px
            float scrollbarW = GUI.skin.verticalScrollbar?.fixedWidth ?? SF(16f);       // ~16px

            float nameW = Mathf.Max(SF(80f), antennaPanelWidth - panelPadH - scrollbarW - fixedW);

            // Column header
            GUILayout.BeginHorizontal(panelRow, GUILayout.Height(RowH));
            GUILayout.Label(_gcEmpty, GUILayout.Width(checkW));
            GUILayout.Space(GapW);
            GUILayout.Label(_gcEmpty, GUILayout.Width(iconW));
            GUILayout.Space(SF(4f));
            GUILayout.Label(_gcEmpty, GUILayout.Width(swW));
            GUILayout.Label("Antenna", colHeader, GUILayout.Width(nameW));
            GUILayout.Label("Band", colHeader, GUILayout.Width(bandW));
            if (showSubnet) GUILayout.Label("Subnet", colHeader, GUILayout.Width(snW));
            if (showTarget) GUILayout.Label("Target", colHeader, GUILayout.Width(targetW));
            GUILayout.EndHorizontal();

            float tNow = Time.realtimeSinceStartup;
            bool doFlash = (tNow - _lastApplyFlashT) < 1.6f;

            GUILayout.BeginVertical(panelRow, GUILayout.Height(scrollH));
            GUILayout.Box(GUIContent.none, dividerLine, GUILayout.ExpandWidth(true), GUILayout.Height(SF(1f)));
            antennaScroll = GUILayout.BeginScrollView(antennaScroll, GUILayout.Height(scrollH));

            if (rows == null)
            {
                GUILayout.Label("Select a vessel or ground station to view antennas.", mutedLabel);
                GUILayout.FlexibleSpace();
                GUILayout.EndScrollView();
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                if (includeFooterSpacer) DrawPanelFooterSpacer();
                GUILayout.EndVertical();
                return;
            }

            for (int i = 0; i < rows.Count; i++)
            {
                AntennaRow r = rows[i];
                bool sel = selectedAntennaKeys.Contains(r.Key);
                bool flash = doFlash && _lastAppliedKeys.Contains(r.Key);
                GUILayout.BeginHorizontal(sel ? selectedRow : (flash ? flashRow : panelRow), GUILayout.Height(RowH));

                bool next = GUILayout.Toggle(sel, GUIContent.none, GUILayout.Width(checkW));
                if (next != sel)
                {
                    AttemptChangeContext(() =>
                    {
                        if (next) selectedAntennaKeys.Add(r.Key);
                        else selectedAntennaKeys.Remove(r.Key);
                        baselineMask = ComputeBaselineMask(out baselineMixed);
                        if (!hasPendingChanges) workingMask = baselineMask;
                        hasPendingChanges = (workingMask != baselineMask);
                    }, "You have unapplied subnet changes.");
                }

                GUILayout.Space(GapW);
                GUILayout.Label(r.Antenna.Shape == AntennaShape.Dish ? gcDish : gcOmni, GUILayout.Width(iconW), GUILayout.Height(iconW));

                if (GUILayout.Button(r.Antenna.Name, mutedLabel, GUILayout.Width(nameW), GUILayout.Height(RowH)))
                {
                    bool nextClick = !sel;
                    AttemptChangeContext(() =>
                    {
                        if (nextClick) selectedAntennaKeys.Add(r.Key);
                        else selectedAntennaKeys.Remove(r.Key);
                        baselineMask = ComputeBaselineMask(out baselineMixed);
                        if (!hasPendingChanges) workingMask = baselineMask;
                        hasPendingChanges = (workingMask != baselineMask);
                    }, "You have unapplied subnet changes.");
                }

                GUILayout.Label(r.Antenna.RFBand != null ? r.Antenna.RFBand.name : "-", mutedLabel, GUILayout.Width(bandW));
                if (showSubnet)
                    GUILayout.Label(RASubnets.NamedMaskSummary(r.Antenna.SubnetMask), mutedLabel, GUILayout.Width(snW));
                if (showTarget)
                    GUILayout.Label(GetCompactTargetName(r.Antenna), tinyMutedLabel, GUILayout.Width(targetW));

                GUILayout.EndHorizontal();
            }

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            if (includeFooterSpacer) DrawPanelFooterSpacer();
            GUILayout.EndVertical();
        }

        #endregion

        // =====================================================================
        #region Subnet panel
        // =====================================================================
        private void DrawSubnetPanel(float availableH, float scrollH, bool includeFooterSpacer = true)
        {
            GUILayout.BeginVertical(
                panelBgAction,
                GUILayout.Width(subnetPanelWidth),
                GUILayout.Height(availableH)
            );

            bool hasSelection = selectedAntennaKeys.Count > 0;
            if (Registry == null)
            {
                GUILayout.BeginHorizontal(panelBar, GUILayout.Height(PanelBarH));
                GUILayout.Label("Subnets", panelTitle, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();
                GUILayout.Label("Scenario not loaded.", mutedLabel);
                GUILayout.FlexibleSpace();
                if (includeFooterSpacer) DrawPanelFooterSpacer();
                GUILayout.EndVertical();
                return;
            }

            // Header
            int freeBit = FindFirstFreeBit();
            GUILayout.BeginHorizontal(panelBar, GUILayout.Height(PanelBarH));
            GUILayout.Label("Subnets", panelTitle, GUILayout.ExpandWidth(true));
            if (!hasSelection)
                GUILayout.Label("Select antennas to assign.", tinyMutedLabel, GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            GUI.enabled = freeBit > 0;
            if (GUILayout.Button(new GUIContent("+", "Add a new subnet"), actionBtn, GUILayout.Width(MoreBtnW), GUILayout.Height(ControlH)))
                AttemptChangeContext(() => { Registry.AddSubnet(freeBit, "Subnet " + freeBit); InvalidateVesselCache(); InvalidateStationCache(); }, "You have unapplied subnet changes.");
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // Column header
            GUILayout.BeginHorizontal(panelRow, GUILayout.Height(RowH));
            GUILayout.Label(_gcEmpty, colHeader, GUILayout.Width(CheckW));
            GUILayout.Space(GapW);
            GUILayout.Label("Name", colHeader, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            string selLine = hasSelection
                ? (selectedAntennaKeys.Count + " antenna" + (selectedAntennaKeys.Count == 1 ? "" : "s") + " selected")
                : "No antennas selected.";

            float statusInnerW = subnetPanelWidth;
            if (panelBgAction != null) statusInnerW -= panelBgAction.padding.horizontal;
            if (subnetStatusBox != null) statusInnerW -= subnetStatusBox.padding.horizontal;

            float warnH;
            float statusH = MeasureSubnetStatusHeight(statusInnerW, hasSelection, selLine, out warnH);

            // Compute scroll height as remainder of available height
            float padV = panelBgAction != null ? panelBgAction.padding.vertical : 0f;
            float fixedTop = 0f;
            fixedTop = 0f;

            float remaining = availableH - padV - fixedTop - statusH - PanelBarH - RowH;
            float headerPlusCols = PanelBarH + RowH;
            scrollH = Mathf.Max(SF(60f), availableH - padV - headerPlusCols - statusH);

            // Scroll list
            actionsMenuVisibleThisFrame = false;
            colorPopoverVisibleThisFrame = false;
            actionsMenuViewportW = subnetPanelWidth;
            actionsMenuViewportH = scrollH;
            actionsMenuViewportScrollY = subnetScroll.y;

            subnetScroll = GUILayout.BeginScrollView(subnetScroll, GUILayout.Height(scrollH));
            DrawSubnetRow_Public(hasSelection);

            var subnetList = ActiveSubnets.ToList();
            for (int i = 0; i < subnetList.Count; i++)
            {
                var kvp = subnetList[i];
                bool canUp = i > 0;
                bool canDown = i < subnetList.Count - 1;
                DrawSubnetRow_Custom(kvp.Key, kvp.Value, hasSelection, canUp, canDown);
            }
            if (freeBit == 0)
                GUILayout.Label("Maximum subnets reached.", mutedLabel);

            DrawSubnetActionsPopover();
            GUILayout.EndScrollView();

            // Bottom pinned status strip uses the measured height
            GUILayout.BeginVertical(subnetStatusBox, GUILayout.Height(statusH));
            GUILayout.BeginHorizontal();
            GUILayout.Label(hasPendingChanges ? dotDirtyTex : dotCleanTex, swatchStyle,
                GUILayout.Width(SF(10f)), GUILayout.Height(SF(10f)));
            GUILayout.Label(selLine, mutedLabel, GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();

            if (hasSelection)
            {
                string curSummary = baselineMixed ? "Mixed" : RASubnets.NamedMaskSummary(baselineMask);
                GUILayout.Label("Current: " + curSummary, tinyMutedLabel, GUILayout.ExpandWidth(false));
                GUILayout.Space(SF(10f));
            }

            string willSummary = hasPendingChanges
                ? (workingMask == RASubnets.PublicBit ? "Public" : RASubnets.NamedMaskSummary(workingMask))
                : "(no changes)";
            GUILayout.Label("Will apply: " + willSummary, willApplyLabel, GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();

            if (baselineMixed)
                GUILayout.Label("⚠ Mixed selection — apply overwrites all selected.", warningText);

            GUILayout.EndVertical();

            if (includeFooterSpacer) DrawPanelFooterSpacer();
            GUILayout.EndVertical();
        }

        private void DrawSubnetRow_Public(bool hasSelection)
        {
            bool pubAssigned = (workingMask == RASubnets.PublicBit);

            // Apply a subtle highlight when Public is selected; since no colored halo exists for Public.
            if (pubAssigned && hasSelection)
                GUI.color = new Color(1f, 1f, 1f, 0.10f);

            GUILayout.BeginHorizontal(panelRow, GUILayout.Height(RowH));
            GUI.color = Color.white;

            GUI.enabled = hasSelection;
            bool pubNext = GUILayout.Toggle(pubAssigned, GUIContent.none, GUILayout.Width(CheckW));
            if (pubNext && !pubAssigned)
            {
                workingMask = RASubnets.PublicBit;
                hasPendingChanges = (workingMask != baselineMask);
            }
            GUI.enabled = true;
            //"Public (default) — no color"
            GUILayout.Label(_gcPublicLabel, publicRowLabel, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        private void DrawSubnetRow_Custom(int bit, string name, bool hasSelection, bool canMoveUp, bool canMoveDown)
        {
            bool isRename = (renamingBit == bit);
            bool isDelCon = (deleteConfirmBit == bit);
            bool assigned = (workingMask & (1u << bit)) != 0u;

            // Close overflow when entering other inline modes.
            if (isRename || isDelCon) actionsMenuBit = -1;

            // Halo when assigned and selecting
            if (assigned && hasSelection)
            {
                Color subnetColor;
                if (Registry != null && Registry.TryGetSubnetColor(bit, out subnetColor)) { /* use custom */ }
                else subnetColor = RASubnets.SubnetColor(bit, 1f);
                GUI.color = new Color(subnetColor.r, subnetColor.g, subnetColor.b, 0.20f);
            }

            // Reserve a rect for the entire row so we can detect hover (progressive disclosure)
            Rect rowRect = GUILayoutUtility.GetRect(1f, RowH + SF(2f), GUILayout.ExpandWidth(true));
            GUI.color = Color.white;
            GUI.Box(rowRect, GUIContent.none, panelRow);
            bool hover = rowRect.Contains(Event.current.mousePosition);
            bool menuOpen = (actionsMenuBit == bit);
            bool showMore = hover || menuOpen;

            float x = rowRect.x;
            float y = rowRect.y;
            float h = rowRect.height;
            float checkW = CheckW;
            float gap = GapW;
            float moreW = MoreBtnW;
            float confirmW = SF(40f);

            // Checkbox
            GUI.enabled = hasSelection;
            Rect rCheck = new Rect(x, y, checkW, h);
            bool nextAssigned = GUI.Toggle(rCheck, assigned, GUIContent.none);
            GUI.enabled = true;
            x += checkW + gap;

            if (hasSelection && nextAssigned != assigned)
            {
                if (nextAssigned)
                {
                    workingMask &= ~RASubnets.PublicBit;
                    workingMask |= (1u << bit);
                }
                else
                {
                    workingMask &= ~(1u << bit);
                    if ((workingMask & ~RASubnets.PublicBit) == 0u) workingMask = RASubnets.PublicBit;
                }
                workingMask = RASubnets.NormalizeMask(workingMask);
                hasPendingChanges = (workingMask != baselineMask);
            }

            // If the inline color editor is open for this row, keep it alive even if scrolled.
            if (colorEditBit == bit)
                colorPopoverVisibleThisFrame = true;

            float rightW = (isRename || isDelCon) ? (confirmW * 2f) : moreW;
            float nameW = Mathf.Max(SF(60f), rowRect.xMax - x - rightW);
            Rect rName = new Rect(x, y, nameW, h);

            // --- Name cell / inline rename ---
            if (isRename)
            {
                if (renameFocusPending) { GUI.FocusControl("sn_r_" + bit); renameFocusPending = false; }
                GUI.SetNextControlName("sn_r_" + bit);
                string edited = GUI.TextField(rName, renameBuffer, scaledTextField);
                if (edited != renameBuffer) renameBuffer = edited;

                if (Event.current.type == EventType.KeyDown)
                {
                    if (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                    { CommitRename(); Event.current.Use(); }
                    else if (Event.current.keyCode == KeyCode.Escape)
                    { CancelRename(); Event.current.Use(); }
                }

                float rx = rowRect.xMax - rightW;
                GUIStyle actionBtnAccept = new GUIStyle(tbBtn) { normal = { textColor = Color.green }, hover = { textColor = Color.green }, active = { textColor = Color.green } };
                GUIStyle actionBtnReject = new GUIStyle(tbBtn) { normal = { textColor = Color.red }, hover = { textColor = Color.red }, active = { textColor = Color.red } };
                if (GUI.Button(new Rect(rx, y, confirmW, h), new GUIContent("✓", "Confirm rename"), actionBtnAccept)) CommitRename();
                if (GUI.Button(new Rect(rx + confirmW, y, confirmW, h), new GUIContent("✕", "Cancel rename"), actionBtnReject)) CancelRename();

                // IMPORTANT: do not draw the ⋮ button while renaming (prevents click-through problems)
                return;
            }
            else
            {
                string displayName = assigned ? ("✓ " + name) : name;

                // Paint the name label background with subnet color; inset a translucent black box so text stays legible.
                Color bgC;
                if (Registry != null && Registry.TryGetSubnetColor(bit, out bgC)) { /* use custom */ }
                else bgC = RASubnets.SubnetColor(bit, 1f);
                Color prevCol = GUI.color;
                GUI.color = new Color(bgC.r, bgC.g, bgC.b, 0.55f);
                GUI.DrawTexture(rName, Texture2D.whiteTexture);
                Rect inset = new Rect(rName.x + SF(2f), rName.y + SF(2f), rName.width - SF(4f), rName.height - SF(4f));
                GUI.color = new Color(0f, 0f, 0f, 0.28f);
                GUI.DrawTexture(inset, Texture2D.whiteTexture);
                GUI.color = prevCol;

                GUI.Label(rName, displayName, assigned ? subnetAssignedLabel : subnetUnassignedLabel);

                // Clicking the subnet name toggles assignment (same behavior as the checkbox).
                if (hasSelection && GUI.Button(rName, GUIContent.none, GUIStyle.none))
                {
                    bool clickAssigned = !assigned;
                    if (clickAssigned)
                    {
                        workingMask &= ~RASubnets.PublicBit;
                        workingMask |= (1u << bit);
                    }
                    else
                    {
                        workingMask &= ~(1u << bit);
                        if ((workingMask & ~RASubnets.PublicBit) == 0u) workingMask = RASubnets.PublicBit;
                    }
                    workingMask = RASubnets.NormalizeMask(workingMask);
                    hasPendingChanges = (workingMask != baselineMask);
                }
            }

            float rxMore = rowRect.xMax - rightW;

            // --- Inline delete confirmation ---
            if (isDelCon)
            {
                GUIStyle actionBtnAccept = new GUIStyle(tbBtn) { normal = { textColor = Color.green }, hover = { textColor = Color.green }, active = { textColor = Color.green } };
                GUIStyle actionBtnReject = new GUIStyle(tbBtn) { normal = { textColor = Color.red }, hover = { textColor = Color.red }, active = { textColor = Color.red } };
                if (GUI.Button(new Rect(rxMore, y, confirmW, h), new GUIContent("✓", "Confirm delete"), actionBtnAccept))
                {
                    deleteConfirmBit = -1;
                    InvalidateSwatchCache();
                    Registry.DeleteSubnet(bit);
                    baselineMask = ComputeBaselineMask(out baselineMixed);
                    workingMask = baselineMask;
                    hasPendingChanges = false;
                    InvalidateVesselCache();
                    InvalidateStationCache();
                }
                if (GUI.Button(new Rect(rxMore + confirmW, y, confirmW, h), new GUIContent("✕", "Cancel delete"), actionBtnReject)) deleteConfirmBit = -1;

                GUILayout.BeginHorizontal(panelBar);
                GUILayout.Label($"Delete \"{name} \"? All antennas move to Public.", warningText, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();

                // IMPORTANT: do not draw the ⋮ button while confirming delete (prevents click-through problems)
                return;
            }

            // --- Inline color editor (opened via More menu) ---
            if (colorEditBit == bit)
            {
                GUILayout.BeginVertical(panelBar, GUILayout.Width(SF(250f)));
                Color cur = new Color(colorR, colorG, colorB, 1f);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Color", mutedLabel, GUILayout.Width(SF(44f)));
                GUILayout.Label(new GUIContent(MakeSolidTex(cur)), swatchStyle, GUILayout.Width(SF(16f)), GUILayout.Height(SF(16f)));
                GUILayout.FlexibleSpace();
                GUILayout.Label(ToHexRGB_UI(cur), tinyMutedLabel);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("R", mutedLabel, GUILayout.Width(SF(13f)));
                colorR = GUILayout.HorizontalSlider(colorR, 0f, 1f, GUILayout.Width(SF(200f)));
                GUILayout.Label($"{Mathf.RoundToInt(colorR * 255f)}", tinyMutedLabel, GUILayout.Width(SF(32f)));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("G", mutedLabel, GUILayout.Width(SF(13f)));
                colorG = GUILayout.HorizontalSlider(colorG, 0f, 1f, GUILayout.Width(SF(200f)));
                GUILayout.Label($"{Mathf.RoundToInt(colorG * 255f)}", tinyMutedLabel, GUILayout.Width(SF(32f)));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("B", mutedLabel, GUILayout.Width(SF(13f)));
                colorB = GUILayout.HorizontalSlider(colorB, 0f, 1f, GUILayout.Width(SF(200f)));
                GUILayout.Label($"{Mathf.RoundToInt(colorB * 255f)}", tinyMutedLabel, GUILayout.Width(SF(32f)));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("Hex", mutedLabel, GUILayout.Width(SF(42f)));
                colorHexBuffer = GUILayout.TextField(colorHexBuffer ?? string.Empty, scaledTextField, GUILayout.Width(SF(90f)));
                GUIStyle actionBtnAccept = new GUIStyle(tbBtn) { normal = { textColor = Color.green }, hover = { textColor = Color.green }, active = { textColor = Color.green } };
                if (GUILayout.Button(new GUIContent("✓", "Parse hex"), actionBtnAccept, GUILayout.Width(SF(32f))))
                {
                    if (TryParseHexRGB_UI(colorHexBuffer, out Color hc)) { colorR = hc.r; colorG = hc.g; colorB = hc.b; }
                }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Apply", "Apply this color"), tbBtn, GUILayout.Width(SF(90f))))
                {
                    Color apply = new Color(colorR, colorG, colorB, 1f);
                    if (!string.IsNullOrEmpty(colorHexBuffer) && TryParseHexRGB_UI(colorHexBuffer, out Color hc)) apply = hc;
                    Registry?.SetSubnetColor(bit, apply);
                    InvalidateSwatchCache();
                    InvalidateVesselCache();
                    InvalidateStationCache();
                    CloseColorEdit();
                }
                if (GUILayout.Button(new GUIContent("Reset", "Reset to default"), tbBtn, GUILayout.Width(SF(90f))))
                {
                    Registry?.ClearSubnetColor(bit);
                    InvalidateSwatchCache();
                    InvalidateVesselCache();
                    InvalidateStationCache();
                    CloseColorEdit();
                }
                if (GUILayout.Button(new GUIContent("Close", "Close color editor"), tbBtn, GUILayout.Width(SF(90f)))) CloseColorEdit();
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }

            // --- More (overflow) button ---
            int itemCount = 5;
            float actionBarW = itemCount * SF(ControlH); // itemCount = 5

            Rect rMore = new Rect(rowRect.xMax - moreW, rowRect.y, moreW, rowRect.height);

            if (GUI.Button(rMore, new GUIContent("⋮", "Subnet actions"), actionBtn))
            {
                // Optionally: set actionsMenuBit = bit; (if you want to support click-to-stick)

                float barX = rMore.x - actionBarW;
                float barY = rMore.y;
                float barH = rowRect.height;
                Rect barRect = new Rect(barX, barY, actionBarW, barH);

                GUI.Box(barRect, GUIContent.none, panelBar);

                float itemH = barH - SF(4f); // slightly smaller than row
                float itemW = SF(ControlH);

                for (int i = 0; i < itemCount; i++)
                {
                    Rect btnRect = new Rect(barX + i * itemW, barY + SF(2f), itemW - SF(4f), itemH);

                    switch (i)
                    {
                        case 0: // ▲ Move up
                            GUI.enabled = canMoveUp;
                            if (GUI.Button(btnRect, new GUIContent("▲", "Move up"), actionBtn))
                            {
                                Registry?.MoveSubnetPriority(bit, -1);
                                InvalidateVesselCache();
                                InvalidateStationCache();
                            }
                            GUI.enabled = true;
                            break;
                        case 1: // ▼ Move down
                            GUI.enabled = canMoveDown;
                            if (GUI.Button(btnRect, new GUIContent("▼", "Move down"), actionBtn))
                            {
                                Registry?.MoveSubnetPriority(bit, +1);
                                InvalidateVesselCache();
                                InvalidateStationCache();
                            }
                            GUI.enabled = true;
                            break;
                        case 2: // 🎨 Color
                            if (DrawIconButton(btnRect, icoPaint, actionBtn, "Change subnet color", 4f, ScaleMode.ScaleToFit, true))
                            {
                                if (colorEditBit == bit) CloseColorEdit();
                                else BeginColorEdit(bit);
                            }
                            break;
                        case 3: // ✎ Rename
                            if (DrawIconButton(btnRect, icoPencil, actionBtn, "Rename subnet", 4f, ScaleMode.ScaleToFit, true))
                            {
                                BeginRename(bit, name);
                            }
                            break;
                        case 4: // 🗑 Delete
                            if (DrawIconButton(btnRect, icoTrash, actionBtn, "Delete subnet", 4f, ScaleMode.ScaleToFit, true))
                            {
                                CommitRename();
                                CloseColorEdit();
                                deleteConfirmBit = bit;
                            }
                            break;
                    }
                }
                if (actionsMenuBit == bit)
                {
                    actionsMenuAnchorRect = rMore;
                    actionsMenuName = name;
                    actionsMenuCanUp = canMoveUp;
                    actionsMenuCanDown = canMoveDown;
                    actionsMenuVisibleThisFrame = true;
                }
            }
            /*
            Rect rMore = new Rect(rowRect.xMax - moreW, y, moreW, h);
            var moreContent = new GUIContent("⋮", "Subnet actions");
            bool hideOtherMoreButtons = (actionsMenuBit >= 0 && actionsMenuBit != bit);
            if (!hideOtherMoreButtons)
            {

                Color prevC2 = GUI.color;
                if (!showMore) GUI.color = new Color(prevC2.r, prevC2.g, prevC2.b, 0.35f);
                if (GUI.Button(rMore, moreContent, actionBtn))
                {
                    CommitRename();
                    deleteConfirmBit = -1;
                    CloseColorEdit();
                    actionsMenuBit = (actionsMenuBit == bit) ? -1 : bit;
                }
                GUI.color = prevC2;
            }
            else
            {
                // Keep the anchor rect stable so the popover positioning continues to work.
                GUI.Label(rMore, GUIContent.none);
            }

            if (actionsMenuBit == bit)
            {
                actionsMenuAnchorRect = rMore;
                actionsMenuName = name;
                actionsMenuCanUp = canMoveUp;
                actionsMenuCanDown = canMoveDown;
                actionsMenuVisibleThisFrame = true;
            }
            */
        }

        // Draw a clickable button whose icon is manually scaled to fill the button rect.
        // IMGUI does NOT scale GUIContent images to the button size by default, so we draw the icon ourselves.
        private bool DrawIconButton(Rect r,
                                   Texture2D icon,
                                   GUIStyle style,
                                   string tooltip = null,
                                   float insetPx = 2f,
                                   ScaleMode scaleMode = ScaleMode.ScaleToFit,
                                   bool alphaBlend = true)
        {
            // Draw the button background & handle clicks using the provided style
            bool clicked = GUI.Button(r, new GUIContent(string.Empty, tooltip ?? string.Empty), style);

            // Draw the icon scaled into the same rect
            if (icon != null)
            {
                float inset = SF(insetPx);
                Rect ir = new Rect(
                    r.x + inset,
                    r.y + inset,
                    Mathf.Max(0f, r.width - inset * 2f),
                    Mathf.Max(0f, r.height - inset * 2f)
                );

                Color prev = GUI.color;
                GUI.color = Color.white;
                GUI.DrawTexture(ir, icon, scaleMode, alphaBlend);
                GUI.color = prev;
            }

            return clicked;
        }

        private bool DrawIconButtonLayout(Texture2D icon,
                                         GUIStyle style,
                                         float w,
                                         float h,
                                         string tooltip = null,
                                         float insetPx = 2f,
                                         ScaleMode scaleMode = ScaleMode.ScaleToFit,
                                         bool alphaBlend = true)
        {
            Rect r = GUILayoutUtility.GetRect(w, h, GUILayout.Width(w), GUILayout.Height(h));
            return DrawIconButton(r, icon, style, tooltip, insetPx, scaleMode, alphaBlend);
        }

        private void DrawSubnetActionsPopover()
        {
            if (actionsMenuBit < 0) return;

            // Don't show if other inline modes are active for this row.
            if (renamingBit == actionsMenuBit || deleteConfirmBit == actionsMenuBit) return;

            Rect a = actionsMenuAnchorRect;
            if (a.width <= 0f || a.height <= 0f) return;

            //float itemH = RowH; 

            float itemH = Mathf.Round(SF(ControlH));
            int itemCount = 5; // Up, Down, Color, Rename, Delete

            // Compact icon-only menu (tooltips explain each action)
            float menuW = Mathf.Round(SF(ControlH));
            float menuH = itemCount * itemH;

            // Anchor to ⋮ button: align right, open below.
            float x = Mathf.Round(a.xMax - menuW);
            float y = Mathf.Round(a.yMax);

            // Boundary handling inside visible scroll viewport (content coords).
            float scrollY = actionsMenuViewportScrollY;
            float viewH = actionsMenuViewportH;
            float viewW = actionsMenuViewportW;
            if (viewH > 0f && (y + menuH) > (scrollY + viewH)) y = a.yMin - menuH; // flip up
            if (viewH > 0f && y < scrollY) y = scrollY; // clamp top
            if (viewW > 0f) x = Mathf.Clamp(x, 0f, Mathf.Max(0f, viewW - menuW));
            else if (x < 0f) x = 0f;

            Rect menuRect = new Rect(Mathf.Round(x), Mathf.Round(y), menuW, menuH);
            GUI.Box(menuRect, GUIContent.none, panelBar);
            
            float pad = Mathf.Round(SF(2f));
            float bw = menuRect.width - pad * 2f;
            float bh = itemH - pad * 2f;
            float baseX = menuRect.x + pad;
            float baseY = menuRect.y + pad;

            Rect RowRect(int i) => new Rect(baseX, baseY + i * itemH, bw, bh);

            // ▲ Move up
            GUI.enabled = actionsMenuCanUp;
            if (GUI.Button(RowRect(0), new GUIContent("▲", "Move up (higher priority)"), actionBtn))
            {
                Registry?.MoveSubnetPriority(actionsMenuBit, -1);
                InvalidateVesselCache();
                InvalidateStationCache();
                actionsMenuBit = -1;
            }
            GUI.enabled = true;

            // ▼ Move down
            GUI.enabled = actionsMenuCanDown;
            if (GUI.Button(RowRect(1), new GUIContent("▼", "Move down (lower priority)"), actionBtn))
            {
                Registry?.MoveSubnetPriority(actionsMenuBit, +1);
                InvalidateVesselCache();
                InvalidateStationCache();
                actionsMenuBit = -1;
            }
            GUI.enabled = true;

            // 🎨 Color \U0001F3A8
            if (DrawIconButton(RowRect(2), icoPaint,actionBtn, "Change subnet color", 4f,ScaleMode.ScaleToFit,true))     
            {
                int b = actionsMenuBit;
                actionsMenuBit = -1;
                CommitRename();
                deleteConfirmBit = -1;

                // Toggle color editor for this subnet.
                if (colorEditBit == b) CloseColorEdit();
                else BeginColorEdit(b);
            }

            // ✎ Rename \U0001F589
            if (DrawIconButton(RowRect(3), icoPencil, actionBtn, "Rename subnet", 4f, ScaleMode.ScaleToFit, true))
            {
                int b = actionsMenuBit;
                string nm = actionsMenuName;
                actionsMenuBit = -1;
                BeginRename(b, nm);
            }

            // 🗑 Delete \U0001F5D1
            if (DrawIconButton(RowRect(4), icoTrash, actionBtn, "Delete subnet", 4f, ScaleMode.ScaleToFit, true))
            {
                int b = actionsMenuBit;
                actionsMenuBit = -1;
                CommitRename();
                CloseColorEdit();
                deleteConfirmBit = b;
            }
        }

        private float MeasureSubnetStatusHeight(float statusInnerWidth, bool hasSelection, string selLine, out float warnH)
        {
            // Defensive clamps
            float w = Mathf.Max(SF(120f), statusInnerWidth);

            // Build the same strings the status strip draws
            string curSummary = hasSelection ? (baselineMixed ? "Mixed" : RASubnets.NamedMaskSummary(baselineMask)) : "";
            string willSummary = hasPendingChanges
                ? (workingMask == RASubnets.PublicBit ? "Public" : RASubnets.NamedMaskSummary(workingMask))
                : "(no changes)";

            float line1H = RowH; // safe baseline

            try
            {
                float hSel = mutedLabel != null ? mutedLabel.CalcHeight(new GUIContent(selLine), w) : RowH;
                float hCur = (hasSelection && tinyMutedLabel != null) ? tinyMutedLabel.CalcHeight(new GUIContent("Current: " + curSummary), w) : 0f;
                float hWill = willApplyLabel != null ? willApplyLabel.CalcHeight(new GUIContent("Will apply: " + willSummary), w) : RowH;
                line1H = Mathf.Max(RowH, hSel, hCur, hWill, SF(10f));
            }
            catch { /* ignore; keep baseline */ }

            // Warning line only if baselineMixed true (same behavior as the draw code)
            warnH = 0f;
            if (baselineMixed && warningText != null)
            {
                const string mixedMsg = "⚠ Mixed selection — apply overwrites all selected.";
                warnH = Mathf.Max(SF(16f), warningText.CalcHeight(new GUIContent(mixedMsg), w));
            }

            // Total = subnetStatusBox padding + line1 + optional warn + small internal spacing
            float padV = subnetStatusBox != null ? subnetStatusBox.padding.vertical : 0f;
            float internalGap = SF(6f); // matches existing intent (you had RowH + SF(6f) previously)
            float total = padV + line1H + (baselineMixed ? warnH : 0f) + internalGap;

            return Mathf.Max(RowH + SF(6f), total);
        }

        #endregion

        // =====================================================================
        #region Footer (single commit zone)
        // =====================================================================

        private void DrawFooter()
        {
            bool hasSelection = selectedAntennaKeys.Count > 0;

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            GUI.enabled = hasPendingChanges && hasSelection;
            if (GUILayout.Button("Apply", footerBtnPrimary, GUILayout.Width(SF(140f)), GUILayout.Height(SF(40f))))
                ApplySubnet();
            GUI.enabled = true;

            GUI.enabled = hasPendingChanges;
            if (GUILayout.Button("Discard", footerBtn, GUILayout.Width(SF(140f)), GUILayout.Height(SF(40f))))
                DiscardChanges();
            GUI.enabled = true;

            GUILayout.Space(SF(16f));

            if (GUILayout.Button("Close", footerBtn, GUILayout.Width(SF(140f)), GUILayout.Height(SF(40f))))
            {
                // Never block; closing discards pending (unapplied) changes to avoid surprise on reopen
                if (hasPendingChanges) DiscardChanges();
                Visible = false;
            }

            GUILayout.EndHorizontal();
        }

        #endregion

        // =====================================================================
        #region Selection/apply/rename
        // =====================================================================       
        private void AttemptChangeContext(Action action, string message)
        {
            action?.Invoke();
        }
        
        private void ClearSelection(bool keepBodyFilter)
        {
            CommitRename();
            deleteConfirmBit = -1;
            actionsMenuBit = -1;
            CloseColorEdit();
            selectedVessel = null; selectedVesselNode = null; selectedStation = null;
            selectedAntennaKeys.Clear();
            baselineMask = workingMask = RASubnets.PublicBit;
            baselineMixed = hasPendingChanges = false;
            if (!keepBodyFilter) selectedBody = null;
        }

        private void SelectVessel(Vessel vessel, RACommNode node)
        {
            if (hasPendingChanges) DiscardChanges();
            CommitRename(); deleteConfirmBit = -1;
            actionsMenuBit = -1;
            CloseColorEdit();

            selectedVessel = vessel;
            selectedStation = null;

            // IMPORTANT: do NOT trust the cached row node; resolve the current live node
            selectedVesselNode = (vessel?.Connection?.Comm as RACommNode);

            // If the vessel's comm system isn't fully initialized yet, force discovery once.
            if ((selectedVesselNode == null || selectedVesselNode.RAAntennaList == null || selectedVesselNode.RAAntennaList.Count == 0) &&
                vessel?.Connection is RACommNetVessel cnv)
            {
                cnv.DiscoverAntennas();
                selectedVesselNode = (vessel.Connection?.Comm as RACommNode);
            }

            selectedAntennaKeys.Clear();
            baselineMask = workingMask = RASubnets.PublicBit;
            baselineMixed = hasPendingChanges = false;
        }

        private void SelectStation(Network.RACommNetHome station)
        {
            if (hasPendingChanges) DiscardChanges();
            CommitRename(); deleteConfirmBit = -1;
            actionsMenuBit = -1;
            CloseColorEdit();
            selectedStation = station; selectedVessel = null; selectedVesselNode = null;
            selectedAntennaKeys.Clear();
            baselineMask = workingMask = RASubnets.PublicBit;
            baselineMixed = hasPendingChanges = false;
        }

        private void SelectAllAntennas()
        {
            selectedAntennaKeys.Clear();
            foreach (var r in GetCurrentAntennaRows()) selectedAntennaKeys.Add(r.Key);
            baselineMask = ComputeBaselineMask(out baselineMixed);
            if (!hasPendingChanges) workingMask = baselineMask;
            hasPendingChanges = (workingMask != baselineMask);
        }

        private uint ComputeBaselineMask(out bool mixed)
        {
            mixed = false;
            uint? first = null;
            foreach (var r in GetCurrentAntennaRows())
            {
                if (!selectedAntennaKeys.Contains(r.Key)) continue;
                uint m = RASubnets.NormalizeMask(r.Antenna.SubnetMask);
                if (!first.HasValue) first = m;
                else if (first.Value != m) mixed = true;
            }
            return first ?? RASubnets.PublicBit;
        }

        private void DiscardChanges() { workingMask = baselineMask; hasPendingChanges = false; actionsMenuBit = -1; CloseColorEdit(); }


        private void ApplySubnet()
        {
            workingMask = RASubnets.NormalizeMask(workingMask);
            actionsMenuBit = -1;
            CloseColorEdit();

            var touchedVessels = new HashSet<Guid>();
            var unresolvedModuleVessels = new HashSet<Guid>(); // if we can't resolve module for any selected antenna on a vessel, we avoid DiscoverAntennas for that vessel
            _lastAppliedKeys.Clear();

            foreach (var key in selectedAntennaKeys)
            {
                if (string.IsNullOrEmpty(key)) continue;

                // ---- New stable module key path ----
                // VM:<vesselGuid>:<partFlightId>:<moduleIndex>
                if (key.StartsWith("VM:"))
                {
                    var p = key.Split(':');
                    if (p.Length == 4 &&
                        Guid.TryParse(p[1], out Guid vid) &&
                        uint.TryParse(p[2], out uint flightId) &&
                        int.TryParse(p[3], out int modIdx))
                    {
                        touchedVessels.Add(vid);

                        var v = FlightGlobals.Vessels.FirstOrDefault(x => x?.id == vid);
                        if (v == null) { unresolvedModuleVessels.Add(vid); continue; }

                        Part part = null;
                        try { part = v.parts?.FirstOrDefault(pp => pp != null && pp.flightID == flightId); }
                        catch { part = null; }

                        if (part == null) { unresolvedModuleVessels.Add(vid); continue; }

                        ModuleRealAntenna mra = null;
                        try
                        {
                            if (modIdx >= 0 && modIdx < part.Modules.Count)
                                mra = part.Modules[modIdx] as ModuleRealAntenna;
                        }
                        catch
                        {
                            mra = null;
                        }

                        if (mra == null) { unresolvedModuleVessels.Add(vid); continue; }

                        // Persist to module (authoritative for rebuilds)
                        mra.SubnetMask = workingMask;
                        mra.SubnetSummary = RASubnets.NamedMaskSummary(workingMask);

                        // Update module's antenna instance if present
                        if (mra.RAAntenna != null)
                            mra.RAAntenna.SubnetMask = workingMask;

                        // Also update the current live antenna object we can resolve by key (if any),
                        // so the UI reflects immediately even before any rebuild.
                        if (TryGetAntennaByKey(key, out RealAntenna ant) && ant != null)
                            ant.SubnetMask = workingMask;

                        _lastAppliedKeys.Add(key);
                    }
                    else
                    {
                        // malformed key
                        // no-op
                    }

                    continue;
                }

                // ---- Legacy paths (existing behavior) ----
                if (!TryGetAntennaByKey(key, out RealAntenna legacyAnt) || legacyAnt == null)
                    continue;

                legacyAnt.SubnetMask = workingMask;
                _lastAppliedKeys.Add(key);

                if (key.StartsWith("V:"))
                {
                    // Track vessel id
                    Guid vid = Guid.Empty;
                    var parts = key.Split(':');
                    if (parts.Length == 3 && Guid.TryParse(parts[1], out vid))
                        touchedVessels.Add(vid);

                    // Persist to backing module when possible to avoid rebuild reverting.
                    ModuleRealAntenna mra = legacyAnt.Parent as ModuleRealAntenna;

                    if (mra == null)
                        TryResolveModuleForVesselAntenna(key, legacyAnt, out mra);

                    if (mra != null)
                    {
                        mra.SubnetMask = workingMask;
                        mra.SubnetSummary = RASubnets.NamedMaskSummary(workingMask);
                        if (mra.RAAntenna != null) mra.RAAntenna.SubnetMask = workingMask;
                    }
                    else
                    {
                        if (vid != Guid.Empty)
                            unresolvedModuleVessels.Add(vid);
                    }
                }
                else if (key.StartsWith("S:"))
                {
                    // Ground stations: scenario override is the authoritative store.
                    var parts = key.Split(':');
                    if (parts.Length == 3 && int.TryParse(parts[2], out int idx))
                        if (Registry != null) Registry.SetGroundStationAntennaOverride(parts[1], idx, workingMask);
                }
            }

            // Refresh touched vessels.
            foreach (var v in FlightGlobals.Vessels)
            {
                if (v == null || !touchedVessels.Contains(v.id)) continue;

                // Always notify vessel modified so UIs refresh.
                GameEvents.onVesselWasModified.Fire(v);

                // Only rebuild antenna lists if we did NOT have any module-resolution failures
                // (otherwise rebuild can reconstruct from stale module values and "revert").
                if (!unresolvedModuleVessels.Contains(v.id))
                {
                    if (v.Connection is RACommNetVessel cnv && cnv.Comm is RACommNode)
                        cnv.DiscoverAntennas();
                }
            }

            baselineMask = ComputeBaselineMask(out baselineMixed);
            workingMask = baselineMask;
            hasPendingChanges = false;
            _lastApplyFlashT = Time.realtimeSinceStartup;

            // Make sure summaries update immediately.
            InvalidateVesselCache();
            InvalidateStationCache();

            SubnetManagerScenario.RequestNetworkRefresh();
        }

        private void BeginColorEdit(int bit)
        {
            CommitRename();
            deleteConfirmBit = -1;
            colorEditBit = bit;
            Color c;
            if (Registry != null && Registry.TryGetSubnetColor(bit, out c))
            {
                colorR = c.r; colorG = c.g; colorB = c.b;
            }
            else
            {
                c = RASubnets.SubnetColor(bit, 1f);
                colorR = c.r; colorG = c.g; colorB = c.b;
            }
            colorHexBuffer = "";
        }
        
        private void CloseColorEdit() { colorEditBit = -1; colorHexBuffer = string.Empty; colorPopoverAnchorRect = Rect.zero; }
        private static bool TryParseHexRGB_UI(string s, out Color c)
        {
            c = Color.white;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.Length != 6) return false;
            if (!int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int rgb)) return false;
            c = new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
            return true;
        }
        
        private static string ToHexRGB_UI(Color c)
        {
            int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
            int g = Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
            int b = Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
            return $"#{r:X2}{g:X2}{b:X2}";
        }
        
        private void BeginRename(int bit, string currentName)
        {
            CommitRename();
            renamingBit = bit;
            renameBuffer = currentName;
            renameFocusPending = true;
        }

        private void CommitRename()
        {
            if (renamingBit < 0) return;
            string trimmed = renameBuffer.Trim();
            if (!string.IsNullOrEmpty(trimmed) && Registry != null) Registry.RenameSubnet(renamingBit, trimmed);
            renamingBit = -1;
            renameBuffer = string.Empty;
        }

        private void CancelRename() { renamingBit = -1; renameBuffer = string.Empty; }

        private void OpenPlannerForContext(List<AntennaRow> rows)
        {
            RealAntenna source = null;
            foreach (string key in selectedAntennaKeys)
                if (TryGetAntennaByKey(key, out RealAntenna ant) && ant != null) { source = ant; break; }
            if (source == null && rows != null && rows.Count > 0) source = rows[0].Antenna;
            if (source != null) LaunchPlanningGUI(source);
        }

        #endregion

        // =====================================================================
        #region Data + filters
        // =====================================================================

        private int FindFirstFreeBit()
        {
            if (Registry == null) return 0;
            for (int b = 1; b < RASubnets.MaxSubnets; b++)
                if (!Registry.HasSubnet(b)) return b;
            return 0;
        }

        private List<VesselRow> GetCachedVessels(bool includeRfSubnetFiltering)
        {
            if (_vesselCacheDirty || _cachedVesselRows == null)
            {
                _cachedVesselRows = GetVisibleVessels(includeRfSubnetFiltering);
                _vesselCacheDirty = false;
            }
            return _cachedVesselRows;
            //return _cachedVesselRows ?? new List<VesselRow>();
        }

        private List<StationRow> GetCachedStations()
        {
            if (_stationCacheDirty || _cachedStationRows == null)
            {
                _cachedStationRows = GetVisibleStations();
                _stationCacheDirty = false;
            }
            return _cachedStationRows ?? new List<StationRow>();
        }

        private float ComputeColumnWidthFromVessels(List<VesselRow> rows, bool useBand, float min, float max,
                                                     bool useNames = false)
        {
            float w = min;
            for (int i = 0; i < rows.Count; i++)
            {
                string s = useNames ? rows[i].Vessel.vesselName
                         : useBand ? rows[i].BandSummary
                                     : rows[i].SubnetSummary;
                if (string.IsNullOrEmpty(s)) continue;
                GUIStyle st = useNames ? rowBtn : mutedLabel;
                w = Mathf.Max(w, st.CalcSize(new GUIContent(s)).x + SF(8f));
                if (w >= max) return max;
            }
            return Mathf.Clamp(w, min, max);
        }

        private float ComputeColumnWidthFromStations(List<StationRow> rows, bool useBand, float min, float max,
                                                      bool useNames = false)
        {
            float w = min;
            for (int i = 0; i < rows.Count; i++)
            {
                string s = useNames ? rows[i].Name
                         : useBand ? rows[i].BandSummary
                                     : rows[i].SubnetSummary;
                if (string.IsNullOrEmpty(s)) continue;
                GUIStyle st = useNames ? rowBtn : mutedLabel;
                w = Mathf.Max(w, st.CalcSize(new GUIContent(s)).x + SF(8f));
                if (w >= max) return max;
            }
            return Mathf.Clamp(w, min, max);
        }

        private List<VesselRow> GetVisibleVessels(bool includeRfSubnetFiltering)
        {
            IEnumerable<VesselRow> vessels =
                FlightGlobals.Vessels
                    .Where(v => v != null && v.Connection is RACommNetVessel)
                    .Select(v => new VesselRow { Vessel = v, Node = v.Connection.Comm as RACommNode })
                    .Where(v => v.Node != null && v.Node.RAAntennaList != null && v.Node.RAAntennaList.Count > 0);

            if (!string.IsNullOrEmpty(searchText))
                vessels = vessels.Where(v =>
                    v.Vessel.vesselName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    SummarizeVesselSubnets(v.Node).IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    SummarizeVesselRFBands(v.Node).IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0);
            else if (selectedBody != null)
                vessels = vessels.Where(v => v.Vessel.mainBody == selectedBody);

            vessels = vessels.Where(v =>
                Targeting.TextureTools.filterStates.TryGetValue(v.Vessel.vesselType, out bool on) ? on : true);

            var list = vessels.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                list[i].BandSummary = SummarizeVesselRFBands(list[i].Node);
                list[i].SubnetSummary = SummarizeVesselSubnets(list[i].Node);
            }

            if (tab == TopTab.Vessels && includeRfSubnetFiltering)
            {
                if (AnyRfBandFilterOn())
                {
                    var rfFiltered = new List<VesselRow>(list.Count);
                    for (int i = 0; i < list.Count; i++)
                    {
                        var antennas = list[i].Node.RAAntennaList;
                        for (int j = 0; j < antennas.Count; j++)
                        {
                            bool on;
                            if (antennas[j].RFBand != null &&
                                rfBandFilterStates.TryGetValue(antennas[j].RFBand.name, out on) && on)
                            { rfFiltered.Add(list[i]); break; }
                        }
                    }
                    list = rfFiltered;
                }

                if (AnySubnetFilterOn())
                {
                    var snFiltered = new List<VesselRow>(list.Count);
                    for (int i = 0; i < list.Count; i++)
                    {
                        var antennas = list[i].Node.RAAntennaList;
                        bool match = false;
                        for (int j = 0; j < antennas.Count && !match; j++)
                        {
                            uint m = RASubnets.NormalizeMask(antennas[j].SubnetMask);
                            if (GetSubnetFilter(0) && (m & RASubnets.PublicBit) != 0u) match = true;
                            for (int b = 1; b < RASubnets.MaxSubnets && !match; b++)
                                if (GetSubnetFilter(b) && (m & (1u << b)) != 0u) match = true;
                        }
                        if (match) snFiltered.Add(list[i]);
                    }
                    list = snFiltered;
                }
            }

            switch (vesselSortMode)
            {
                case VesselSortMode.VesselType:
                    return list.OrderBy(v => v.Vessel.vesselType).ThenBy(v => v.Vessel.vesselName).ToList();
                case VesselSortMode.RFBand:
                    return list.OrderBy(v => v.BandSummary).ThenBy(v => v.Vessel.vesselName).ToList();
                case VesselSortMode.Subnet:
                    return list.OrderBy(v => v.SubnetSummary).ThenBy(v => v.Vessel.vesselName).ToList();
                default:
                    return list.OrderBy(v => v.Vessel.vesselName).ToList();
            }
        }

        private List<StationRow> GetVisibleStations()
        {
            IEnumerable<Network.RACommNetHome> stations = RACommNetScenario.GroundStations != null
                ? RACommNetScenario.GroundStations.Values.Where(x => x?.Comm != null)
                : Enumerable.Empty<Network.RACommNetHome>();

            if (!string.IsNullOrEmpty(searchText))
                stations = stations.Where(s =>
                {
                    string n = s.displaynodeName ?? s.nodeName;
                    return n.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                           SummarizeStationSubnets(s).IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                           SummarizeStationRFBands(s).IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;
                });
            else if (selectedBody != null)
                stations = stations.Where(s => s.Comm.ParentBody == selectedBody);

            var rows = stations.Select(s => new StationRow
            {
                Station = s,
                Name = s.displaynodeName ?? s.nodeName,
                AntCount = s.Comm.RAAntennaList.Count,
                BandSummary = SummarizeStationRFBands(s),
                SubnetSummary = SummarizeStationSubnets(s)
            }).ToList();

            switch (stationSortMode)
            {
                case StationSortMode.RFBand: return rows.OrderBy(r => r.BandSummary).ThenBy(r => r.Name).ToList();
                case StationSortMode.Subnet: return rows.OrderBy(r => r.SubnetSummary).ThenBy(r => r.Name).ToList();
                default: return rows.OrderBy(r => r.Name).ToList();
            }
        }

        private List<AntennaRow> GetCurrentAntennaRows()
        {
            var rows = new List<AntennaRow>();
            if (selectedVessel != null)
            {
                // Always resolve the current live comm node (cached reference can go stale)
                var liveNode = selectedVessel.Connection?.Comm as RACommNode;
                // If not ready, trigger discovery and retry onc
                if ((liveNode == null || liveNode.RAAntennaList == null || liveNode.RAAntennaList.Count == 0) &&
                        selectedVessel.Connection is RACommNetVessel cnv)
                {
                    cnv.DiscoverAntennas();
                    liveNode = selectedVessel.Connection?.Comm as RACommNode;
                }
                // Keep the cached field in sync
                selectedVesselNode = liveNode;

                if (liveNode?.RAAntennaList == null || liveNode.RAAntennaList.Count == 0)
                    return rows;

                for (int i = 0; i < selectedVesselNode.RAAntennaList.Count; i++)
                {
                    RealAntenna ant = selectedVesselNode.RAAntennaList[i];
                    string key = null;
                    var mra = ant?.Parent as ModuleRealAntenna; // RealAntenna.Parent is often the module
                    if (mra != null && mra.part != null)
                    {
                        uint flightId = mra.part.flightID;
                        int modIdx = -1;
                        try
                        {
                            modIdx = mra.part.Modules.IndexOf(mra);
                        }
                        catch
                        {
                            modIdx = -1;
                        }
                        if (flightId != 0 && modIdx >= 0)
                            key = "VM:" + selectedVessel.id + ":" + flightId + ":" + modIdx;
                    }
                    if (string.IsNullOrEmpty(key))
                        key = BuildVesselAntennaKey(selectedVessel, i);
                    rows.Add(new AntennaRow { Key = key, Antenna = ant });
                }
            }
            else if (selectedStation != null && selectedStation.Comm != null)
            {
                for (int i = 0; i < selectedStation.Comm.RAAntennaList.Count; i++)
                {
                    rows.Add(new AntennaRow
                    {
                        Key = BuildStationAntennaKey(selectedStation, i), // existing behavior 
                        Antenna = selectedStation.Comm.RAAntennaList[i],
                        StationIndex = i
                    });
                }
            }
            return rows;
        }

        private void EnsureFilterStateDictionaries(List<VesselRow> basis)
        {
            var bands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < basis.Count; i++)
            {
                var antennas = basis[i].Node.RAAntennaList;
                for (int j = 0; j < antennas.Count; j++)
                    if (antennas[j].RFBand != null && !string.IsNullOrEmpty(antennas[j].RFBand.name))
                        bands.Add(antennas[j].RFBand.name);
            }

            var rfKeysToRemove = new List<string>();
            foreach (string k in rfBandFilterStates.Keys)
                if (!bands.Contains(k)) rfKeysToRemove.Add(k);
            for (int i = 0; i < rfKeysToRemove.Count; i++) rfBandFilterStates.Remove(rfKeysToRemove[i]);
            foreach (string b in bands)
                if (!rfBandFilterStates.ContainsKey(b)) rfBandFilterStates[b] = false;

            if (!subnetFilterStates.ContainsKey(0)) subnetFilterStates[0] = false;
            if (Registry != null)
                foreach (var kvp in Registry.Subnets)
                    if (!subnetFilterStates.ContainsKey(kvp.Key)) subnetFilterStates[kvp.Key] = false;

            var snKeysToRemove = new List<int>();
            foreach (int k in subnetFilterStates.Keys)
                if (k != 0 && (Registry == null || !Registry.HasSubnet(k))) snKeysToRemove.Add(k);
            for (int i = 0; i < snKeysToRemove.Count; i++) subnetFilterStates.Remove(snKeysToRemove[i]);
        }

        private bool AnyRfBandFilterOn()
        {
            foreach (var kv in rfBandFilterStates) if (kv.Value) return true;
            return false;
        }

        private bool AnySubnetFilterOn()
        {
            foreach (var kv in subnetFilterStates) if (kv.Value) return true;
            return false;
        }

        private void SetAllRfBandFilters(bool v)
        {
            foreach (var k in rfBandFilterStates.Keys.ToList()) rfBandFilterStates[k] = v;
        }

        private void SetAllSubnetFilters(bool v)
        {
            foreach (var k in subnetFilterStates.Keys.ToList()) subnetFilterStates[k] = v;
        }

        private bool GetSubnetFilter(int idx) => subnetFilterStates.TryGetValue(idx, out bool v) && v;
        private void SetSubnetFilter(int idx, bool v) => subnetFilterStates[idx] = v;

        private string GetSubnetFilterSummary()
        {
            var parts = new List<string>();
            if (GetSubnetFilter(0)) parts.Add("Public");
            foreach (var kvp in ActiveSubnets)
                if (GetSubnetFilter(kvp.Key)) parts.Add(kvp.Value);
            if (parts.Count == 0) return "All";
            if (parts.Count <= 2) return string.Join(", ", parts);
            return parts[0] + ", +" + (parts.Count - 1);
        }

        #endregion

        // =====================================================================
        #region Summaries
        // =====================================================================
        private string SummarizeVesselSubnets(RACommNode node) =>
            string.Join(",", node.RAAntennaList
                .Select(a => RASubnets.NamedMaskSummary(a.SubnetMask)).Distinct().ToArray());

        private string SummarizeVesselRFBands(RACommNode node) =>
            string.Join(",", node.RAAntennaList
                .Where(a => a.RFBand != null && !string.IsNullOrEmpty(a.RFBand.name))
                .Select(a => a.RFBand.name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray());

        private string SummarizeStationSubnets(Network.RACommNetHome s) =>
            string.Join(",", s.Comm.RAAntennaList
                .Select(a => RASubnets.NamedMaskSummary(a.SubnetMask)).Distinct().ToArray());

        private string SummarizeStationRFBands(Network.RACommNetHome s) =>
            string.Join(",", s.Comm.RAAntennaList
                .Where(a => a.RFBand != null && !string.IsNullOrEmpty(a.RFBand.name))
                .Select(a => a.RFBand.name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());

        #endregion

        // =====================================================================
        #region Body tree
        // =====================================================================

        private List<BodyNode> BuildBodyTree()
        {
            var included = new HashSet<CelestialBody>();
            var counts = new Dictionary<CelestialBody, int>();

            if (tab == TopTab.Vessels)
            {
                foreach (var row in GetVisibleVessels(includeRfSubnetFiltering: false))
                {
                    AddBodyChain(row.Vessel.mainBody, included);
                    if (row.Vessel.mainBody != null)
                        counts[row.Vessel.mainBody] = counts.TryGetValue(row.Vessel.mainBody, out int c) ? c + 1 : 1;
                }
            }
            else if (RACommNetScenario.GroundStations != null)
            {
                foreach (var s in RACommNetScenario.GroundStations.Values)
                {
                    var body = s?.Comm?.ParentBody;
                    AddBodyChain(body, included);
                    if (body != null)
                        counts[body] = counts.TryGetValue(body, out int c) ? c + 1 : 1;
                }
            }

            var roots = FlightGlobals.Bodies
                .Where(b => b != null && included.Contains(b) && GetParentBody(b) == null)
                .OrderBy(b => GetDisplayBodyName(b), StringComparer.OrdinalIgnoreCase).ToList();

            var res = new List<BodyNode>();
            foreach (var r in roots) AppendBodyNode(res, r, 0, included, counts);
            return res;
        }

        private void AddBodyChain(CelestialBody body, HashSet<CelestialBody> set)
        {
            while (body != null) { if (!set.Add(body)) break; body = GetParentBody(body); }
        }

        private void AppendBodyNode(List<BodyNode> res, CelestialBody body, int depth,
            HashSet<CelestialBody> included, Dictionary<CelestialBody, int> counts)
        {
            counts.TryGetValue(body, out int count);
            res.Add(new BodyNode { Body = body, Depth = depth, Count = count });
            foreach (var child in FlightGlobals.Bodies
                .Where(b => b != null && included.Contains(b) && GetParentBody(b) == body)
                .OrderBy(b => GetDisplayBodyName(b), StringComparer.OrdinalIgnoreCase))
                AppendBodyNode(res, child, depth + 1, included, counts);
        }

        private CelestialBody GetParentBody(CelestialBody body)
        {
            if (body == null) return null;
            try
            {
                if (body.orbit?.referenceBody != null) return body.orbit.referenceBody;
                if (body.orbitDriver?.orbit?.referenceBody != null) return body.orbitDriver.orbit.referenceBody;
            }
            catch { }
            return null;
        }

        private string GetDisplayBodyName(CelestialBody body)
        {
            if (body == null) return string.Empty;
            if (body.isStar &&
                string.Equals(body.bodyName, "Sun", StringComparison.OrdinalIgnoreCase) &&
                IsStockKerbolSystem())
                return "Kerbol";
            return body.bodyName;
        }

        private bool IsStockKerbolSystem()
        {
            try
            {
                var k = FlightGlobals.GetBodyByName("Kerbin");
                if (k?.orbit?.referenceBody == null) return false;
                return string.Equals(k.orbit.referenceBody.bodyName, "Sun", StringComparison.OrdinalIgnoreCase)
                    && FlightGlobals.GetBodyByName("Earth") == null;
            }
            catch { return false; }
        }

        #endregion

        // =====================================================================
        #region Helpers — antenna resolution, planner
        // =====================================================================

        private void LaunchPlanningGUI(RealAntenna antenna)
        {
            if (antenna == null) return;
            var parentModule = antenna.Parent as ModuleRealAntenna;
            var existing = FindObjectsOfType<PlannerGUI>()
                .FirstOrDefault(pg => pg?.primaryAntenna == antenna);
            if (existing != null) { existing.RequestUpdate = true; return; }

            var go = new GameObject(antenna.Name + "-Planning");
            DontDestroyOnLoad(go);
            var planner = go.AddComponent<PlannerGUI>();
            planner.primaryAntenna = antenna;
            planner.parentPartModule = parentModule;
            var homes = RACommNetScenario.GroundStations?.Values.Where(x => x?.Comm is RACommNode)
                        ?? Enumerable.Empty<Network.RACommNetHome>();
            planner.fixedAntenna = planner.GetBestMatchingGroundStation(antenna, homes) ?? antenna;
            planner.RequestUpdate = true;
        }

        private bool TryResolveModuleForVesselAntenna(string key, RealAntenna ant, out ModuleRealAntenna mra)
        {
            mra = null;
            if (ant == null || string.IsNullOrEmpty(key) || !key.StartsWith("V:")) return false;
            var parts = key.Split(':');
            if (parts.Length != 3 || !Guid.TryParse(parts[1], out Guid vesselId)) return false;
            Vessel v = FlightGlobals.Vessels.FirstOrDefault(x => x?.id == vesselId);
            if (v == null) return false;

            mra = ant.Parent as ModuleRealAntenna;
            if (mra != null) return true;

            string antName = ant.Name ?? string.Empty;
            string bandName = ant.RFBand?.name ?? string.Empty;
            var shape = ant.Shape;

            foreach (Part p in v.parts)
            {
                if (p == null) continue;
                foreach (var cand in p.FindModulesImplementing<ModuleRealAntenna>())
                {
                    if (cand == null) continue;
                    if (ReferenceEquals(cand.RAAntenna, ant)) { mra = cand; return true; }
                    var ra = cand.RAAntenna;
                    if (ra == null) continue;
                    if (string.Equals(ra.Name ?? string.Empty, antName, StringComparison.Ordinal) &&
                        ra.Shape == shape &&
                        string.Equals(ra.RFBand?.name ?? string.Empty, bandName, StringComparison.Ordinal))
                    { mra = cand; return true; }
                }
            }
            return false;
        }

        private bool TryGetAntennaByKey(string key, out RealAntenna antenna)
        {
            antenna = null;
            if (string.IsNullOrEmpty(key)) return false;

            // --- New stable vessel-module key ---
            // Format: VM:<vesselGuid>:<partFlightId>:<moduleIndex>
            if (key.StartsWith("VM:"))
            {
                var p = key.Split(':');
                if (p.Length == 4 &&
                    Guid.TryParse(p[1], out Guid vid) &&
                    uint.TryParse(p[2], out uint flightId) &&
                    int.TryParse(p[3], out int modIdx))
                {
                    var v = FlightGlobals.Vessels.FirstOrDefault(x => x?.id == vid);
                    if (v == null) return false;

                    // Find the part by flightID
                    Part part = null;
                    try { part = v.parts?.FirstOrDefault(pp => pp != null && pp.flightID == flightId); }
                    catch { part = null; }

                    if (part == null) return false;

                    // Resolve ModuleRealAntenna by module index
                    ModuleRealAntenna mra = null;
                    try
                    {
                        if (modIdx >= 0 && modIdx < part.Modules.Count)
                            mra = part.Modules[modIdx] as ModuleRealAntenna;
                    }
                    catch
                    {
                        mra = null;
                    }

                    if (mra == null) return false;

                    // Prefer the module's antenna instance if available
                    if (mra.RAAntenna != null)
                    {
                        antenna = mra.RAAntenna;
                        return true;
                    }

                    // Fallback: try to find a matching RealAntenna in the vessel comm node by Parent reference
                    try
                    {
                        if (v.Connection is RACommNetVessel cnv && cnv.Comm is RACommNode n && n.RAAntennaList != null)
                        {
                            var match = n.RAAntennaList.FirstOrDefault(a => a != null && ReferenceEquals(a.Parent, mra));
                            if (match != null)
                            {
                                antenna = match;
                                return true;
                            }
                        }
                    }
                    catch { }

                    return false;
                }
                return false;
            }

            if (key.StartsWith("V:"))
            {
                var p = key.Split(':');
                if (p.Length == 3 && Guid.TryParse(p[1], out Guid vid) && int.TryParse(p[2], out int idx))
                {
                    var v = FlightGlobals.Vessels.FirstOrDefault(x => x?.id == vid);
                    if (v?.Connection is RACommNetVessel cnv &&
                        cnv.Comm is RACommNode n && n.RAAntennaList != null && idx >= 0 && idx < n.RAAntennaList.Count)
                    {
                        antenna = n.RAAntennaList[idx];
                        return antenna != null;
                    }
                }
            }

            if (key.StartsWith("S:"))
            {
                var p = key.Split(':');
                if (p.Length == 3 && int.TryParse(p[2], out int idx) &&
                    RACommNetScenario.GroundStations?.TryGetValue(p[1], out var sta) == true &&
                    sta?.Comm?.RAAntennaList != null && idx >= 0 && idx < sta.Comm.RAAntennaList.Count)
                {
                    antenna = sta.Comm.RAAntennaList[idx];
                    return antenna != null;
                }
            }

            return false;
        }

        private string GetCompactTargetName(RealAntenna antenna)
        {
            var tgt = antenna?.Target;
            if (tgt == null) return string.Empty;

            try
            {
                if (tgt is RealAntennas.Targeting.AntennaTargetVessel tv)
                    return tv.vessel != null ? tv.vessel.vesselName : string.Empty;

                if (tgt is RealAntennas.Targeting.AntennaTargetLatLonAlt tlla)
                    return !string.IsNullOrEmpty(tlla.bodyName) ? tlla.bodyName : tlla.ToString();

                if (tgt is RealAntennas.Targeting.AntennaTargetAzEl taz)
                    return taz.vessel != null ? "Az/El (" + taz.vessel.vesselName + ")" : "Az/El";

                if (tgt is RealAntennas.Targeting.AntennaTargetOrbitRelative tor)
                    return tor.vessel != null ? "OrbitRel (" + tor.vessel.vesselName + ")" : "OrbitRel";

                return tgt.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private string BuildVesselAntennaKey(Vessel vessel, int index) =>
            "V:" + vessel.id + ":" + index;

        private string BuildStationAntennaKey(Network.RACommNetHome s, int i) =>
            "S:" + s.nodeName + ":" + i;

        private void DrawPanelFooterSpacer()
        {
            GUILayout.BeginHorizontal(panelBar, GUILayout.Height(PanelBarH));
            GUILayout.EndHorizontal();
        }

        private void DrawListSummary(int count)
        {
            GUILayout.Label($"{count} shown", mutedLabel);
        }

        #endregion

        // =====================================================================
        #region Swatches & textures
        // =====================================================================
        private void EnsurePublicOutlineSwatch()
        {
            if (publicOutlineSwatch != null) return;
            // Transparent center, light outline for "Public".
            publicOutlineSwatch = MakeOutlineTex(new Color(0.9f, 0.95f, 1f, 0.9f), new Color(0, 0, 0, 0));
        }

        private Texture2D GetSubnetSwatch(int bit)
        {
            if (bit == 0) return publicOutlineSwatch;

            // NOTE: Use ReferenceEquals to avoid UnityEngine.Object overloaded null semantics.
            if (swatchTexCache.TryGetValue(bit, out Texture2D cached) && !ReferenceEquals(cached, null))
                return cached;

            Color c;
            Texture2D t;
            if (Registry != null && Registry.TryGetSubnetColor(bit, out c))
                t = MakeSolidTex(c);
            else
                t = MakeSolidTex(RASubnets.SubnetColor(bit, 1f));

            swatchTexCache[bit] = t;
            return t;
        }

        private void InvalidateSwatchCache()
        {
            swatchTexCache.Clear();
            maskSwatchCache.Clear();
            SafeDestroy(ref publicOutlineSwatch);
        }

        private void EnsureBodyIcons()
        {
            if (texStar != null) return;
            texStar = LoadIconFromSprites(new[] { "OrbitIcons_Sun", "OrbitIcons_Star", "OrbitIcons_S" });
            texPlanet = LoadIconFromSprites(new[] { "OrbitIcons_Planet", "OrbitIcons_AT", "OrbitIcons_GG", "OrbitIcons_Unknown" });
            texMoon = LoadIconFromSprites(new[] { "OrbitIcons_Moon", "OrbitIcons_T", "OrbitIcons_Unknown" });
            if (texStar == null) texStar = MakeCircleTex(new Color(1.00f, 0.85f, 0.15f), 12);
            if (texPlanet == null) texPlanet = MakeCircleTex(new Color(0.25f, 0.60f, 1.00f), 12);
            if (texMoon == null) texMoon = MakeCircleTex(new Color(0.75f, 0.75f, 0.80f), 12);
        }

        private Texture2D LoadIconFromSprites(string[] names)
        {
            try
            {
                var sprites = Resources.FindObjectsOfTypeAll<Sprite>();
                if (sprites == null) return null;
                foreach (string name in names)
                {
                    var s = sprites.FirstOrDefault(x => x?.name == name);
                    if (s != null) return Targeting.TextureTools.TextureFromSprite(s);
                }
            }
            catch { }
            return null;
        }

        private Texture2D MakeSolidTex(Color c)
        {
            var t = new Texture2D(12, 12, TextureFormat.RGBA32, false);
            var pix = new Color[144];
            for (int i = 0; i < 144; i++) pix[i] = c;
            t.SetPixels(pix);
            t.Apply(false, true);
            return t;
        }

        // Function to create a circular texture
        public static Texture2D MakeCircleTex(Color color, int diameter)
        {
            Texture2D texture = new Texture2D(diameter, diameter, TextureFormat.RGBA32, false);

            float radius = diameter / 2f;
            Vector2 center = new Vector2(radius, radius);

            for (int y = 0; y < diameter; y++)
            {
                for (int x = 0; x < diameter; x++)
                {
                    // Calculate distance from center
                    float distance = Vector2.Distance(new Vector2(x, y), center);
                    if (distance <= radius)
                        texture.SetPixel(x, y, color); // Inside the circle
                    else
                        texture.SetPixel(x, y, Color.clear); // Outside the circle
                }
            }

            texture.Apply(); // Apply all SetPixel changes
            return texture;
        }

        private Texture2D MakeOutlineTex(Color border, Color fill)
        {
            int w = 12, h = 12;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var pix = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool edge = (x == 0 || y == 0 || x == w - 1 || y == h - 1);
                    pix[y * w + x] = edge ? border : fill;
                }
            }
            t.SetPixels(pix);
            t.Apply(false, true);
            return t;
        }

        private static void SafeDestroy(ref Texture2D tex)
        {
            if (tex != null)
            {
                UnityEngine.Object.Destroy(tex);
                tex = null;
            }
        }

        private Texture2D IconForBody(CelestialBody body)
        {
            if (body == null) return texPlanet;
            if (bodyIconCache.TryGetValue(body.bodyName, out Texture2D cached) && cached != null)
                return cached;

            var custom = TryLoadOrbitIconTexture(body);
            if (custom != null) { bodyIconCache[body.bodyName] = custom; return custom; }

            if (body.isStar) return texStar;
            return GetParentBody(body)?.isStar == true ? texPlanet : texMoon;
        }

        private Texture2D TryLoadOrbitIconTexture(CelestialBody body)
        {
            string path = TryGetOrbitIconPathFromRuntime(body);
            if (string.IsNullOrEmpty(path))
            {
                EnsureKopernicusIconPathCache();
                kopernicusIconPathCache?.TryGetValue(body.bodyName, out path);
            }
            if (string.IsNullOrEmpty(path)) return null;

            string norm = NormalizeGameDatabaseTexturePath(path);
            return string.IsNullOrEmpty(norm) ? null : GameDatabase.Instance?.GetTexture(norm, false);
        }

        private string TryGetOrbitIconPathFromRuntime(CelestialBody body)
        {
            try
            {
                object orb = body.orbit ?? (object)(body.orbitDriver?.orbit);
                if (orb == null) return null;
                var t = orb.GetType();
                foreach (string field in new[] { "iconTexture", "iconTexturePath" })
                {
                    var pi = t.GetProperty(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pi?.PropertyType == typeof(string)) return pi.GetValue(orb, null) as string;
                    var fi = t.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fi?.FieldType == typeof(string)) return fi.GetValue(orb) as string;
                }
            }
            catch { }
            return null;
        }

        private void EnsureKopernicusIconPathCache()
        {
            if (kopernicusIconPathCache != null) return;
            kopernicusIconPathCache = new Dictionary<string, string>(64);
            try
            {
                foreach (ConfigNode root in GameDatabase.Instance?.GetConfigNodes("Kopernicus") ?? new ConfigNode[0])
                    foreach (ConfigNode bn in root?.GetNodes("Body") ?? new ConfigNode[0])
                    {
                        string bName = bn?.GetValue("name");
                        string iPath = bn?.GetNode("Orbit")?.GetValue("iconTexture");
                        if (!string.IsNullOrEmpty(bName) && !string.IsNullOrEmpty(iPath) &&
                            !kopernicusIconPathCache.ContainsKey(bName))
                            kopernicusIconPathCache.Add(bName, iPath);
                    }
            }
            catch { }
        }

        private string NormalizeGameDatabaseTexturePath(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string s = raw.Trim().Replace('\\', '/');
            if (s.StartsWith("GameData/", StringComparison.OrdinalIgnoreCase)) s = s.Substring(9);
            int q = s.IndexOf('?'); if (q >= 0) s = s.Substring(0, q);
            string ext = Path.GetExtension(s);
            if (!string.IsNullOrEmpty(ext)) s = s.Substring(0, s.Length - ext.Length);
            return s;
        }

        private void EnsureUIIcons()
        {
            if (gcPlan != null) return;
            icoPlan = GameDatabase.Instance.GetTexture("RealAntennas/plan", false) ?? Texture2D.whiteTexture;
            icoDish = GameDatabase.Instance.GetTexture("RealAntennas/dish", false) ?? Texture2D.whiteTexture;
            icoOmni = GameDatabase.Instance.GetTexture("RealAntennas/omni", false) ?? Texture2D.whiteTexture;
            //icoDish = GameDatabase.Instance.GetTexture("Squad/PartList/SimpleIcons/deployable_comms_part", false) ?? Texture2D.whiteTexture;
            //icoOmni = GameDatabase.Instance.GetTexture("Squad/PartList/SimpleIcons/deployable_antenna", false) ?? Texture2D.whiteTexture;
            icoPaint = GameDatabase.Instance.GetTexture("RealAntennas/paint", false) ?? Texture2D.whiteTexture;
            icoTrash = GameDatabase.Instance.GetTexture("RealAntennas/trash", false) ?? Texture2D.whiteTexture;
            icoPencil = GameDatabase.Instance.GetTexture("RealAntennas/pencil", false) ?? Texture2D.whiteTexture;
            gcPlan = new GUIContent(icoPlan, "Analyze link quality / signal strength"); //"Planner \\u2197\", 
            gcDish = new GUIContent(icoDish, "Dish antenna");
            gcOmni = new GUIContent(icoOmni, "Omni antenna");
            gcPaint = new GUIContent(icoPaint, "Change subnet color");
            gcPencil = new GUIContent(icoPencil, "Rename subnet");
            gcTrash = new GUIContent(icoTrash, "Delete subnet");
        }


        // Draw a small tooltip near the mouse cursor for any control that sets GUIContent.tooltip.
        // KSP/Unity IMGUI does not automatically render tooltips, so we do it ourselves.
        private void DrawTooltip()
        {
            string tip = GUI.tooltip;
            if (string.IsNullOrEmpty(tip) || tooltipBox == null) return;

            // Measure with a max width so long tips wrap.
            float maxW = SF(360f);
            var content = new GUIContent(tip);
            float w = Mathf.Min(maxW, tooltipBox.CalcSize(content).x);
            w = Mathf.Max(SF(120f), w);
            float h = tooltipBox.CalcHeight(content, w);

            // Mouse position is in the current window's GUI space while drawing the window.
            Vector2 mp = Event.current.mousePosition;
            float x = mp.x + SF(16f);
            float y = mp.y + SF(20f);

            // Clamp inside the window rectangle.
            float pad = SF(8f);
            if (x + w > windowRect.width - pad) x = windowRect.width - w - pad;
            if (y + h > windowRect.height - pad) y = windowRect.height - h - pad;
            if (x < pad) x = pad;
            if (y < pad) y = pad;

            int prevDepth = GUI.depth;
            GUI.depth = -1000;
            GUI.Box(new Rect(x, y, w, h), tip, tooltipBox);
            GUI.depth = prevDepth;
        }

        #endregion
    }
}
