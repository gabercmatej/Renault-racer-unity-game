using UnityEngine;
using RenaultRacer.Track;
using RenaultRacer.InputSystem;

namespace RenaultRacer.Vehicle
{
    /// <summary>
    /// THE CORE MODEL.
    ///
    /// The car owns its heading IN THE WORLD. The road does not.
    ///
    /// That one sentence is the whole of this rewrite, and it is worth being precise
    /// about what it replaced. The previous model measured yaw against the ROAD and
    /// sprang it to zero, and advanced position with `Z += Speed * dt` along the curved
    /// centre line. Both halves of that follow the road by construction: a car with the
    /// controls untouched came out of a 90 degree corner rotated by 90 degrees and
    /// travelling 93% of the way round it, measured. The player was handed the corner and
    /// charged a small drift outward for it. AutoTurnProbe scores exactly this and the
    /// old model scored 100% body / 93% trajectory on every corner of the circuit.
    ///
    /// THE STATE, and which of it is owned rather than derived:
    ///
    ///   Z            distance along the centre line              owned
    ///   X            lateral offset, 0 = centre, +/-1 = edge     owned   (+X is RIGHT)
    ///   Speed        forward speed along the car's OWN nose      owned
    ///   SlipVel      sideways speed in the car's OWN frame       owned
    ///   WorldHeading where the car points, in the world          owned
    ///   WorldYawRate how fast it is rotating, in the world       owned
    ///   Steer        smoothed steering input                     owned
    ///   DriftAmount  0 = gripping, 1 = sliding                   owned
    ///   Yaw          heading RELATIVE TO THE ROAD                DERIVED
    ///
    /// Yaw is the last line and it is the important one:
    ///
    ///     Yaw = WorldHeading - RoadHeading(Z)
    ///
    /// It is computed, never integrated, which makes the relation the diagnosis asked for
    ///
    ///     dYaw/dt = WorldYawRate - curvature * (rate of progress along the road)
    ///
    /// exact and free of drift, because both of its terms come from state that is tracked
    /// honestly. And it makes the headline property STRUCTURAL rather than tuned: with no
    /// input, WorldYawRate is zero, so WorldHeading does not change, so the car cannot
    /// inherit one degree of the road's rotation no matter how the rest is tuned. The road
    /// curves away underneath it and the car carries straight on.
    ///
    /// POSITION IS INTEGRATED IN THE SAME FRAME, which is the half that a visual-only fix
    /// would have missed. Velocity is resolved out of the body frame and into the road's:
    ///
    ///     vAlong = Speed * cos(Yaw) - SlipVel * sin(Yaw)
    ///     vLat   = Speed * sin(Yaw) + SlipVel * cos(Yaw)
    ///     Z += vAlong / (1 - curvature * lateralOffset) * dt
    ///     X += vLat / RoadHalfWidth * dt
    ///
    /// The denominator is the standard Frenet correction and it earns its place: it is
    /// why the inside of a corner is a shorter lap and the outside a longer one.
    ///
    /// GRIP ALIGNS THE CAR'S VELOCITY TO THE CAR, NOT TO THE ROAD. The old grip term
    /// damped road-relative lateral velocity, which is a spring pulling the car's motion
    /// onto the road's tangent - road-following wearing a tyre's clothes. Here the tyres
    /// kill SlipVel, which is sideways motion measured against the car's own nose, and
    /// nothing in the model knows or cares which way the road is pointing except the one
    /// subtraction that defines Yaw.
    ///
    /// WHAT IS DELIBERATELY NOT HERE:
    ///   * a centrifugal term. In a road-following model it was the only thing making a
    ///     corner cost anything; here a corner costs rotation, and rotation costs grip, so
    ///     the difficulty is already in the geometry. Adding an outward push on top would
    ///     charge the player twice, and would push a car that is not turning at all.
    ///   * a heading spring onto the road tangent. That WAS the auto-turn.
    ///   * any term that reads the car's POSITION on the road. There is still no centre
    ///     attraction and no racing line; X is written, and read only by the surface and
    ///     barrier code.
    ///
    /// The one road-referenced term that survives is the straight-only re-alignment, it is
    /// gated to curvature near zero, and AutoTurnProbe [A4] is the guard that it stays
    /// there.
    ///
    /// Pure math. No Unity objects, no rendering, no UI, no allocation per step.
    /// </summary>
    public sealed class CarSimulation
    {
        private readonly TrackModel _track;
        private HandlingProfile _profile;

        // ---- owned state ----
        public float Z;             // position along the track
        public float X;             // lateral offset, normalised, +1 = the RIGHT road edge
        public float Speed;         // forward velocity along the car's own nose, units/sec
        public float SlipVel;       // sideways velocity in the CAR's frame, units/sec, + = right
        public float WorldHeading;  // where the car points, radians, unwrapped, + = right
        public float WorldYawRate;  // how fast it is rotating in the world, rad/sec
        public float Steer;         // smoothed steering input, -1..1
        public float DriftAmount;   // 0 = full grip, 1 = full slide

        /// <summary>
        /// Heading relative to the road tangent, radians. DERIVED, every step, as
        /// <c>WorldHeading - RoadHeading(Z)</c> - never integrated, so it cannot drift away
        /// from the two quantities it is the difference of.
        ///
        /// Presentation, telemetry and the camera read it, and a corner now legitimately
        /// drives it a long way from zero: a car that is not being steered through a
        /// 90 degree corner ends up 90 degrees across the road, and Yaw is how that is
        /// visible. The car ART covers about +/-25 degrees, so the SPRITE clamps; the
        /// physics does not (see CarFrameSelector).
        /// </summary>
        public float Yaw { get; private set; }

        /// <summary>The road's own heading under the car, radians, unwrapped across laps.</summary>
        public float RoadHeading { get; private set; }

        /// <summary>
        /// The road's rotation rate under the car right now, rad/sec: curvature times the
        /// rate of progress along it. This is the number the player has to MATCH with
        /// WorldYawRate to hold a line, and the gap between the two is the whole game.
        /// </summary>
        public float RoadYawRate { get; private set; }

        /// <summary>
        /// Which way the car is actually GOING, in the world, radians. Differs from
        /// <see cref="WorldHeading"/> by the slip angle, and it is the honest thing to
        /// measure a trajectory against.
        /// </summary>
        public float TravelHeading
        {
            get { return WorldHeading + Mathf.Atan2(SlipVel, Mathf.Max(Speed, 1f)); }
        }

        /// <summary>Rate of progress along the centre line, units/sec. Not the same as Speed.</summary>
        public float AlongVel { get; private set; }

        /// <summary>
        /// Lateral velocity in ROAD space, normalised units/sec - how fast X is changing.
        /// Derived from the body-frame velocity rather than integrated, and kept because
        /// the camera lean, the debug HUD and the skid marks were all written against it.
        /// </summary>
        public float LateralVel { get; private set; }

        /// <summary>
        /// The world rotation rate the player's input is asking for, rad/sec. Exactly zero
        /// with the controls untouched, in a corner as much as on a straight - which is the
        /// number that settles the "is the game steering for me" question outright.
        /// </summary>
        public float YawRateCommand { get; private set; }

        /// <summary>
        /// The world rotation rate FULL lock would ask for right now, rad/sec: everything
        /// speed, surface and drift do to the car's steering authority, in one number.
        ///
        /// Published because the scripted drivers have to invert it - a rate-steered car is
        /// driven by asking for a rotation rate and dividing by what is available - and
        /// they must not be allowed to keep their own copy of the arithmetic. A copy is how
        /// a probe comes to be flying a car that no longer exists.
        /// </summary>
        public float YawRateCeiling { get; private set; }

        /// <summary>
        /// 0..1: how much of the steering command the yaw envelope let through in the last
        /// step. 1 everywhere in ordinary driving; below 1 only when the wheel is asking to
        /// turn the car more than steerYawSoftLimit away from the road.
        /// </summary>
        public float SteerYawEnvelope { get; private set; }

        /// <summary>
        /// Kept under its old name for the view, the HUD and the FX, which all read "the
        /// car's own rotation rate". It now means that literally, in world terms.
        /// </summary>
        public float YawRate { get { return WorldYawRate; } }

        // ---- read-only telemetry for the view, HUD and FX ----
        public float SpeedPct { get { return _profile != null ? Mathf.Clamp01(Speed / _profile.maxSpeed) : 0f; } }

        /// <summary>Lateral grip available right now, as a fraction of the dry maximum.</summary>
        public float CurrentGrip { get; private set; }

        /// <summary>Grip multiplier of the surface under the car right now, 1 on dry asphalt.</summary>
        public float SurfaceGrip { get; private set; }
        public Surface CurrentSurface { get; private set; }
        public bool OffRoad { get; private set; }
        public bool CounterSteering { get; private set; }

        /// <summary>
        /// Slip angle, radians: where the car POINTS minus where it is GOING. Positive when
        /// the nose is to the right of the direction of travel.
        /// </summary>
        public float SlipAngle { get; private set; }

        /// <summary>
        /// How hard the tyres are scrubbing, 0..1 - what smoke, squeal and skid marks read.
        ///
        /// It is now simply how fast the car is sliding SIDEWAYS in its own frame, over a
        /// reference speed. That is the honest signal and it needs no special case: a car
        /// cornering inside its grip has no body-frame slip at all and reads zero, a
        /// deliberate drift saturates, and grass sits in between because grass genuinely
        /// cannot hold the rotation the player is asking for.
        /// </summary>
        public float SlipRatio { get; private set; }

        /// <summary>
        /// Kept so tooling built against the previous models still compiles. There is no
        /// stability assist and no self-aligning moment; the straight-only re-alignment is
        /// reported by <see cref="StraightAlignRate"/> instead, where it can be seen.
        /// </summary>
        public float StabilityAssist { get { return 0f; } }
        public float SelfAlignRate { get { return 0f; } }

        /// <summary>
        /// How much world rotation the straight-only re-alignment applied in the last step,
        /// rad/sec. Exposed because it is the one road-referenced term left in the model and
        /// it should be possible to watch it be zero in every corner.
        /// </summary>
        public float StraightAlignRate { get; private set; }

        /// <summary>How open the straight-only re-alignment gate is, 0 in any real corner.</summary>
        public float StraightGate { get; private set; }

        /// <summary>
        /// World rotation the heading-carry limiter applied in the last step, rad/sec. Zero
        /// unless the car is pointed past the limit and the player is not steering further
        /// away - and zero for a car left alone on the outside of a corner.
        /// </summary>
        public float CarryLimitRate { get; private set; }
        private bool _wasOffRoad;
        private float _reWindow;
        private float _reSide;
        private float _carry;

        /// <summary>
        /// BRAKE SKID, 0..1 - presentation only, nothing in the physics reads it. Braking
        /// while turning produces no body-frame slip in this model (the brake LOWERS the
        /// demand), so a hard trail-brake into a corner laid no rubber at all. This is the
        /// separate, visual signal for it: brake held, a real share of lock, tarmac, and
        /// above brakeSkidMinSpeed, debounced so a dab of either lays nothing. It never
        /// touches DriftAmount, grip or speed - the true drift stays the handbrake's.
        /// </summary>
        public float BrakeSkid { get; private set; }
        private float _brakeSkidHeld;

        // ---- LIGHTNING BOOST. A temporary raise of the speed CEILING and a kick of
        // acceleration toward it - never a change to the profile, never to steering
        // authority (which reads the clamped speed fraction). More speed into the same
        // corner is less rotation per metre, which is the risk the pickup is meant to bring.
        private bool _boostActive;
        private float _boostTop, _boostAccel, _boostHold, _boostFade, _boostT;

        /// <summary>0..1: 1 for the hold, then eased back to 0 over the fade.</summary>
        public float BoostLevel { get; private set; }

        /// <summary>Seconds until the boost is fully gone, 0 when there is none.</summary>
        public float BoostTimeLeft
        {
            get { return _boostActive ? Mathf.Max(0f, _boostHold + _boostFade - _boostT) : 0f; }
        }

        /// <summary>Speed over the profile's top speed, NOT clamped - reads above 1 under boost.</summary>
        public float SpeedRatio { get { return _profile != null ? Speed / Mathf.Max(1f, _profile.maxSpeed) : 0f; } }

        /// <summary>
        /// Start (or restart) a boost: <paramref name="addedTopSpeed"/> and
        /// <paramref name="addedAccel"/> are fractions of maxSpeed (per second for the
        /// acceleration), held for <paramref name="hold"/> seconds and eased out over
        /// <paramref name="fade"/>. A second pickup inside the window restarts it, so
        /// boosts never stack.
        /// </summary>
        public void TriggerBoost(float addedTopSpeed, float addedAccel, float hold, float fade)
        {
            _boostActive = true;
            _boostTop = Mathf.Max(0f, addedTopSpeed);
            _boostAccel = Mathf.Max(0f, addedAccel);
            _boostHold = Mathf.Max(0f, hold);
            _boostFade = Mathf.Max(0.01f, fade);
            _boostT = 0f;
        }

        /// <summary>Throttle is automatic during a race (plan section 2.2 item 4).</summary>
        public bool ThrottleEnabled = true;

        /// <summary>Set false during the countdown to freeze the car.</summary>
        public bool ControlsEnabled = true;

        /// <summary>
        /// Extra coast deceleration, fraction of maxSpeed per second, added to coastDrag.
        /// Zero while racing - so driving is bit-identical - and set by GameDirector after
        /// the line so the car pulls up instead of rolling on. Reset clears it.
        /// </summary>
        public float FinishDecel = 0f;

        private float _pendingImpact;
        private float _pendingImpactSide;
        private float _surfaceBlend;

        /// <summary>
        /// Which side of the car struck the barrier in the impact ConsumeImpact is about to
        /// return: -1 the left flank, +1 the right, 0 if there is no impact. Read it AFTER
        /// ConsumeImpact - the pair is one reading, and the side outlives the strength so
        /// the effect can be placed after the strength has been taken.
        /// </summary>
        public float ImpactSide { get { return _pendingImpactSide; } }

        /// <summary>
        /// Strength of an impact since the last call, 0..1, then cleared.
        /// Polled rather than an event so the hot path allocates nothing.
        /// </summary>
        public float ConsumeImpact()
        {
            float v = _pendingImpact;
            _pendingImpact = 0f;
            return v;
        }

        public CarSimulation(TrackModel track, HandlingProfile profile)
        {
            _track = track;
            _profile = profile;
            CurrentSurface = Surface.Road;
        }

        public HandlingProfile Profile
        {
            get { return _profile; }
            set { _profile = value; }
        }

        public void Reset(float startZ)
        {
            Z = startZ;
            X = 0f;
            Speed = 0f;
            FinishDecel = 0f;
            SlipVel = 0f;
            Steer = 0f;
            DriftAmount = 0f;
            WorldYawRate = 0f;
            RoadHeading = _track != null ? _track.WorldFrame.HeadingAt(startZ) : 0f;
            // Pointed exactly down the road is the only sensible place to start, and it is
            // expressed the way everything else is: a world heading that happens to equal
            // the road's.
            WorldHeading = RoadHeading;
            Yaw = 0f;
            RoadYawRate = 0f;
            AlongVel = 0f;
            LateralVel = 0f;
            _pendingImpact = 0f;
            _pendingImpactSide = 0f;
            CounterSteering = false;
            OffRoad = false;
            SlipAngle = 0f;
            SlipRatio = 0f;
            YawRateCommand = 0f;
            YawRateCeiling = 0f;
            SteerYawEnvelope = 1f;
            StraightAlignRate = 0f;
            StraightGate = 0f;
            CarryLimitRate = 0f;
            _wasOffRoad = false;
            _reWindow = 0f;
            _reSide = 0f;
            _carry = 0f;
            SurfaceGrip = 1f;
            CurrentGrip = 1f;
            _surfaceBlend = 1f;
            BrakeSkid = 0f;
            _brakeSkidHeld = 0f;
            _boostActive = false;
            _boostT = 0f;
            BoostLevel = 0f;
        }

        // ------------------------------------------------------------------
        // PLACING THE CAR. Tooling and tests need to put the car in a particular state
        // and then watch what the model does with it, and under this model that can no
        // longer be done by assigning to the fields they used to: Yaw and LateralVel are
        // DERIVED, so writing to them would have written to nothing. These three say what
        // the old assignments meant, in terms of the state that is actually owned.

        /// <summary>
        /// Point the car <paramref name="yawVsRoad"/> radians off the road's tangent,
        /// where it currently stands. Positive is nose-right.
        /// </summary>
        public void PointAt(float yawVsRoad)
        {
            RoadHeading = _track != null ? _track.WorldFrame.HeadingAt(Z) : 0f;
            WorldHeading = RoadHeading + yawVsRoad;
            Yaw = yawVsRoad;
        }

        /// <summary>
        /// Stop everything except forward motion: pointed down the road, no rotation, no
        /// slide. X and Speed are left alone - this settles the car, it does not move it.
        /// </summary>
        public void Settle()
        {
            SlipVel = 0f;
            WorldYawRate = 0f;
            LateralVel = 0f;
            PointAt(0f);
        }

        /// <summary>
        /// Make the car cross the road at <paramref name="normalisedPerSecond"/> half-widths
        /// per second, without moving the nose.
        ///
        /// Road-space lateral velocity is <c>Speed*sin(Yaw) + SlipVel*cos(Yaw)</c>, so the
        /// only part of it that can be set without rotating the body is the slide, and the
        /// slide is solved for rather than assigned. A car already pointed a long way off
        /// the road therefore cannot be given an arbitrary sideways speed - which is a true
        /// statement about the car, not a limitation of this helper.
        /// </summary>
        public void SetLateralVelocity(float normalisedPerSecond)
        {
            float c = Mathf.Cos(Yaw);
            if (Mathf.Abs(c) < 0.2f) return;
            SlipVel = (normalisedPerSecond * RoadGeometry.RoadHalfWidth - Speed * Mathf.Sin(Yaw)) / c;
            if (_profile != null)
            {
                float bound = Mathf.Max(Speed, 1f) * Mathf.Tan(_profile.maxSlipAngle);
                SlipVel = Mathf.Clamp(SlipVel, -bound, bound);
            }
            LateralVel = (Speed * Mathf.Sin(Yaw) + SlipVel * c) / RoadGeometry.RoadHalfWidth;
        }

        /// <summary>
        /// Advance one FIXED step. Never call with a variable frame delta - see
        /// FixedStepClock. Frame-rate independence depends on this.
        /// </summary>
        public void Step(float dt, IDriveInput input)
        {
            if (_profile == null || _track == null) return;

            HandlingProfile p = _profile;
            TrackWorldFrame frame = _track.WorldFrame;

            float rawSteer = ControlsEnabled ? Mathf.Clamp(input.SteerAxis, -1f, 1f) : 0f;
            bool brake     = ControlsEnabled && input.BrakeHeld;
            bool handbrake = ControlsEnabled && input.HandbrakeHeld;

            // ---- 0. Where the road points, and therefore where the car points ON it ----
            RoadHeading = frame.HeadingAt(Z);
            Yaw = WrapPi(WorldHeading - RoadHeading);
            float yawAtStart = Yaw;
            float speedAtStart = Speed;

            int segIndex = _track.SegmentIndexAt(Z);
            Segment seg = _track[segIndex];
            float curvature = seg.curve * RoadGeometry.RoadYawPerCurveUnit;   // rad per unit z

            // ---- 1. Steering input smoothing ------------------------------------
            // Winds on slower than it returns, so the car self-centres crisply but builds
            // lock progressively. The input is two buttons; this is what turns -1/0/+1
            // into something a car can be steered with, and it matters more under rate
            // steering than it did under heading steering, because here a held button
            // accumulates rotation rather than settling at an angle.
            float rate = Mathf.Abs(rawSteer) > 0.01f ? p.steeringResponse : p.steeringReturnSpeed;
            Steer = Mathf.MoveTowards(Steer, rawSteer, rate * dt);

            float speedPct = Mathf.Clamp01(Speed / p.maxSpeed);

            // ---- Surface under the car ------------------------------------------
            float absX = Mathf.Abs(X);
            OffRoad = absX > 1f;

            float surfGrip, surfDrag, surfCap;
            if (OffRoad)
            {
                CurrentSurface = Surface.Grass;
                surfGrip = p.offRoadGrip;
                surfDrag = p.offRoadDrag;
                surfCap  = p.offRoadSpeedCap;
            }
            else if (absX > 0.92f)
            {
                CurrentSurface = Surface.Rumble;
                surfGrip = SurfaceTable.Grip(Surface.Rumble);
                surfDrag = SurfaceTable.DragOf(Surface.Rumble);
                surfCap  = SurfaceTable.SpeedCap(Surface.Rumble);
            }
            else
            {
                CurrentSurface = seg.surface;
                surfGrip = SurfaceTable.Grip(seg.surface);
                surfDrag = SurfaceTable.DragOf(seg.surface);
                surfCap  = SurfaceTable.SpeedCap(seg.surface);
            }
            // The surface's grip is BLENDED IN over about a tenth of a second rather than
            // switched. Grip appears inside a clamp on the tyres' lateral force, so a step
            // change in it is a step change in force: coming back onto tarmac from a slide
            // on the grass, the slide was caught in a single frame and the car snapped
            // sideways - 0.25 half-widths of lateral velocity in one step, measured. That
            // is the catapult [23] exists to catch. Tyres re-load over a moment; this says
            // so, and it is the only smoothing anywhere in the model.
            if (_surfaceBlend <= 0f) _surfaceBlend = surfGrip;
            _surfaceBlend = Mathf.MoveTowards(_surfaceBlend, surfGrip, p.surfaceGripBlend * dt);
            surfGrip = _surfaceBlend;
            SurfaceGrip = surfGrip;

            // ---- 2. The steering asks for a WORLD ROTATION RATE ------------------
            //
            // Not a road-relative heading. This is the change, and everything the player
            // feels follows from it:
            //
            //   tap     - a small, predictable amount of rotation and then nothing
            //   hold    - continuous rotation, at a rate the speed decides
            //   release - rotation stops; the car keeps the heading it has and stays planted
            //
            // and, the point of the exercise: with the button untouched the command is
            // exactly zero, so the car keeps pointing where it pointed while the road turns
            // away underneath it.
            //
            // Authority falls with speed (the same steeringBySpeed curve as before) because
            // a car that can rotate as hard at 200 as at 40 is unplaceable, and it ALSO
            // ramps in from a standstill, because rotation with no speed is a pivot and
            // this is not a tank. Above yawAuthoritySpeed the ramp is fully in.
            float authority = Mathf.Lerp(p.lowSpeedSteering, p.highSpeedSteering,
                                         p.steeringBySpeed.Evaluate(speedPct)) * p.steeringStrength;
            float speedGate = Mathf.Clamp01(speedPct / Mathf.Max(0.01f, p.yawAuthoritySpeed));
            float yawCeiling = p.maxYawRate * authority * speedGate
                             * (1f - p.offRoadYawLoss * (1f - surfGrip))
                             * (1f + p.driftYawGain * DriftAmount);

            YawRateCeiling = yawCeiling;

            // THE SELF-ALIGNING MOMENT, and what makes a drift a drift rather than a
            // pirouette. The further sideways the car is actually travelling, the harder
            // the tyres push the nose back toward the direction of travel - so the
            // rotation a drift can sustain is bounded by the slide it creates, and the two
            // settle against each other.
            //
            // Without it, boosting the rotation rate for a drift boosts it without limit:
            // the car reached 162 deg/s and turned a full circle in a little over two
            // seconds, which reads as a spin and not as a drift. With it the same input
            // settles at about 110 deg/s and 26 degrees of slip, which is one.
            //
            // On tarmac, inside the grip limit, the slip angle is exactly zero and this
            // term contributes exactly nothing - so it changes no part of ordinary driving.
            float aligning = SlipAngle * p.yawSelfAlign;

            // THE STEERING YAW ENVELOPE. Rate steering means a HELD button keeps rotating
            // the car for as long as it is held, and nothing else in the model bounds how
            // far that takes it from the road: holding full lock turned the car 110-136
            // degrees off the road in corners, at which point Speed*cos(Yaw) is negative
            // and the car drives BACKWARDS round the course (FullLockProbe [F1], [F2]). A
            // pseudo-3D view cannot show that; it can only show a car going down the road.
            //
            // So the wheel loses the authority to turn the car FURTHER from the road
            // between steerYawSoftLimit and steerYawHardLimit (45 -> 70 degrees). It is a
            // limit on the PLAYER'S command and nothing else: steering back toward the road
            // keeps full authority, and with the controls untouched the steering term is
            // zero, so this can never rotate the car - it is not a spring and it does not
            // follow the road. AutoTurnProbe is unaffected by construction.
            float steerCmd = Steer * yawCeiling;
            SteerYawEnvelope = 1f;
            if (steerCmd * Yaw > 0f)
            {
                float soft = p.steerYawSoftLimit;
                float hard = Mathf.Max(soft + 0.01f, p.steerYawHardLimit);
                SteerYawEnvelope = Mathf.Clamp01((hard - Mathf.Abs(Yaw)) / (hard - soft));
            }
            YawRateCommand = steerCmd * SteerYawEnvelope - aligning;

            // First order lag, not a spring. A spring overshoots, and an overshoot on a
            // digital input is the first half of a pendulum - which is exactly the failure
            // the previous two models were built to avoid and which this must not
            // reintroduce. One pole, critically nothing: it arrives and stops.
            //
            // Two time constants on the one pole: winding ON at yawRateResponse, winding OFF
            // at yawRateRelease. "Off" is any step where the command asks for less rotation
            // than the car has, or for rotation the other way - so a release, an ease-off,
            // and a reversal down to zero all stop the rotation at the faster rate, and a
            // reversal then builds the other side at the ordinary one. Still one pole on
            // every step, so it still cannot overshoot. It reads the car's own command and
            // rotation and nothing else: no road, no position, no curvature.
            bool windingOff = YawRateCommand * WorldYawRate <= 0f
                           || Mathf.Abs(YawRateCommand) < Mathf.Abs(WorldYawRate);
            float lagRate = windingOff ? Mathf.Max(p.yawRateResponse, p.yawRateRelease)
                                       : p.yawRateResponse;
            float lag = 1f - Mathf.Exp(-Mathf.Max(0.01f, lagRate) * dt);
            WorldYawRate += (YawRateCommand - WorldYawRate) * lag;

            // ---- 3. Grip: how much of that rotation the tyres can actually pay for ----
            //
            // Turning at WorldYawRate while travelling at Speed demands a lateral
            // acceleration of Speed * WorldYawRate. The tyres supply it up to a ceiling.
            // Whatever is left over does not vanish - it becomes the car sliding sideways
            // in its OWN frame, which is understeer when the surface is the limit and a
            // drift when the handbrake is.
            //
            // In the body frame, with u forward and w sideways, rotating at r:
            //     dw/dt = lateralForce - u * r
            // and the tyres want to both hold the turn and kill any w they find:
            //     lateralForce = clamp(u * r - w * alignRate, +/- capacity)
            // so inside the limit dw/dt = -w * alignRate and the car simply tracks its
            // nose, and outside it the shortfall integrates into a genuine slide.
            CurrentGrip = surfGrip * (1f - p.driftGripLoss * DriftAmount);
            float capacity  = p.maxLateralAccel * CurrentGrip;
            float alignRate = p.grip * p.lateralDamping * CurrentGrip;

            float demand = Speed * WorldYawRate;
            float lateralForce = Mathf.Clamp(demand - SlipVel * alignRate, -capacity, capacity);
            SlipVel += (lateralForce - demand) * dt;

            // A slide is bounded by an angle, not by a speed: past about 35 degrees the car
            // is spinning rather than drifting, and every quantity downstream that takes an
            // atan of it stops meaning anything.
            float slipBound = Mathf.Max(Speed, 1f) * Mathf.Tan(p.maxSlipAngle);
            SlipVel = Mathf.Clamp(SlipVel, -slipBound, slipBound);

            // ---- 4. Drift state -------------------------------------------------
            // Counter-steering is judged against the SLIDE, and in this frame a slide to
            // the car's left (SlipVel < 0) is caught by steering left. Read from the RAW
            // input, not the smoothed wheel: coming out of a slide the wheel is wound fully
            // the other way, and a smoothed test does not notice the player asking for
            // opposite lock until the recovery is half over.
            CounterSteering = Mathf.Abs(SlipVel) > p.maxLateralAccel * 0.004f
                           && Mathf.Abs(rawSteer) > 0.05f
                           && (rawSteer < 0f) == (SlipVel < 0f);

            // THE ONLY WAY INTO A DRIFT. Not strong steering, not braking, not grass.
            bool wantsDrift = handbrake && speedPct > 0.25f;

            float recovery = p.driftRecovery
                           * (CounterSteering ? p.counterSteerAssist : 1f)
                           * (Mathf.Abs(rawSteer) < 0.01f ? p.neutralDriftRecovery : 1f);
            DriftAmount = Mathf.MoveTowards(DriftAmount, wantsDrift ? 1f : 0f,
                                            (wantsDrift ? p.driftBuildUp : recovery) * dt);

            // ---- 5. Integrate the heading ---------------------------------------
            WorldHeading += WorldYawRate * dt;

            // THE STRAIGHT-ONLY RE-ALIGNMENT, and the only place the road's direction acts
            // on the car rather than merely being subtracted from it.
            //
            // A true world-heading car that is steered on a straight and then released
            // keeps the heading it was given and leaves the road diagonally, for ever. That
            // is correct and it is not what an arcade racer should feel like: releasing on
            // a straight is a request to go straight, and the car should answer it.
            //
            // So it is gated, hard, on the road being STRAIGHT - and on the player having
            // let go. In any real corner the gate is shut and this term contributes exactly
            // nothing, which is what AutoTurnProbe [A4] measures. The gate closes over
            // straightGateCurve, well below the gentlest corner on the circuit (2.0), so a
            // corner's entry ramp leaves it behind within a handful of segments.
            // TWO gates, and the second one is not a refinement - it is the difference
            // between a self-centring car and a car the player cannot steer.
            //
            // The first version of this scaled with (1 - |steer|), which sounds like the
            // same thing and is not: at a third of lock it still applied two thirds of
            // 1.2 rad/s, against a commanded 0.6 rad/s. The alignment simply beat the
            // player. The car could not be moved across a straight road AT ALL - measured
            // by [7b], which drove for fourteen seconds and never left the tarmac because
            // the steering it asked for was cancelled before it reached the heading.
            //
            // So the release gate is now a THRESHOLD, not a taper: below a twenty-fifth of
            // lock the car straightens itself, above a sixth of lock it does exactly what
            // it is told, and the hand-over between the two is a few hundredths of a
            // second of the wheel returning to centre. The player always wins.
            StraightGate = 1f - Mathf.Clamp01(Mathf.Abs(seg.curve) / Mathf.Max(0.01f, p.straightGateCurve));
            float released = 1f - Mathf.Clamp01((Mathf.Abs(Steer) - 0.04f) / 0.12f);
            float align = StraightGate * released * (1f - DriftAmount) * speedGate;
            // PROPORTIONAL, not a fixed rate. A fixed rate is the same size whether the
            // car is 30 degrees off line or 3, so it is gentle where it should be firm
            // and overwhelming where it should be invisible - and the second half of that
            // is what matters, because holding a steady angle across a straight road needs
            // NO rotation at all, so a fixed rate cancels the small commands a player uses
            // to place the car. Proportional means it is strong exactly when the car is
            // badly out of line and fades to nothing as it comes back.
            StraightAlignRate = 0f;
            if (align > 0.001f)
            {
                // Measured the SHORT way round (wrapped), so a car that has been turned a
                // full circle is not unwound through 360 degrees.
                float before = WorldHeading;
                WorldHeading -= WrapPi(WorldHeading - RoadHeading)
                              * (1f - Mathf.Exp(-p.straightAlignRate * align * dt));
                StraightAlignRate = (WorldHeading - before) / dt;
            }

            Yaw = WrapPi(WorldHeading - RoadHeading);

            // ---- 5b. HEADING-CARRY LIMITER ---------------------------------------
            // See HandlingProfile "Heading carry". It bleeds the heading the PLAYER'S OWN
            // STEERING put beyond a limit, and nothing else. _carry is the signed part of
            // Yaw beyond the limit that was captured while the player was steering away from
            // the road (or, in the re-entry window, while holding lock across it); it is
            // bled away once the player stops steering outward. A car with the controls
            // untouched never captures anything, so it is never rotated - the road turning
            // underneath it adds yaw that is NOT in _carry and is never bled. That is the
            // structural difference from road-follow, and AutoTurnProbe is untouched by it.
            CarryLimitRate = 0f;
            if (OffRoad) { _reWindow = 0f; }
            else if (_wasOffRoad && p.carryReWindow > 0f) { _reWindow = p.carryReWindow; _reSide = Mathf.Sign(X); }
            else if (_reWindow > 0f) _reWindow = Mathf.Max(0f, _reWindow - dt);
            _wasOffRoad = OffRoad;

            if (p.carryRate > 0f)
            {
                float ys = Mathf.Sign(Yaw);
                float steerAway = Steer * ys;
                float gate = 1f - Mathf.Clamp01((steerAway - 0.04f) / 0.12f);
                float lim = p.carryLimit;
                bool inRe = _reWindow > 0f && Yaw * _reSide < 0f;   // pointing across the road it just rejoined
                if (inRe)
                {
                    float reFade = Mathf.Clamp01(_reWindow / Mathf.Max(0.01f, p.carryReWindow));
                    lim = Mathf.Lerp(p.carryReLimit, p.carryLimit, gate);
                    gate = Mathf.Max(gate, reFade);
                }
                float excess = Mathf.Max(0f, Mathf.Abs(Yaw) - lim);

                if (steerAway > 0.04f || WorldYawRate * ys > 0.02f || inRe)
                    _carry = ys * excess;                       // the player is making it: track it
                else if (_carry * ys > 0f)
                    _carry = ys * Mathf.Min(Mathf.Abs(_carry), excess);   // never more than is actually there
                else
                    _carry = 0f;                                // the heading flipped sides: nothing owned

                if (_carry != 0f)
                {
                    float bleed = _carry * (1f - Mathf.Exp(-p.carryRate * gate * (1f - DriftAmount) * speedGate * dt));
                    WorldHeading -= bleed;
                    _carry -= bleed;
                    CarryLimitRate = -bleed / dt;
                    Yaw = WrapPi(WorldHeading - RoadHeading);
                }
            }
            else _carry = 0f;

            // ---- 6. Longitudinal ------------------------------------------------
            float boost = 0f;
            if (_boostActive)
            {
                _boostT += dt;
                if (_boostT < _boostHold) boost = 1f;
                else
                {
                    float f = (_boostT - _boostHold) / _boostFade;
                    if (f >= 1f) _boostActive = false;
                    else boost = 1f - f * f * (3f - 2f * f);   // smoothstep out
                }
            }
            BoostLevel = boost;
            // Exactly maxSpeed with no boost, so an unboosted car is bit-identical.
            float topSpeed = boost > 0f ? p.maxSpeed * (1f + _boostTop * boost) : p.maxSpeed;

            float a;
            if (brake)
                a = -p.brakeStrength * p.maxSpeed
                    * Mathf.Lerp(p.brakeLowSpeedFactor, 1f, speedPct);
            else if (ThrottleEnabled && ControlsEnabled)
            {
                a = p.acceleration * p.maxSpeed * p.accelerationBySpeed.Evaluate(speedPct);
                // The kick, tapered over the last 5% below the raised ceiling so it
                // arrives at it rather than hitting it.
                if (boost > 0f)
                    a += _boostAccel * p.maxSpeed * boost
                       * Mathf.Clamp01((topSpeed - Speed) / (0.05f * p.maxSpeed));
            }
            else
                a = -(p.coastDrag + FinishDecel) * p.maxSpeed;

            // Sliding scrubs speed - what stops "drift everywhere" being optimal. Charged
            // on how far sideways the car is actually going, so a car cornering cleanly at
            // full lock pays nothing at all and ordinary steering is never quietly taxed.
            float scrub = Mathf.Clamp01(Mathf.Abs(SlipVel) / Mathf.Max(1f, slipBound));
            a -= scrub * p.driftSpeedLoss * p.maxSpeed;
            a -= surfDrag * p.maxSpeed;

            // Surface speed ceilings are a FORCE, not a clamp: a surface change must alter
            // what acts on FUTURE speed, never reset the speed already accumulated.
            // On a full-speed surface the cap is the (possibly boosted) top speed; a slow
            // surface keeps its own ceiling - a boost does not make grass fast.
            float surfaceCap = surfCap >= 1f ? topSpeed : p.maxSpeed * surfCap;
            if (Speed > surfaceCap) a -= (Speed - surfaceCap) * p.surfaceCapBleed;

            Speed = Mathf.Clamp(Speed + a * dt, 0f, topSpeed);

            // ---- 7. Move, in the road's frame -----------------------------------
            //
            // The body-frame velocity resolved onto the road's tangent and normal. This is
            // the half of the fix a visual-only change would have missed: if position still
            // advanced along the centre line, the car would merely LOOK like it was not
            // turning while continuing to be carried round the corner.
            // Resolved at the MIDDLE of the step, not at its end. Both the heading and
            // the speed move during a step, and taking either at one end makes the
            // integration first-order - which on a model where thumb reaches position
            // through three integrations is enough to make 30 FPS and 60 FPS visibly
            // different cars. The average of the two ends is the trapezoid rule and costs
            // one add each.
            // Half the WRAPPED change, so a step that crosses +/-180 does not average to 0.
            float yawMid   = yawAtStart + WrapPi(Yaw - yawAtStart) * 0.5f;
            float speedMid = (speedAtStart + Speed) * 0.5f;
            float cy = Mathf.Cos(yawMid), sy = Mathf.Sin(yawMid);
            AlongVel   = speedMid * cy - SlipVel * sy;
            float vLat = speedMid * sy + SlipVel * cy;

            // The Frenet correction: a metre of road on the inside of a corner covers less
            // ground than a metre on the outside, so progress along the centre line is not
            // the same as progress through the world. Clamped well clear of zero - the
            // tightest corner on the circuit reaches about 0.53 at the outer barrier.
            float denom = Mathf.Clamp(1f - curvature * X * RoadGeometry.RoadHalfWidth, 0.35f, 2.0f);

            RoadYawRate = curvature * (AlongVel / denom);
            LateralVel  = vLat / RoadGeometry.RoadHalfWidth;

            Z += (AlongVel / denom) * dt;
            X += LateralVel * dt;

            // ---- Telemetry the presentation reads -------------------------------
            // The slide is re-bounded against the speed the car ACTUALLY ends the step
            // with. It was bounded once already, before the longitudinal step; braking
            // hard or crossing onto grass then drops the speed underneath it, and a bound
            // that was true a moment ago stops being true - which is how a 35 degree cap
            // came to be reported at 50.
            float endBound = Mathf.Max(Speed, 1f) * Mathf.Tan(p.maxSlipAngle);
            SlipVel = Mathf.Clamp(SlipVel, -endBound, endBound);

            SlipAngle = -Mathf.Atan2(SlipVel, Mathf.Max(Speed, 1f));
            SlipRatio = Mathf.Clamp01(Mathf.Abs(SlipVel)
                        / Mathf.Max(1f, p.slipReference * RoadGeometry.RoadHalfWidth));

            // Brake skid: visual only (see BrakeSkid). Smoothed wheel, fixed step, so it
            // is the same at 30 and 60 FPS; held for brakeSkidHoldSeconds before it shows.
            float sp = Mathf.Clamp01(Speed / p.maxSpeed);
            bool skidArmed = brake && !OffRoad && sp >= p.brakeSkidMinSpeed
                          && Mathf.Abs(Steer) > p.brakeSkidMinSteer;
            _brakeSkidHeld = skidArmed ? _brakeSkidHeld + dt : 0f;
            float skidTarget = 0f;
            if (skidArmed && _brakeSkidHeld >= p.brakeSkidHoldSeconds)
            {
                float gs = Mathf.Clamp01((sp - p.brakeSkidMinSpeed) / Mathf.Max(0.01f, p.brakeSkidSpeedRamp));
                float gt = Mathf.Clamp01((Mathf.Abs(Steer) - p.brakeSkidMinSteer)
                                         / Mathf.Max(0.01f, 1f - p.brakeSkidMinSteer));
                skidTarget = Mathf.Lerp(0.4f, 1f, gs * gt);
            }
            BrakeSkid = Mathf.MoveTowards(BrakeSkid, skidTarget, (skidTarget > BrakeSkid ? 8f : 4f) * dt);

            // ---- 8. Barrier collisions ------------------------------------------
            // Analytic, in road space. No colliders, no Physics2D.
            //
            // The invariant this code exists to guarantee: the outer boundary must NEVER
            // trap the car. Contact costs momentum and redirects, and there is always a
            // clear path back toward the road. Everything here is now expressed in the
            // car's own frame, because that is where the state lives - an impact rotates
            // the BODY in the world, and the bounce is applied to the sideways velocity
            // that actually produces it.
            //
            // The limits are read at the car's NEW z, interpolated across the segment
            // exactly as the tyre wall is drawn (TrackModel.BarrierLeftAt). They used to be
            // the segment's own value, taken before the move: a step function under a wall
            // drawn as a line, so where the run-off changed width the car met an invisible
            // wall on one side of the joint and drove through the visible one on the other
            // (TyreWallContinuityProbe [W2]). On a constant-width run-off - every wall the
            // full-lock and handling probes touch - this is the same number as before.
            float limitL = _track.BarrierLeftAt(Z);
            float limitR = _track.BarrierRightAt(Z);
            // Where the run-off changes width the wall itself runs at an angle to the
            // road, so it moves sideways under a car travelling along z. An impact is
            // judged against the WALL's direction: a car running straight into a
            // narrowing wall is hitting it, and the bounce leaves along the wall. Zero
            // wherever the width is constant, which leaves every existing wall untouched.
            float slopeL, slopeR;
            _track.BarrierSlopeAt(Z, out slopeL, out slopeR);
            float zRate = AlongVel / denom;

            // TOUCHING, as distinct from PENETRATING. The response below pushes the car
            // back off the wall (bounce, inward bias), so on the very next step X sits a
            // few thousandths of a half-width inside the limit and a strict "X beyond the
            // limit" test reads NOT touching. The anti-trap yaw recovery used to live
            // behind that strict test, so it fired one step in fourteen while the held
            // steering re-aimed the nose at the wall on the other thirteen: measured, a
            // car holding full lock sat 17-19 degrees nose-into the wall, crabbing, at 9%
            // of top speed for as long as the button was held (FullLockProbe [F1] "never crawls"). The
            // recovery now acts whenever the car is within a hair of the wall.
            float contactSide = 0f;
            if (X <= limitL + BarrierContactBand) contactSide = -1f;
            else if (X >= limitR - BarrierContactBand) contactSide = 1f;

            if (contactSide != 0f && !(X < limitL || X > limitR))
            {
                // Touching but not penetrating: no impact, no clamp, no bias - only the
                // rule that the nose may not stay pointed into the wall.
                if (BarrierYawRecovery(contactSide, dt))
                {
                    LateralVel = (Speed * Mathf.Sin(Yaw) + SlipVel * Mathf.Cos(Yaw))
                               / RoadGeometry.RoadHalfWidth;
                }
            }

            if (X < limitL || X > limitR)
            {
                // Resolve by SIDE, not by the sign of a value we are about to mutate.
                // +1 = against the right barrier, -1 = against the left.
                float side = (X > limitR) ? 1f : -1f;
                X = (side > 0f) ? limitR : limitL;

                // Only motion still heading INTO the wall is an impact. Testing merely
                // "outside the limit" made the whole response re-fire every step while the
                // car rested against the barrier, which is what used to pin it there.
                float wallVel = (side > 0f ? slopeR : slopeL) * zRate;
                float into = (LateralVel - wallVel) * side;
                if (into > 0f)
                {
                    // Scale by how squarely we hit - the tangent of the approach angle - so
                    // a glancing scrape is not punished like a head-on. About 27 degrees
                    // counts as fully square.
                    float approach = into * RoadGeometry.RoadHalfWidth / Mathf.Max(Speed, 1f);
                    float squareness = Mathf.Clamp01(approach / 0.5f);
                    float impact = Mathf.Clamp01(Speed / p.maxSpeed) * squareness;

                    // Proportional loss - never to zero, and never a control lock.
                    Speed *= (1f - p.collisionSpeedLoss * impact);

                    // Redirect INWARD rather than mirroring, so the rebound always carries
                    // the car back toward the playable area. The lateral velocity the road
                    // frame sees is Speed*sin(Yaw) + SlipVel*cos(Yaw); the only part of it
                    // this can change without teleporting the body is SlipVel, so the
                    // target is solved for rather than assigned. A car pointed hard into
                    // the wall therefore cannot bounce cleanly off it - it scrapes along,
                    // which is right.
                    float wantLat = (wallVel - side * into * p.collisionBounce) * RoadGeometry.RoadHalfWidth;
                    float c = Mathf.Cos(Yaw);
                    if (Mathf.Abs(c) > 0.2f)
                    {
                        SlipVel = (wantLat - Speed * Mathf.Sin(Yaw)) / c;
                        float bound = Mathf.Max(Speed, 1f) * Mathf.Tan(p.maxSlipAngle);
                        SlipVel = Mathf.Clamp(SlipVel, -bound, bound);
                    }

                    // Kick the nose AWAY from the wall, in the world. This is a real
                    // rotation of the body and it is the right one: hitting a wall with
                    // the left front turns the car right.
                    WorldYawRate += -side * p.collisionYawKick * impact;

                    if (impact > _pendingImpact)
                    {
                        _pendingImpact = impact;
                        _pendingImpactSide = side;
                    }
                }

                // Touching the barrier must be an UNSTABLE state whichever way we got here.
                // Rotate the body away from the wall, stop rotation driving further into
                // it, and add a small inward bias. Together these mean the car can never
                // sit pushing into the wall indefinitely.
                BarrierYawRecovery(side, dt);
                SlipVel += -side * p.barrierInwardBias * RoadGeometry.RoadHalfWidth * dt;
                // The slide's angular bound applies here too, RECOMPUTED - an impact has
                // just taken a third of the speed away, and a bound worked out before that
                // is a bound on a car that no longer exists. This block also runs after the
                // telemetry, so without the clamp a car scraping along a wall accumulated
                // the inward bias with nothing holding it, and the 35 degree cap was
                // reported at 50.
                float wallBound = Mathf.Max(Speed, 1f) * Mathf.Tan(p.maxSlipAngle);
                SlipVel = Mathf.Clamp(SlipVel, -wallBound, wallBound);
                LateralVel = (Speed * Mathf.Sin(Yaw) + SlipVel * Mathf.Cos(Yaw))
                           / RoadGeometry.RoadHalfWidth;
                SlipAngle = -Mathf.Atan2(SlipVel, Mathf.Max(Speed, 1f));
            }
        }

        /// <summary>
        /// Half-widths inside a barrier that still count as TOUCHING it for the yaw
        /// recovery - about 33 units. The bounce and inward bias move a scraping car about
        /// 0.005 per step off the wall, so this holds contact through that cycle without
        /// reaching anything that could be called driving beside the wall.
        /// </summary>
        public const float BarrierContactBand = 0.03f;

        /// <summary>
        /// The anti-trap rule: a car touching the barrier on <paramref name="side"/> with
        /// its nose pointed INTO it is rotated back toward the road's heading, and rotation
        /// driving further into the wall is stopped. Returns true if it acted. Measured the
        /// short way round (Yaw is wrapped).
        /// </summary>
        private bool BarrierYawRecovery(float side, float dt)
        {
            if (Yaw * side <= 0f) return false;
            float step = Mathf.Min(Mathf.Abs(Yaw), _profile.barrierYawRecovery * dt);
            WorldHeading -= Mathf.Sign(Yaw) * step;
            Yaw = WrapPi(WorldHeading - RoadHeading);
            if (WorldYawRate * side > 0f) WorldYawRate = 0f;
            return true;
        }

        /// <summary>Wrap an angle into [-pi, pi).</summary>
        public static float WrapPi(float a)
        {
            const float TwoPi = 2f * Mathf.PI;
            a -= TwoPi * Mathf.Floor((a + Mathf.PI) / TwoPi);
            return a;
        }

        /// <summary>
        /// The rotation rate the road will be asking for <paramref name="seconds"/> from
        /// now, if the car keeps its present pace - i.e. what a driver LOOKING AHEAD sees
        /// coming. A pure query on the track: nothing in <see cref="Step"/> reads it, and
        /// no force in the model depends on it. It exists because a scripted driver that
        /// only ever reacts to the curvature under its own wheels is permanently a corner
        /// entry late, and a human is not.
        /// </summary>
        public float RoadYawRateAhead(float seconds)
        {
            if (_track == null) return 0f;
            float ahead = Z + Mathf.Max(0f, seconds) * Mathf.Max(AlongVel, Speed);
            return _track.WorldFrame.CurvatureAt(ahead) * Mathf.Max(AlongVel, Speed);
        }

        /// <summary>Curve strength of the segment under the car, for the camera rig.</summary>
        public float CurrentCurve
        {
            get { return _track != null ? _track.CurveAt(Z) : 0f; }
        }
    }
}
