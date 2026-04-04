using System.Text;
using UnityEngine;
using ClickThroughFix;
using System.Linq;

namespace RealAntennas
{
    /// <summary>
    /// RA Subnets (Constellations)
    ///
    /// Policies:
    /// - Link-to-link enforcement only (no end-to-end subnet state).
    /// - 32 subnets (0..31) encoded in a 32-bit mask.
    /// - Subnet 0 is Public wildcard, BUT Public is mutually exclusive with non-public selection:
    ///     * If any non-public subnet is selected, Public is cleared.
    ///     * If no non-public subnet is selected, Public is enabled.
    /// - Halo rendering:
    ///     * No halo is drawn for Public links (subnet 0), preserving stock/current RA visuals
    ///       unless user opts into non-public subnets.
    /// </summary>
    public static class RASubnets
    {
        public const int MaxSubnets = 32;
        public const int PublicSubnet = 0;
        public const uint PublicBit = 1u << PublicSubnet;

        // Wired at runtime from MapUI.Settings by RACommNetUI
        public static bool EnableSubnets = true;

        /// <summary>
        /// Normalize mask to enforce mutual exclusivity rule:
        /// - If any non-public bits are set, Public bit is cleared.
        /// - If no non-public bits are set, Public bit is set.
        /// </summary>
        public static uint NormalizeMask(uint mask)
        {
            uint nonPublic = mask & ~PublicBit;
            if (nonPublic != 0u) return nonPublic; // explicit non-public => public off
            return PublicBit;                      // otherwise => public on
        }

        public static bool IsPublicOnly(uint mask) => NormalizeMask(mask) == PublicBit;

        public static int LowestSetBit(uint mask)
        {
            if (mask == 0u) return -1;
            int bit = 0;
            while ((mask & 1u) == 0u) { mask >>= 1; bit++; }
            return bit;
        }

        /// <summary>
        /// Link-to-link subnet match. Public-only matches anything.
        /// If subnets disabled => always match.
        /// </summary>
        public static bool SubnetMatch(uint aMask, uint bMask)
        {
            if (!EnableSubnets) return true;
            uint a = NormalizeMask(aMask);
            uint b = NormalizeMask(bMask);
            // Public-only matches Public-only only.
            if (a == PublicBit || b == PublicBit) return (a == PublicBit && b == PublicBit);
            return (a & b) != 0u;
        }

        /// <summary>
        /// Pick a single subnet id to label/color a link.
        /// Rules:
        /// - If either side is Public-only and the other has non-public bits, return the other's lowest bit.
        /// - Else return the lowest set bit of the intersection.
        /// - If no intersection (shouldn't happen if caller ensured match), return Public.
        /// </summary>


        private static int PickByPriority(uint intersection)
        {
            var registry = SubnetManagerScenario.Instance;
            var order = registry?.SubnetPriority;
            if (order != null)
            {
                for (int i = 0; i < order.Count; i++)
                {
                    int bit = order[i];
                    if (bit <= 0 || bit >= MaxSubnets) continue;
                    if ((intersection & (1u << bit)) != 0u) return bit;
                }
            }
            return PublicSubnet;
        }

        public static int PickLinkSubnet(uint aMask, uint bMask)
        {
            if (!EnableSubnets) return PublicSubnet;
            uint a = NormalizeMask(aMask);
            uint b = NormalizeMask(bMask);

            uint inter = (a & b);
            if (inter == 0u) return PublicSubnet;

            if ((inter & (inter - 1u)) == 0u)
                return (inter == PublicBit) ? PublicSubnet : LowestSetBit(inter);

            return PickByPriority(inter);
        }

        /// <summary>
        /// Compact bit-number summary used in vessel/station list columns for brevity.
        /// e.g. "Public", "SN:1,3"
        /// </summary>
        public static string MaskSummary(uint mask)
        {
            uint normalizedMask = NormalizeMask(mask);
            if (normalizedMask == PublicBit) return "Public";

            var sb = new StringBuilder("SN:");
            bool first = true;
            for (int subnetbit = 1; subnetbit < 32; subnetbit++)
            {
                if ((normalizedMask & (1u << subnetbit)) != 0u)
                {
                    if (!first) sb.Append(",");
                    sb.Append(subnetbit);
                    first = false;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Human-readable summary that resolves bit numbers to registered subnet names.
        /// Used in PAW SubnetSummary field and SubnetEditorGUI header.
        /// Falls back to "SN:N" for any bit that has no registered name.
        /// Requires SubnetManagerScenario.Instance to be available; if not, falls back to MaskSummary.
        /// </summary>
        public static string NamedMaskSummary(uint mask)
        {
            uint normalizedMask = NormalizeMask(mask);
            if (normalizedMask == PublicBit) return "Public";

            var registry = SubnetManagerScenario.Instance;
            var sb = new StringBuilder();
            bool first = true;

            var order = registry?.SubnetPriority;
            if (order != null && order.Count > 0)
            {
                for (int i = 0; i < order.Count; i++)
                {
                    int bit = order[i];
                    if (bit <= 0 || bit >= 32) continue;
                    if ((normalizedMask & (1u << bit)) == 0u) continue;
                    if (!first) sb.Append(", ");
                    if (registry != null && registry.HasSubnet(bit)) sb.Append(registry.Subnets[bit]);
                    else sb.Append("SN:").Append(bit);
                    first = false;
                }
                for (int bit = 1; bit < 32; bit++)
                {
                    if ((normalizedMask & (1u << bit)) == 0u) continue;
                    if (order.Contains(bit)) continue;
                    if (!first) sb.Append(", ");
                    if (registry != null && registry.HasSubnet(bit)) sb.Append(registry.Subnets[bit]);
                    else sb.Append("SN:").Append(bit);
                    first = false;
                }
                return sb.ToString();
            }

            for (int bit = 1; bit < 32; bit++)
            {
                if ((normalizedMask & (1u << bit)) == 0u) continue;
                if (!first) sb.Append(", ");
                if (registry != null && registry.HasSubnet(bit)) sb.Append(registry.Subnets[bit]);
                else sb.Append("SN:").Append(bit);
                first = false;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Deterministic subnet color. Public (0) is gray, but note: halo is not drawn for Public.
        ///
        /// The signal-strength line already occupies the red-yellow-green hue band (0.0-0.40).
        /// To keep subnet halos visually distinct from that gradient, the palette is anchored
        /// in the blue-cyan-violet range and steps through hues that never overlap with the
        /// signal colors.  The first 8 slots use a hand-tuned fixed palette; slots 9+ fall
        /// back to a golden-ratio walk starting from cyan so they stay in the cool range.
        /// </summary>
        public static Color SubnetColor(int subnet, float alpha = 1f)
        {
            if (subnet == PublicSubnet)
            {
                var c = new Color(0.85f, 0.85f, 0.85f, alpha);
                return c;
            }

            var registry = SubnetManagerScenario.Instance;
            if (registry != null && registry.TryGetSubnetColor(subnet, out Color user))
            {
                user.a = alpha;
                return user;
            }

            float hue;
            switch (subnet)
            {
                case 1: hue = 0.55f; break;
                case 2: hue = 0.68f; break;
                case 3: hue = 0.78f; break;
                case 4: hue = 0.88f; break;
                case 5: hue = 0.48f; break;
                case 6: hue = 0.60f; break;
                case 7: hue = 0.73f; break;
                case 8: hue = 0.83f; break;
                default:
                    hue = Mathf.Repeat(0.55f + (subnet - 9) * 0.61803398875f, 1f);
                    break;
            }
            Color c2 = Color.HSVToRGB(hue, 0.85f, 1.00f);
            c2.a = alpha;
            return c2;
        }

        // ------------------ Checkbox GUI (multi-select, PAW) ------------------

        public class SubnetEditorGUI : MonoBehaviour
        {
            const string GUIName = "Antenna Subnets";
            Rect Window = new Rect(80, 140, 380, 460);
            Vector2 scroll;

            public ModuleRealAntenna parent;
            public RealAntenna antenna;

            uint workingMask;

            public void Init(ModuleRealAntenna m, RealAntenna a)
            {
                parent = m;
                antenna = a;
                workingMask = NormalizeMask(a?.SubnetMask ?? PublicBit);
            }

            public void OnGUI()
            {
                GUI.skin = HighLogic.Skin;
                Window = ClickThruBlocker.GUILayoutWindow(GetHashCode(), Window, Draw, GUIName, HighLogic.Skin.window);
            }

            void Draw(int id)
            {
                GUILayout.BeginVertical(HighLogic.Skin.box);
                GUILayout.Label($"Antenna: {antenna?.Name ?? "(null)"}");
                GUILayout.Label($"Band: {antenna?.RFBand?.name ?? "(null)"}");
                GUILayout.Label($"Selected: {NamedMaskSummary(workingMask)}");
                GUILayout.EndVertical();

                GUILayout.Space(6);

                scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(300));

                // Public checkbox (mutually exclusive with all private subnets)
                bool isPublic = (NormalizeMask(workingMask) == PublicBit);
                bool publicNext = GUILayout.Toggle(isPublic, "Public");
                if (publicNext && !isPublic)
                    workingMask = PublicBit;

                // Private subnet checkboxes — names come from the global registry
                var registry = SubnetManagerScenario.Instance;
                if (registry != null)
                {
                    foreach (var kvp in registry.SubnetsByPriority())
                    {
                        int i = kvp.Key;
                        bool on = (workingMask & (1u << i)) != 0u;
                        bool next = GUILayout.Toggle(on, kvp.Value);

                        if (next != on)
                        {
                            if (next)
                            {
                                workingMask &= ~PublicBit;
                                workingMask |= (1u << i);
                            }
                            else
                            {
                                workingMask &= ~(1u << i);
                                if ((workingMask & ~PublicBit) == 0u)
                                    workingMask = PublicBit;
                            }
                        }
                    }
                }

                GUILayout.EndScrollView();

                GUILayout.Space(8);

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Apply"))
                {
                    uint finalMask = NormalizeMask(workingMask);

                    if (antenna != null) antenna.SubnetMask = finalMask;

                    if (parent != null)
                    {
                        parent.SubnetMask = finalMask;
                        parent.SubnetSummary = NamedMaskSummary(finalMask);

                        // Force update of vessel/network
                        if (HighLogic.LoadedSceneIsEditor || HighLogic.LoadedSceneIsFlight)
                            GameEvents.onVesselWasModified.Fire(parent.vessel);

                        MonoUtilities.RefreshPartContextWindow(parent.part);
                    }

                    Close();
                }

                if (GUILayout.Button("Close"))
                    Close();

                GUILayout.EndHorizontal();

                GUI.DragWindow();
            }

            void Close()
            {
                Destroy(this);
                gameObject.DestroyGameObject();
            }
        }
    }
}
