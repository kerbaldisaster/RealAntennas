using System;
using UnityEngine;
using UnityEngine.Profiling;
using Unity.Mathematics;

namespace RealAntennas
{
    class Physics
    {
        //public static readonly double boltzmann_dBW = 10 * Math.Log10(1.38064852e-23);      //-228.59917;
        public const float boltzmann_dBW = -228.599168683097f;
        public const float boltzmann_dBm = boltzmann_dBW + 30;
        public const float MaxPointingLoss = 200;
        public const float MaxOmniGain = 5;
        public const float c = 2.998e8f;
        public const float CMB = 2.725f;
        public static double SolarLuminosity => PhysicsGlobals.SolarLuminosity > double.Epsilon ? PhysicsGlobals.SolarLuminosity : Math.Pow(Planetarium.fetch.Home.orbit.semiMajorAxis, 2.0) * 4.0 * Math.PI * PhysicsGlobals.SolarLuminosityAtHome;

        //private static readonly double path_loss_constant = 20 * Math.Log10(4 * Math.PI / (2.998 * Math.Pow(10, 8)));
        private const float path_loss_constant = -147.552435289803f;

        public static float GainFromDishDiamater(float diameter, float freq, float efficiency =1)
        {
            float gain = 0;
            if (diameter > 0 && efficiency > 0)
            {
                float wavelength = Physics.c / freq;
                gain = RATools.LogScale(9.87f * efficiency * diameter * diameter / (wavelength * wavelength));
            }
            return gain;
        }
        public static float GainFromReference(float refGain, float refFreq, float newFreq)
        {
            float gain = 0;
            if (refGain > 0)
            {
                gain = refGain;
                gain += (refGain <= MaxOmniGain) ? 0 : RATools.LogScale(newFreq / refFreq);
            }
            return gain;
        }

        public static double Beamwidth(double gain) => Beamwidth(Convert.ToSingle(gain));
        public static float Beamwidth(float gain) => math.sqrt(52525 / RATools.LinearScale(gain));

        public static double PathLoss(double distance, double frequency = 1e9)
        {
            //FSPL = 20 log D + 20 log freq + 20 log (4pi/c)
            double df = math.max(distance * frequency, 0.1);
            return (20 * math.log10(df)) + path_loss_constant;
        }
        public static float PathLoss(float distance, float frequency = 1e9f)
        {
            float df = math.max(distance * frequency, 0.1f);
            return (20 * math.log10(df)) + path_loss_constant;
        }

        public static float ReceivedPower(RealAntenna tx, RealAntenna rx, float distance, float frequency = 1e9f)
            => tx.TxPower + tx.Gain - PathLoss(distance, frequency) - PointingLoss(tx, rx.Position) - PointingLoss(rx, tx.Position) + rx.Gain;

        // Beamwidth is full side-to-side HPBW, == 0 to -10dB offset angle
        public static float PointingLoss(double angle, double beamwidth)
        //            => (angle > beamwidth) ? MaxPointingLoss : -1 * AntennaGainCurve.Evaluate(Convert.ToSingle(angle / beamwidth));
        {
            float norm = Convert.ToSingle(angle / beamwidth);
            if (norm > 1) return MaxPointingLoss;
            if (norm < 0.14f) return math.remap(0, 0.14f, 0, 0.25f, norm);
            else if (norm < 0.2f) return math.remap(0.14f, 0.2f, 0.25f, 0.5f, norm);
            else if (norm < 0.29f) return math.remap(0.2f, 0.29f, 0.5f, 1, norm);
            else if (norm < 0.41f) return math.remap(0.29f, 0.41f, 1, 2, norm);
            else if (norm < 0.5f) return math.remap(0.41f, 0.5f, 2, 3, norm);
            else if (norm < 0.57f) return math.remap(0.5f, 0.57f, 3, 4, norm);
            else if (norm < 0.61f) return math.remap(0.57f, 0.61f, 4, 4.5f, norm);
            else if (norm < 0.64f) return math.remap(0.61f, 0.64f, 4.5f, 5, norm);
            else if (norm < 0.7f) return math.remap(0.64f, 0.7f, 5, 6, norm);
            else if (norm < 0.76f) return math.remap(0.7f, 0.76f, 6, 7, norm);
            else if (norm < 0.81f) return math.remap(0.76f, 0.81f, 7, 8, norm);
            else if (norm < 0.86f) return math.remap(0.81f, 0.86f, 8, 9, norm);
            else return math.remap(0.86f, 1, 9, 10, norm);
        }
        public static float PointingLoss(RealAntenna ant, Vector3 origin)
            => (ant.CanTarget && !ant.IsTracking) ? PointingLoss(Vector3.Angle(origin - ant.Position, ant.ToTarget), ant.Beamwidth) : 0;

        public static float GainAtAngle(float gain, float angle) => gain - PointingLoss(math.abs(angle), Beamwidth(gain));
        // Beamwidth is the 3dB full beamwidth contour, ~= the offset angle to the 10dB contour.
        // 10dBi: Beamwidth = 72 = 4dB full beamwidth contour
        // 10dBi @ .6 efficiency: 57 = 3dB full beamwidth contour
        // 20dBi: Beamwidth = 23 = 4dB full beamwidth countour
        // 20dBi @ .6 efficiency: Beamwidth = 17.75 = 3dB full beamwidth contour

        // Sun Temp vs Freq from https://deepspace.jpl.nasa.gov/dsndocs/810-005/Binder/810-005_Binder_Change51.pdf Manual 105 Eq 14: T = 5672 * lambda ^ 0.24517, lambda units is mm
        public static float StarRadioTemp(float surfaceTemp, float frequency) => surfaceTemp * math.pow(1000 * c / frequency, .24517f);   // QUIET Sun Temp, active can be 2-3x higher
        public static float AtmosphereMeanEffectiveTemp(float CD) => 255 + (25 * CD); // 0 <= CD <= 1
        public static float AtmosphereNoiseTemperature(float elevationAngle, float frequency =1e9f)
        {
            float CD = 0.5f;
            float Atheta = AtmosphereAttenuation(CD, elevationAngle, frequency);
            float LossFactor = RATools.LinearScale(Atheta);  // typical values = 1.01 to 2.0 (A = 0.04 dB to 3 dB) 
            float meanTemp = AtmosphereMeanEffectiveTemp(CD);
            float result = meanTemp * (1 - (1 / LossFactor));
            //            Debug.LogFormat("AtmosphereNoiseTemperature calc for elevation {0:F2} freq {1:F2}GHz yielded attenuation {2:F2}, LossFactor {3:F2} and mean temp {4:F2} for result {5:F2}", elevationAngle, frequency/1e9, Atheta, LossFactor, meanTemp, result);
            return result;
        }
        public static float AtmosphereAttenuation(float CD, float elevationAngle, float frequency =1e9f)
        {
            float AirMasses = (1 / math.sin(math.radians(math.abs(elevationAngle))));
            return AtmosphereZenithAttenuation(CD, frequency) * AirMasses;
        }
        public static float AtmosphereZenithAttenuation(float CD, float frequency = 1e9f)
        {
            // This would be a gigantic table lookup per ground station.
            if (frequency < 3e9) return 0.035f;          // S/C/L band, didn't really vary by CD
            else if (frequency < 10e9)                  // X-Band, varied 0.4 to 0.6
            {
                return Mathf.Lerp(0.4f, 0.6f, CD);
            }
            else if (frequency < 27e9)                  // Ka-Band, varied .116-.239, .124-.384, .121-.407
            {
                return Mathf.Lerp(0.121f, 0.384f, CD);
            }
            else                                      // K-Band, 0.084-.226, .086-.375, .084-.373
            {
                return Mathf.Lerp(0.084f, 0.373f, CD);
            }
        }
        //https://en.wikipedia.org/wiki/Planetary_equilibrium_temperature   - hopefully body.albedo is the bond albedo?
        public static float GetEquilibriumTemperature(CelestialBody body)
        {
            if (body == Planetarium.fetch.Sun) return 5e6f;  // Failsafe, should not trigger.
            double sunDistSqr = (body.position - Planetarium.fetch.Sun.position).sqrMagnitude;
            double IncidentSolarRadiation = SolarLuminosity / (math.PI * 4 * sunDistSqr);
            double val = IncidentSolarRadiation * (1 - body.albedo) / (4 * PhysicsGlobals.StefanBoltzmanConstant);
            return math.pow((float) val, 0.25f);
        }

        public static float BodyBaseTemperature(CelestialBody body)
            => body.atmosphere ? (float)body.GetTemperature(1) : GetEquilibriumTemperature(body) + (float)body.coreTemperatureOffset;

        //double baseTemp = body.atmosphere ? body.GetTemperature(1) : GetEquilibriumTemperature(body) + body.coreTemperatureOffset;
        //return body.isStar ? StarRadioTemp(baseTemp, rx.Frequency) : baseTemp;      // TODO: Get the BLACKBODY temperature!


        public static float BodyNoiseTemp(double3 antPos,
                                            float gain,
                                            double3 dir,
                                            double3 bodyPos,
                                            float bodyRadius,
                                            float bodyTemp,
                                            float beamwidth = -1)
        {
            if (gain < MaxOmniGain) return 0;
            if (bodyTemp < float.Epsilon) return 0;
            double3 toBody = bodyPos - antPos;
            float angle = (float) MathUtils.Angle2(toBody, dir);
            float distance = (float) math.length(toBody);
            beamwidth = (beamwidth < 0) ? Beamwidth(gain) : beamwidth;
            float bodyRadiusAngularRad = (distance > 10 * bodyRadius)
                    ? math.atan2(bodyRadius, distance)
                    : math.radians(MathUtils.AngularRadius(bodyRadius, distance));
            float bodyRadiusAngularDeg = math.degrees(bodyRadiusAngularRad);
            if (beamwidth < angle - bodyRadiusAngularDeg) return 0;  // Pointed too far away

            float angleRad = math.radians(angle);
            float beamwidthRad = math.radians(beamwidth);
            float gainDelta; // Antenna Pointing adjustment
            float viewedAreaBase;

            // How much of the body is in view of the antenna?
            if (beamwidth < bodyRadiusAngularDeg - angle)    // Antenna viewable area completely enclosed by body
            {
                viewedAreaBase = math.PI * beamwidthRad * beamwidthRad;
                gainDelta = 0;
            }
            else if (beamwidth > bodyRadiusAngularDeg + angle)   // Antenna viewable area completely encloses body
            {
                viewedAreaBase = math.PI * bodyRadiusAngularRad * bodyRadiusAngularRad;
                gainDelta = -PointingLoss(angle, beamwidth);
            }
            else
            {
                viewedAreaBase = MathUtils.CircleCircleIntersectionArea(beamwidthRad, bodyRadiusAngularRad, angleRad);
                float intersectionCenter = MathUtils.CircleCircleIntersectionOffset(beamwidthRad, bodyRadiusAngularRad, angleRad);
                gainDelta = -PointingLoss(math.degrees(intersectionCenter + beamwidthRad) / 2, beamwidth);
            }

            // How much of the antenna viewable area is occupied by the body
            float antennaViewableArea = math.PI * beamwidthRad * beamwidthRad;
            float viewableAreaRatio = viewedAreaBase / antennaViewableArea;

            /*
            double d = body.Radius * 2;
            double Rsqr = toBody.sqrMagnitude;
            double G = RATools.LinearScale(rx.Gain);
            double angleRatio = angle / rx.Beamwidth;

            // https://deepspace.jpl.nasa.gov/dsndocs/810-005/Binder/810-005_Binder_Change51.pdf Module 105: 2.4.3 Planetary Noise estimator
            // This estimator is correct for the DSN viewing planets, but wrong for the sun & moon.
            double result = (t * G * d * d / (16 * Rsqr)) * Math.Pow(Math.E, -2.77 * angleRatio * angleRatio);
            */

            float result = bodyTemp * viewableAreaRatio * RATools.LinearScale(gainDelta);
            //Debug.Log($"Planetary Body Noise Power Estimator: Body {body} Temp: {bodyTemp:F0} AngularDiameter: {bodyRadiusAngularDeg * 2:F1} @ {angle:F1} HPBW: {rx.Beamwidth:F1} ViewableAreaRatio: {viewableAreaRatio:F2} gainDelta: {gainDelta:F4} result: {result}");
            return result;
        }

        public static float BodyNoiseTemp(RealAntenna rx, CelestialBody body, Vector3d rxPointing) =>
            BodyNoiseTemp(new double3(rx.PrecisePosition.x, rx.PrecisePosition.y, rx.PrecisePosition.z),
                        rx.Gain,
                        new double3(rxPointing.x, rxPointing.y, rxPointing.z),
                        new double3(body.position.x, body.position.y, body.position.z),
                        (float) body.Radius,
                        body.isStar ? StarRadioTemp(BodyBaseTemperature(body), rx.Frequency) : BodyBaseTemperature(body));

        public static float MinimumTheoreticalEbN0(float SpectralEfficiency)
        {
            // Given SpectralEfficiency in bits/sec/Hz (= Channel Capacity / Bandwidth)
            // Solve Shannon Hartley for Eb/N0 >= (2^(C/B) - 1) / (C/B)
            return RATools.LogScale(math.pow(2, SpectralEfficiency) - 1) / SpectralEfficiency;
            // 1=> 0dB, 2=> 1.7dB, 3=> 3.7dB, 4=> 5.7dB, 5=> 8dB, 6=> 10.2dB, 7=> 12.6dB, 8=> 15dB
            // 9=> 17.6dB, 10=> 20.1dB, 11=> 22.7dB, 20=> 47.2dB
            // 0.5 => -0.8dB.  Rate 1/2 BPSK Turbo code is EbN0 = +1dB, so about 1.8 above Shannon?
        }
        public static float NoiseFloor(float bandwidth, float noiseTemp) => NoiseSpectralDensity(noiseTemp) + (10 * math.log10(bandwidth));
        public static float NoiseSpectralDensity(float noiseTemp) => boltzmann_dBm + (10 * math.log10(noiseTemp));
        public static float NoiseTemperature(RealAntenna rx, Vector3d origin)
        {
            float amt = AntennaMicrowaveTemp(rx);
            float atmos = AtmosphericTemp(rx, origin);
            float cosmic = CosmicBackgroundTemp(rx, origin);
            // Home Stations are directional, but treated as always pointing towards the peer.
            float allbody = rx.ParentNode.isHome ? AllBodyTemps(rx, origin - rx.Position) : AllBodyTemps(rx, rx.ToTarget);
            float total = amt + atmos + cosmic + allbody;
            //            Debug.LogFormat("NoiseTemp: Antenna {0:F2}  Atmos: {1:F2}  Cosmic: {2:F2}  Bodies: {3:F2}  Total: {4:F2}", amt, atmos, cosmic, allbody, total);
            return total;

            //
            // https://www.itu.int/dms_pubrec/itu-r/rec/p/R-REC-P.372-7-200102-S!!PDF-E.pdf
            //
            // Sky Temperature:  curves that vary based on atmosphere composition of a body.
            //   Earth example: http://www.delmarnorth.com/microwave/requirements/satellite_noise.pdf
            //     <10 deg K @ <15GHz and >30deg elevation in clear weather
            //     Rain can be a very large contributor, tho.
            //     At 0 deg elevation, sky temperature is effectively Earth ambient temperature = 290K.
            //     An omni antenna near Earth has a temperature = Earth ambient temperature = 290K.
            //     A directional antenna on a satellite pointed at Earth s.t. the entire main lobe of the ant
            //        is the Earth also has a noise temperature ~= 290K.
            //     A directional antenna pointed at the sun s.t. its main lobe encompasses ONLY the sun
            //        (so very directional) has an antenna temperature ~= 6000K?
            //  Ref: Galactic noise is high below 1000 MHz. At around 150 MHz, it is approximately 1000 K. At 2500 MHz, it has leveled off to around 10 K.
            //  Atmospheric absorption:  Earth example:  <1dB @ <20GHz
            //  Rainfall has a specific attenuation from absorption, and 
            //    and a correlated contribution to sky temperature from re-emission.
            //  We should either track the effective temperature of the receiver electronics,
            //    or calculate it from its more conventional notation (to me) of noise figure.
            //  So we can get system temperature = antenna effective temperature + sky temperature + receiver effective temperature
            //  Sky we can "define" based on the near celestial body, or set as 3K for deep space.
            //  Receiver effective temperature we can derive from a noise figure.
            //  Antenna temperature is basically the notion of "sky" temperature + rain temperature, or 3-4K in deep space.
            //
            // P(dBW) = 10*log10(Kb*T*bandwidth) = -228.59917 + 10*log10(T*BW)
        }
        public static float AntennaMicrowaveTemp(RealAntenna rx) =>
            ((rx.ParentNode as RACommNode)?.ParentBody is CelestialBody) ? rx.AMWTemp : rx.TechLevelInfo.ReceiverNoiseTemperature;

        public static float AtmosphericTemp(RealAntenna rx, Vector3d origin)
        {
            if (rx.ParentNode is RACommNode rxNode && rxNode.ParentBody != null)
            {
                Vector3d normal = rxNode.GetSurfaceNormalVector();
                return AtmosphericTemp(new double3(rx.Position.x, rx.Position.y, rx.Position.z),
                                        new double3(normal.x, normal.y, normal.z),
                                        new double3(origin.x, origin.y, origin.z),
                                        rx.Frequency);
            }
            return 0;
        }

        public static float AtmosphericTemp(double3 position, double3 surfaceNormal, double3 origin, float frequency)
        {
            float elevation = MathUtils.ElevationAngle(position, surfaceNormal, origin);
            return AtmosphereNoiseTemperature(elevation, frequency);
        }

        public static float CosmicBackgroundTemp(double3 surfaceNormal, double3 toOrigin, float freq, bool isHome)
        {
            float lossFactor = 1;
            if (isHome)
            {
                float CD = 0.5f;
                float angle = (float) MathUtils.Angle2(surfaceNormal, toOrigin);
                float elevation = math.max(0, 90.0f - angle);
                lossFactor = Convert.ToSingle(RATools.LinearScale(AtmosphereAttenuation(CD, elevation, freq)));
            }
            return CMB / lossFactor;
        }

        private static float CosmicBackgroundTemp(RealAntenna rx, Vector3d origin)
        {
            float temp = 3;
            if (rx.ParentNode is RACommNode rxNode)
            {
                Vector3d normal = (rxNode.ParentBody is CelestialBody) ? rxNode.GetSurfaceNormalVector() : Vector3d.zero;
                Vector3d to_origin = origin - rx.Position;
                temp = CosmicBackgroundTemp(new double3(normal.x, normal.y, normal.z),
                                            new double3(to_origin.x, to_origin.y, to_origin.z),
                                            rx.Frequency,
                                            rxNode.isHome);

            }
            return temp;
        }

        public static float AllBodyTemps(RealAntenna rx, Vector3d rxPointing)
        {
            float temp = 0;
            if (rx.Shape != AntennaShape.Omni)
            {
                Profiler.BeginSample("RA Physics AllBodyTemps MainLoop");
                RACommNode node = rx.ParentNode as RACommNode;
                // Note there are ~33 bodies in RSS.
                foreach (CelestialBody body in FlightGlobals.Bodies)
                {
                    if (!node.isHome || !node.ParentBody.Equals(body))
                    {
                        temp += BodyNoiseTemp(rx, body, rxPointing);
                    }
                }
                Profiler.EndSample();
            }
            return temp;
        }

        #region Plasma attenuation model
        // ── Physics background ──────────────────────────────────────────────────────
        // During hypersonic atmospheric entry a shock-heated plasma sheath can form
        // around the vehicle. This sheath can attenuate and/or reflect RF signals.
        //
        // A commonly cited collisional-plasma attenuation-rate expression has the form:
        //
        //   attenuation rate ∝ fp² / (f² - fp²)
        //
        // where fp is the plasma frequency and f is the communication frequency.
        // In the high-frequency limit (f >> fp), this behaves approximately like 1/f²:
        // lower-frequency links are generally affected more strongly than higher-
        // frequency links.
        //
        // This code does NOT evaluate the full plasma-dispersion relation directly.
        // Instead, it applies a configurable empirical attenuation fit that preserves
        // the intended gameplay behavior:
        //   - low-frequency links degrade strongly
        //   - higher-frequency links degrade less
        //   - degradation is gradual rather than binary
        //
        // ── Reference flight data (RAM-C programme, NASA) ───────────────────────────
        // Source 1:  "Causes and Mitigation of Radio Frequency (RF) Blackout During
        //             Reentry", Gillman et al., NASA/TP-2010-216745.
        // Source 2:  NASA TN D-5615 (1970).
        // Source 3:  EUCASS 2022, paper 6128.
        // Source 4:  Zhou et al., AIP Advances 7, 105314 (2017).
        // ── Reference observations ──────────────────────────────────────────────────
        // RAM-C flight results and later blackout reviews consistently indicate that
        // higher frequencies perform substantially better through reentry plasma than
        // lower frequencies. X-band persisted much deeper into the blackout region than
        // VHF, and even higher frequencies performed better still, but higher frequency
        // does not imply zero plasma interaction.
        //
        // ── Applied gameplay model ──────────────────────────────────────────────────
        // One-way sheath attenuation is modeled as:
        //
        //   peakDB(f) = A_ref * (f_ref / f)^n
        //   attenuationDB(f, severity) = peakDB(f) * severity
        //
        // where:
        //   f_ref     = reference frequency
        //   A_ref     = one-way peak attenuation at f_ref and severity = 1
        //   n         = configurable frequency exponent
        //   severity  = plasmaSeverity ∈ [0, 1]
        //
        //   Default (RO/RSS): f_ref = 137 MHz (VHF), A_ref = 50 dB — RAM-C calibrated.
        //   Default (Stock):  f_ref = 1620 MHz (L-band), A_ref = 70 dB — Kerbin-scaled.
        //     Stock mapping: L-band plays the role VHF plays in RO.  The lowest available
        //     stock band receives the same peak blackout severity that VHF receives at
        //     Earth reentry speeds, compensating for Kerbin's lower orbital velocity
        //     (~2,300 m/s vs ~7,700 m/s for Earth LEO).
        //   n = 1.7 empirical fit; pure collision-dominated theory gives n = 2.0.
        //
        // Receiver-side plasma thermal noise is then derived from the modeled
        // attenuation using Kirchhoff-style absorption:
        //
        //   T_noise = PlasmaElectronTemp * (1 - 10^(-attenDB / 10))
        //
        // ── Severity model ──────────────────────────────────────────────────────────
        // For loaded vessels, severity is derived from the ratio:
        //
        //   ratio = vessel.externalTemperature / PhysicsGlobals.CommNetBlackoutThreshold
        //
        // and passed through a smoothstep window.
        //
        // This anchors the gradual RA degradation curve to KSP's stock plasma threshold:
        // at ratio = 1.0, the vessel is at the stock CommNet blackout threshold, while
        // RA can begin degrading earlier and continue degrading beyond that point.
        //
        // For unloaded vessels, no plasma severity is currently modeled.
        //
        // ── Link-budget use ─────────────────────────────────────────────────────────
        // PlasmaAttenuationDB() returns one-way attenuation through a single sheath
        // crossing. In the RA link budget, this can be applied:
        //   - on transmit (Tx-side attenuation)
        //   - on receive (Rx-side attenuation)
        //   - and as additional receiver-side plasma noise
        //
        // All plasma parameters are loaded from RealAntennasCommNetParams.cfg via
        // InitPlasma(), so installs can retune the model without code changes.
        // ────────────────────────────────────────────────────────────────────────────
        
        // Loaded from config in InitPlasma().  Defaults match the stock Kerbin-scaled model.
        public static double PlasmaAtten_RefFreqHz = 1620e6; // Hz  — stock default: L-band
        public static double PlasmaAtten_RefPeakDB = 70.0;   // dB  — stock default
        public static double PlasmaAtten_FreqExponent = 1.7;    // empirical fit; pure theory = 2.0
        public static float PlasmaElectronTemp = 8000f;  // K   — RAM-C measured

        // Smoothstep window expressed as RATIOS of PhysicsGlobals.CommNetBlackoutThreshold.
        // Using ratios ties the transition directly to KSP's own plasma parameter so the
        // model stays correct for any body or mod that changes the threshold.
        //
        //   ratio = vessel.externalTemperature / PhysicsGlobals.CommNetBlackoutThreshold
        //
        // At ratio = 1.0, stock KSP's binary InPlasma becomes true.
        // With window [0.5, 1.5], the stock trigger falls exactly at the midpoint:
        //   ratio < 0.5  → no effect (pre-plasma)
        //   ratio = 1.0  → severity 0.5  (stock InPlasma just fired, half-degraded)
        //   ratio ≥ 1.5  → full blackout
        public static float PlasmaHeat_OnsetRatio = 0.5f; // ratio at plasma onset
        public static float PlasmaHeat_PeakRatio = 1.5f; // ratio at full blackout

        /// <summary>
        /// Loads plasma model parameters from the RealAntennasCommNetParams config node.
        /// Called from RACommNetScenario.Initialize() alongside BandInfo.Init() etc.
        /// Absent values leave the existing default intact, so partial configs work.
        /// </summary>
        public static void InitPlasma(ConfigNode config)
        {
            double d = 0; float f = 0;
            if (config.TryGetValue("PlasmaRefFreqMHz", ref d)) PlasmaAtten_RefFreqHz = d * 1e6;
            if (config.TryGetValue("PlasmaRefPeakDB", ref d)) PlasmaAtten_RefPeakDB = d;
            if (config.TryGetValue("PlasmaFreqExp", ref d)) PlasmaAtten_FreqExponent = d;
            if (config.TryGetValue("PlasmaElectronTempK", ref f)) PlasmaElectronTemp = f;
            if (config.TryGetValue("PlasmaHeatOnsetRatio", ref f)) PlasmaHeat_OnsetRatio = f;
            if (config.TryGetValue("PlasmaHeatPeakRatio", ref f)) PlasmaHeat_PeakRatio = f;
        }

        /// <summary>
        /// Derives plasma severity in [0, 1] from the normalised plasma ratio:
        ///   ratio = vessel.externalTemperature / PhysicsGlobals.CommNetBlackoutThreshold
        ///
        /// Uses a smoothstep curve between PlasmaHeat_OnsetRatio and PlasmaHeat_PeakRatio.
        /// Expressing the window as ratios of CommNetBlackoutThreshold means the curve
        /// tracks KSP's own plasma definition body-agnostically — no per-body config needed.
        /// At ratio = 1.0 (exactly where stock InPlasma becomes true) severity = 0.5,
        /// so degradation starts before the binary trigger and continues past it.
        /// (Gillman 2010 NASA/TP-2010-216745; Apollo TN D-7269.)
        /// </summary>
        /// <param name="ratio">vessel.externalTemperature / PhysicsGlobals.CommNetBlackoutThreshold</param>
        public static float PlasmaHeatSeverity(float ratio)
        {
            if (ratio <= PlasmaHeat_OnsetRatio) return 0f;
            if (ratio >= PlasmaHeat_PeakRatio) return 1f;
            float t = (ratio - PlasmaHeat_OnsetRatio) / (PlasmaHeat_PeakRatio - PlasmaHeat_OnsetRatio);
            return t * t * (3f - 2f * t);   // smoothstep
        }

        /// <summary>
        /// One-way signal attenuation in dB from the reentry plasma sheath.
        /// peakDB(f) = RefPeakDB * (RefFreqHz / f)^FreqExponent, scaled by severity.
        /// </summary>
        public static float PlasmaAttenuationDB(float freqHz, float plasmaSeverity)
        {
            if (plasmaSeverity <= 0f || freqHz <= 0f) return 0f;
            double freqRatio = PlasmaAtten_RefFreqHz / freqHz;
            double peakDB = PlasmaAtten_RefPeakDB * Math.Pow(freqRatio, PlasmaAtten_FreqExponent);
            return (float)Math.Max(0.0, peakDB * plasmaSeverity);
        }

        /// <summary>
        /// Noise temperature (K) injected into a receiving antenna by the plasma sheath,
        /// via Kirchhoff's law:  T_noise = PlasmaElectronTemp * (1 - 10^(-attenDB/10)).
        /// Applies when the plasma-sheathed vessel is the receiver (uplink direction).
        /// </summary>
        public static float PlasmaNoiseTemperature(float freqHz, float plasmaSeverity)
        {
            if (plasmaSeverity <= 0f || freqHz <= 0f) return 0f;
            float attenDB = PlasmaAttenuationDB(freqHz, plasmaSeverity);
            float absorption = 1f - (float)Math.Pow(10.0, -attenDB / 10.0);
            return PlasmaElectronTemp * absorption;
        }

        /// <summary>
        /// Fallback severity for unloaded vessels: maps the stock binary CommNet plasma
        /// modifier to [0, 1].  Loaded vessels use PlasmaHeatSeverity instead.
        ///   stockModifier = 1.0 → severity 0.0 (clear)
        ///   stockModifier = 0.0 → severity 1.0 (full blackout)
        ///   intermediate  → severity = 1.0 - stockModifier (future-proof)
        /// </summary>
        public static float PlasmaModifierToSeverity(double stockModifier) =>
            (float)Math.Max(0.0, Math.Min(1.0, 1.0 - stockModifier));

        #endregion
    }
}
