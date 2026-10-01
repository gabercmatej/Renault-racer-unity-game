using UnityEngine;
using RenaultRacer.Vehicle;
using RenaultRacer.Effects;

namespace RenaultRacer.UI
{
    /// <summary>
    /// Development HUD (plan item 3.14). At this stage a readout of the underlying
    /// simulation is far more useful than a pretty one.
    ///
    /// Also hosts the runtime handling-profile switch, so profiles can be compared
    /// mid-corner instead of by editing asset references between runs.
    ///
    /// IMGUI on purpose: zero scene setup, zero prefabs. Deleted before the Phase 7
    /// UI work and not part of the shipping build path.
    /// </summary>
    public sealed class DebugHud : MonoBehaviour
    {
        [SerializeField] private CarController car;
        [SerializeField] private RenaultRacer.Core.GameDirector director;
        [SerializeField] private FeedbackFX feedbackFX;
        [SerializeField] private SkidMarks skidMarks;

        [Tooltip("Optional. The plan 19 budget numbers, measured in a BUILD - the " +
                 "Editor cannot answer any of them (plan 9.2).")]
        [SerializeField] private RenaultRacer.Core.PerfProbe perf;

        [Tooltip("Optional. Triangle count for the plan 19 geometry budget. The " +
                 "road mesh is nearly all of it - everything else is sprites.")]
        [SerializeField] private RenaultRacer.Track.RoadRenderer roadRenderer;

        [Tooltip("Optional. Which quality tier is live and what decided it (plan 20).")]
        [SerializeField] private RenaultRacer.Core.QualityDirector quality;

        [Tooltip("Optional. Shows the raw control state the simulation is being fed, " +
                 "which is what makes the touch handbrake verifiable on a phone: the " +
                 "drift gesture is otherwise invisible until the car is already sliding.")]
        [SerializeField] private RenaultRacer.InputSystem.InputRouter input;

        [SerializeField] private bool show = true;

        private GUIStyle _style;
        private GUIStyle _titleStyle;
        private readonly System.Text.StringBuilder _sb = new System.Text.StringBuilder(640);

        // Rolling frame-time window, for a stable FPS readout.
        private readonly float[] _frames = new float[60];
        private int _frameIndex;

        private void Update()
        {
            _frames[_frameIndex] = Time.unscaledDeltaTime;
            _frameIndex = (_frameIndex + 1) % _frames.Length;

            if (UnityEngine.Input.GetKeyDown(KeyCode.F1)) show = !show;

            // There is no F1 on a phone, and this panel covers most of the left
            // steering gutter - which is exactly what a device playtest needs to be
            // able to get out of the way. A THREE-finger tap toggles it: two thumbs
            // is the most driving ever uses, so a third finger cannot be an accident.
            TickTouchToggle();

            if (car == null) return;

            // R restarts the whole race via GameDirector (countdown, timer, checkpoints).
            // Only fall back to a bare car reset if there is no director.
            if (UnityEngine.Input.GetKeyDown(KeyCode.R) && director == null) car.ResetCar(0f);

            // 0/P = Production (index 0, the shipping profile); 1/2/3 = the dev
            // reference profiles; TAB cycles (plan 8.13.2). Development only - this
            // whole selector is stripped for production per checklist 9.6b.
            if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha0)) car.SetProfile(0);
            if (UnityEngine.Input.GetKeyDown(KeyCode.P))      car.SetProfile(0);
            if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha1)) car.SetProfile(1);
            if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha2)) car.SetProfile(2);
            if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha3)) car.SetProfile(3);
            if (UnityEngine.Input.GetKeyDown(KeyCode.Tab))    car.NextProfile();

            // Force a quality tier, so a tier gets LOOKED at on a real device rather
            // than reasoned about. QualityDirector's own decision is a one-way ratchet
            // and deliberately cannot be talked back up (plan 20); this is the dev
            // override, and it does not ship - ProductionStrip removes this whole
            // component from a production build.
            if (quality != null)
            {
                if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha7))
                    quality.ForceTier(RenaultRacer.Core.QualityDirector.Tier.Ideal);
                if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha8))
                    quality.ForceTier(RenaultRacer.Core.QualityDirector.Tier.Normal);
                if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha9))
                    quality.ForceTier(RenaultRacer.Core.QualityDirector.Tier.Weak);
            }
        }

        /// <summary>
        /// Three fingers down together, then released, toggles the panel. Edge-triggered
        /// on the count so holding three fingers does not strobe it.
        /// </summary>
        private void TickTouchToggle()
        {
            int touches = UnityEngine.Input.touchCount;
            if (touches >= 3) _threeFingerArmed = true;
            else if (touches == 0 && _threeFingerArmed)
            {
                _threeFingerArmed = false;
                show = !show;
                Debug.Log("[DebugHud] three-finger tap -> " + (show ? "shown" : "hidden"));
            }
        }
        private bool _threeFingerArmed;

        private void OnGUI()
        {
            if (!show || car == null || car.Sim == null) return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label);
                _style.fontSize = 15;
                _style.normal.textColor = Color.white;
                _style.padding = new RectOffset(12, 10, 6, 8);

                _titleStyle = new GUIStyle(GUI.skin.label);
                _titleStyle.fontSize = 19;
                _titleStyle.fontStyle = FontStyle.Bold;
                _titleStyle.normal.textColor = new Color(1f, 0.82f, 0.15f);
                _titleStyle.padding = new RectOffset(12, 10, 8, 0);
            }

            CarSimulation s = car.Sim;
            HandlingProfile p = car.CurrentProfile;

            float avg = 0f;
            for (int i = 0; i < _frames.Length; i++) avg += _frames[i];
            avg /= _frames.Length;

            // The car's own scrub, straight from the model. It used to be re-derived here
            // from lateral velocity over a clamp that no longer exists - and which, under
            // the world-heading model, would have read a car crossing the road perfectly
            // cleanly as a car that was sliding.
            float lateralSlip = s.SlipRatio;

            const float W = 380f, H = 446f;

            GUI.color = new Color(0f, 0f, 0f, 0.62f);
            GUI.DrawTexture(new Rect(8, 8, W, H), Texture2D.whiteTexture);
            GUI.color = Color.white;

            // Index 0 is always the shipping profile - call it out so a tuning
            // session can never mistake a dev reference for production.
            bool isProduction = car.ProfileIndex == 0;
            _titleStyle.normal.textColor = isProduction
                ? new Color(0.45f, 1f, 0.45f)      // green = production
                : new Color(1f, 0.82f, 0.15f);     // amber = dev reference
            GUI.Label(new Rect(8, 8, W, 28),
                      "PROFILE  " + car.CurrentProfileName.ToUpperInvariant()
                      + (isProduction ? "  [SHIPPING]" : "  [dev ref]"),
                      _titleStyle);

            _sb.Length = 0;
            // FIRST LINE, always. "Is the phone running the same code as the Editor" is
            // the question this whole panel exists to make answerable, and for three
            // phases it was the one thing the panel could not say (plan 8.21).
            _sb.Append(RenaultRacer.Core.BuildInfo.Summary).AppendLine();
            _sb.Append("speed       ").Append(s.Speed.ToString("F0"))
               .Append("   ").Append((s.SpeedPct * 100f).ToString("F0")).Append("%\n");
            _sb.Append("steer in    ").Append(Bar(s.Steer)).Append(' ')
               .Append(s.Steer.ToString("F2")).Append('\n');
            if (input != null)
            {
                // The whole touch -> simulation chain on one line. On a phone this is
                // the only way to tell a drift request that never armed apart from one
                // that armed while the car simply did not slide.
                _sb.Append("input       ").Append(input.SteeringFromTouch ? "TOUCH" : "key  ")
                   .Append(input.BrakeHeld ? "  BRAKE" : "       ")
                   .Append(input.HandbrakeHeld ? "  HANDBRAKE-ARMED" : "")
                   .Append(input.Suspended ? "  [suspended]" : "").Append('\n');
            }
            _sb.Append("lateralVel  ").Append(s.LateralVel.ToString("F3"))
               .Append("   slip ").Append((lateralSlip * 100f).ToString("F0")).Append("%\n");
            _sb.Append("drift       ").Append(Meter(s.DriftAmount)).Append(' ')
               .Append(s.DriftAmount.ToString("F2"))
               .Append(s.CounterSteering ? "  COUNTER" : "").Append('\n');
            // THE LINE THIS WHOLE REBUILD IS VERIFIED BY, and the one to watch with the
            // controls untouched in a corner.
            //
            // "road" is how fast the ROAD is rotating under the car; "car" is how fast
            // the CAR is rotating. The percentage is the second over the first - how much
            // of the turn the player is actually supplying. Hands off in a corner it must
            // read 0%, and the yaw beside it must climb away from zero as the road leaves
            // the car behind. The old model sat at 100% with nothing touched.
            float roadRate = s.RoadYawRate;
            bool turning = Mathf.Abs(roadRate) > 0.02f;
            _sb.Append("rotation    road ").Append((roadRate * Mathf.Rad2Deg).ToString("F0"))
               .Append("  car ").Append((s.YawRate * Mathf.Rad2Deg).ToString("F0"))
               .Append(" deg/s   supplied ")
               .Append(turning ? (s.YawRate / roadRate * 100f).ToString("F0") + "%" : "--")
               .Append('\n');
            _sb.Append("heading     world ").Append((s.WorldHeading * Mathf.Rad2Deg).ToString("F0"))
               .Append("  road ").Append((s.RoadHeading * Mathf.Rad2Deg).ToString("F0"))
               .Append("  yaw ").Append((s.Yaw * Mathf.Rad2Deg).ToString("F1")).Append(" deg\n");
            _sb.Append("slip        ").Append(Meter(s.SlipRatio)).Append(' ')
               .Append(s.SlipVel.ToString("F0")).Append(" u/s  ")
               .Append((s.SlipAngle * Mathf.Rad2Deg).ToString("F1")).Append(" deg")
               .Append(s.StraightGate > 0.001f ? "   [straight gate]" : "")
               .Append('\n');
            _sb.Append("grip        ").Append(s.CurrentGrip.ToString("F2"))
               .Append("   ").Append(s.CurrentSurface.ToString())
               .Append(s.OffRoad ? "  OFF-ROAD" : "").Append('\n');
            _sb.Append("x           ").Append(s.X.ToString("F3")).Append('\n');
            if (director != null && director.Session != null)
            {
                var ses = director.Session;
                _sb.Append("state       ").Append(director.State.ToString());
                if (director.State == RenaultRacer.Core.GameState.Countdown)
                    _sb.Append("  lamps ").Append(director.LampsLit).Append("/3");
                _sb.Append('\n');
                _sb.Append("lap         ")
                   .Append(RenaultRacer.Race.RaceSession.FormatTime(ses.ElapsedSeconds))
                   .Append("   cp ").Append(ses.CheckpointsPassed).Append('/')
                   .Append(ses.CheckpointCount)
                   .Append("   ").Append((ses.LapProgress * 100f).ToString("F0")).Append("%\n");
                if (director.Rejection != RenaultRacer.Race.LapRejection.None)
                    _sb.Append("REJECTED    ").Append(director.Rejection.ToString()).Append('\n');
            }
            _sb.Append("z           ").Append(s.Z.ToString("F0")).Append('\n');
            _sb.Append("fps         ").Append(Mathf.RoundToInt(1f / Mathf.Max(0.0001f, avg)));
            if (feedbackFX != null) _sb.Append("   particles ").Append(feedbackFX.AliveParticles);
            // Both numbers, because they answer different questions and for three phases
            // only the first one was being asked. Marks were being STORED the whole time;
            // none of them were ever DRAWN, because they were recorded at the camera's
            // own z (plan 8.19.8). "skids 40/0" is what that bug looks like.
            if (skidMarks != null)
                _sb.Append("   skids ").Append(skidMarks.MarkCount)
                   .Append('/').Append(skidMarks.LastQuadCount);
            _sb.Append('\n');
            // ...and a third question the first two still cannot answer: the quads exist,
            // but are they ON SCREEN and are they OPAQUE? A trail built correctly and
            // drawn off the bottom of the frame, or drawn at alpha 0.02, looks exactly
            // like "72/72" and exactly like no skid marks at all (plan 10.6).
            if (skidMarks != null)
            {
                _sb.Append("skid        seen ").Append(skidMarks.LastOnScreenQuads)
                   .Append("  a ").Append(skidMarks.LastPeakAlpha.ToString("F2"))
                   .Append("  tex ").Append(skidMarks.Textured ? "y" : "n")
                   .Append("  dz ").Append(skidMarks.LastNearDepth.ToString("F0"))
                   .Append("..").Append(skidMarks.LastFarDepth.ToString("F0"))
                   .Append("  y ").Append(skidMarks.LastMinY.ToString("F1"))
                   .Append("..").Append(skidMarks.LastMaxY.ToString("F1"))
                   .Append("  cut ").Append(skidMarks.LastCutByStripBreak)
                   .Append('/').Append(skidMarks.LastCutByNear)
                   .Append('/').Append(skidMarks.LastCutByFar)
                   .Append('/').Append(skidMarks.LastCutByProjection)
                   .Append('\n');
            }
            if (quality != null)
            {
                _sb.Append("tier        ").Append(quality.CurrentTier.ToString())
                   .Append("   from ").Append(quality.BootTier.ToString());
                if (quality.Decided)
                    _sb.Append("   measured ").Append(quality.MeasuredMilliseconds.ToString("F1"))
                       .Append(" ms");
                else _sb.Append("   measuring...");
                _sb.Append('\n');
            }
            if (roadRenderer != null)
            {
                // Triangles, counted rather than estimated. The road mesh is drawDistance
                // bands of 7 quads and dwarfs everything else in the scene, so it is the
                // only number worth watching against the plan 19 budget - and it is also
                // exactly what QualityDirector turns down.
                int tris = roadRenderer.LastTriangleCount;
                if (skidMarks != null) tris += skidMarks.LastQuadCount * 2;
                _sb.Append("tris        ").Append(tris)
                   .Append("   road ").Append(roadRenderer.LastQuadCount).Append(" quads")
                   .Append(" @ draw ").Append(roadRenderer.drawDistance).Append('\n');
            }
            if (perf != null) { perf.AppendTo(_sb); _sb.Append('\n'); }

            _sb.Append("\nsteer arrows/AD   brake down/S/space\n");
            _sb.Append("handbrake shift   restart R   hide F1 / 3-finger tap\n");
            _sb.Append("profile 0/P=production  1/2/3=dev  TAB cycles\n");
            _sb.Append("tier 7=ideal 8=normal 9=weak");

            GUI.Label(new Rect(8, 34, W, H - 34), _sb.ToString(), _style);
        }

        /// <summary>Signed -1..1 bar, so steering input is readable at a glance.</summary>
        private static string Bar(float v)
        {
            int slots = 9;
            int mid = slots / 2;
            int pos = Mathf.Clamp(Mathf.RoundToInt(mid + v * mid), 0, slots - 1);
            System.Span<char> buf = stackalloc char[slots + 2];
            buf[0] = '[';
            for (int i = 0; i < slots; i++) buf[i + 1] = i == pos ? '#' : (i == mid ? '|' : '-');
            buf[slots + 1] = ']';
            return new string(buf);
        }

        /// <summary>Unsigned 0..1 meter.</summary>
        private static string Meter(float v)
        {
            int slots = 9;
            int filled = Mathf.Clamp(Mathf.RoundToInt(v * slots), 0, slots);
            System.Span<char> buf = stackalloc char[slots + 2];
            buf[0] = '[';
            for (int i = 0; i < slots; i++) buf[i + 1] = i < filled ? '#' : '-';
            buf[slots + 1] = ']';
            return new string(buf);
        }
    }
}
