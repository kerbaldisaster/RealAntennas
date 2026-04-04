
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RealAntennas.MapUI
{
    // Persisted UI settings for map network visualization.
    // Defaults updated to TUFX-friendly "Preset B".
    public class Settings
    {
        [Persistent] public float coneOpacity = 1;
        [Persistent] public int coneCircles = 4;
        [Persistent] public RACommNetUI.DrawConesMode drawConesMode = RACommNetUI.DrawConesMode.Cone3D;
        [Persistent] public RACommNetUI.RadioPerspective radioPerspective = RACommNetUI.RadioPerspective.Transmit;
        [Persistent] public bool drawTarget = false;
        [Persistent] public bool drawCone3 = true;
        [Persistent] public bool drawCone10 = true;

        // NOTE: This is width scaling (historically labeled "brightness" in the config window).
        [Persistent] public float lineScaleWidth = 2.5f;
        [Persistent] public bool enableSubnets = true;
        [Persistent] public bool showSubnetHalo = true;
        [Persistent] public float subnetHaloOpacity = 0.25f;
        [Persistent] public float subnetHaloWidthMult = 2.4f;

        // New: true brightness controls (alpha/intensity multipliers)
        // Core alpha multiplier (signal strength line). 1 = unchanged.
        [Persistent] public float coreIntensity = 0.90f;
        // Halo alpha multiplier applied to subnetHaloOpacity.
        [Persistent] public float haloIntensity = 1.00f;
        // Matte separator alpha multiplier applied to matteOpacity.
        [Persistent] public float matteOpacity = 0.35f;
        [Persistent] public float matteIntensity = 1.00f;
        // Matte width multiplier relative to core width.
        [Persistent] public float matteWidthMult = 1.28f;
    }
}