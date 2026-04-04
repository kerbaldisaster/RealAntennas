
using ClickThroughFix;
using System;
using UnityEngine;

namespace RealAntennas.MapUI
{
    public class NetUIConfigurationWindow : MonoBehaviour
    {
        private Rect winPos = new Rect(Screen.width - 460, Screen.height - 520, 450, 480);
        private Vector2 scrollPos = Vector2.zero;
        private GUIStyle titleStyle;
        public const string ModTag = "[RealAntennas.NetUIConfigurationWindow]";

        public void OnGUI()
        {
            winPos = ClickThruBlocker.GUILayoutWindow(GetHashCode(), winPos, WindowGUI, ModTag, GUILayout.MinWidth(320));
        }

        private void WindowGUI(int ID)
        {
            titleStyle = new GUIStyle(HighLogic.Skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
            };
            titleStyle.normal.textColor = Color.yellow;

            var settings = RACommNetScenario.MapUISettings;
            GUILayout.BeginVertical();
            scrollPos = GUILayout.BeginScrollView(scrollPos);
            GUILayout.BeginVertical(HighLogic.Skin.box);
            GUILayout.BeginHorizontal();    
            GUILayout.FlexibleSpace();
            GUILayout.Label("Visualization Toggles", titleStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label($"{RACommNetScenario.assembly.GetName().Name} v{RACommNetScenario.info.FileVersion}");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button($"ConeMode: {settings.drawConesMode}"))
            {
                settings.drawConesMode++;
                settings.drawConesMode = (RACommNetUI.DrawConesMode)((int)settings.drawConesMode % Enum.GetValues(typeof(RACommNetUI.DrawConesMode)).Length);
            }
            if (GUILayout.Button($"Link End Mode: {settings.radioPerspective}"))
            {
                settings.radioPerspective++;
                settings.radioPerspective = (RACommNetUI.RadioPerspective)((int)settings.radioPerspective % Enum.GetValues(typeof(RACommNetUI.RadioPerspective)).Length);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button($"TargetLine: {settings.drawTarget}"))
                settings.drawTarget = !settings.drawTarget;
            if (GUILayout.Button($"3dB Cones: {settings.drawCone3}"))
                settings.drawCone3 = !settings.drawCone3;
            if (GUILayout.Button($"10dB Cones: {settings.drawCone10}"))
                settings.drawCone10 = !settings.drawCone10;
            GUILayout.EndHorizontal();

            if (MapView.fetch is MapView && MapView.MapCamera is PlanetariumCamera)
            {
                GUILayout.BeginVertical();
                GUILayout.Label($"3D Drawing Distance {MapView.MapCamera.Distance:F0}, Max: {MapView.fetch.max3DlineDrawDist:F1}");
                MapView.fetch.max3DlineDrawDist = GUILayout.HorizontalSlider(MapView.fetch.max3DlineDrawDist, 100, 1e5f);
                GUILayout.EndVertical();
            }
            GUILayout.EndVertical();

            GUILayout.BeginVertical(HighLogic.Skin.box);
            GUILayout.Label("Link Rendering", titleStyle);
            
            // Width (formerly mislabeled brightness)
            GUILayout.Label($"Link Line Width: {settings.lineScaleWidth:F2}");
            settings.lineScaleWidth = GUILayout.HorizontalSlider(settings.lineScaleWidth, 1.0f, 4.0f);

            // True brightness controls
            GUILayout.Label($"Core Intensity (Alpha): {settings.coreIntensity:F2}");
            settings.coreIntensity = GUILayout.HorizontalSlider(settings.coreIntensity, 0.20f, 1.20f);

            GUILayout.EndVertical();

            GUILayout.BeginVertical(HighLogic.Skin.box);
            GUILayout.Label("Cones", titleStyle);
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            GUILayout.Label("Cone Circles");
            settings.coneCircles = Convert.ToInt32(GUILayout.HorizontalSlider(settings.coneCircles, 0, 8));
            GUILayout.EndVertical();

            GUILayout.BeginVertical();
            GUILayout.Label("Cone Opacity");
            settings.coneOpacity = GUILayout.HorizontalSlider(settings.coneOpacity, 0, 1);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.BeginVertical(HighLogic.Skin.box);
            GUILayout.Label("Subnets", titleStyle);
            if (GUILayout.Button($"Show Subnet Glow: {settings.showSubnetHalo}")) settings.showSubnetHalo = !settings.showSubnetHalo;

            GUILayout.Label($"Halo Opacity (Base): {settings.subnetHaloOpacity:F2}");
            settings.subnetHaloOpacity = GUILayout.HorizontalSlider(settings.subnetHaloOpacity, 0.00f, 0.50f);

            GUILayout.Label($"Halo Intensity (Multiplier): {settings.haloIntensity:F2}");
            settings.haloIntensity = GUILayout.HorizontalSlider(settings.haloIntensity, 0.00f, 1.50f);

            GUILayout.Label($"Halo Width Mult: {settings.subnetHaloWidthMult:F2}");
            settings.subnetHaloWidthMult = GUILayout.HorizontalSlider(settings.subnetHaloWidthMult, 1.50f, 3.50f);

            GUILayout.Space(6);
            GUILayout.Label("Matte Separator (Bloom Shield)");

            GUILayout.Label($"Matte Opacity (Base): {settings.matteOpacity:F2}");
            settings.matteOpacity = GUILayout.HorizontalSlider(settings.matteOpacity, 0.00f, 0.60f);

            GUILayout.Label($"Matte Intensity (Multiplier): {settings.matteIntensity:F2}");
            settings.matteIntensity = GUILayout.HorizontalSlider(settings.matteIntensity, 0.00f, 1.50f);

            GUILayout.Label($"Matte Width Mult: {settings.matteWidthMult:F2}");
            settings.matteWidthMult = GUILayout.HorizontalSlider(settings.matteWidthMult, 1.10f, 1.60f);

            GUILayout.EndVertical();

            GUILayout.Space(6);
            GUILayout.BeginVertical(HighLogic.Skin.box);
            GUILayout.Label("Presets", titleStyle);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Max Readability (Busy Networks)");
            if (GUILayout.Button(new GUIContent("Preset A", "What you’ll see: \nCore signal red→green stays crisp; subnet glow is present but won’t flood the core. \nUse when bloom is strong or you have lots of links crossing."), GUILayout.Width(120)))
            {
                settings.lineScaleWidth = 2.0f;
                settings.coreIntensity = 0.85f;

                settings.subnetHaloOpacity = 0.18f;
                settings.haloIntensity = 0.90f;
                settings.subnetHaloWidthMult = 2.0f;

                settings.matteOpacity = 0.40f;
                settings.matteIntensity = 1.10f;
                settings.matteWidthMult = 1.30f;
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Balanced Preset (Recommended Default)");
            if (GUILayout.Button(new GUIContent("Preset B", "What you’ll see: \nSubnet is clearly readable as a soft band; signal strength remains obvious. \nGood general-purpose look with TUFX."), GUILayout.Width(120)))
            {
                settings.lineScaleWidth = 2.6f;
                settings.coreIntensity = 0.90f;

                settings.subnetHaloOpacity = 0.25f;
                settings.haloIntensity = 1.00f;
                settings.subnetHaloWidthMult = 2.4f;

                settings.matteOpacity = 0.35f;
                settings.matteIntensity = 1.00f;
                settings.matteWidthMult = 1.28f;
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Subnet Emphasis");
            if (GUILayout.Button(new GUIContent("Preset C", "What you’ll see: \nRicher subnet glow; slightly more bloom footprint, but still controlled. \nUse when you want subnet glow to be more prominent."),GUILayout.Width(120)))
            {
                settings.lineScaleWidth = 2.8f;
                settings.coreIntensity = 0.90f;

                settings.subnetHaloOpacity = 0.30f;
                settings.haloIntensity = 1.10f;
                settings.subnetHaloWidthMult = 2.8f;

                settings.matteOpacity = 0.38f;
                settings.matteIntensity = 1.05f;
                settings.matteWidthMult = 1.28f;
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Cinematic / Screenshots");
            if (GUILayout.Button(new GUIContent("Preset D", "What you’ll see: \nStrong glow, very “pretty”, but crossings can start to bloom-merge if the view is busy. \nOnly use if the network isn’t extremely dense onscreen."), GUILayout.Width(120)))
            {
                settings.lineScaleWidth = 3.2f;
                settings.coreIntensity = 0.95f;

                settings.subnetHaloOpacity = 0.35f;
                settings.haloIntensity = 1.15f;
                settings.subnetHaloWidthMult = 3.0f;

                settings.matteOpacity = 0.42f;
                settings.matteIntensity = 1.10f;
                settings.matteWidthMult = 1.30f;
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUI.DragWindow();
        }
    }
}
