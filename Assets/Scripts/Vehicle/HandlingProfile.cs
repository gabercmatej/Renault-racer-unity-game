using UnityEngine;

namespace RenaultRacer.Vehicle
{
    /// <summary>
    /// The complete tuning surface for the driving model. Every value the car's feel
    /// depends on lives here and nowhere else, so we can A/B whole handling personalities
    /// by swapping one asset reference.
    ///
    /// THE EIGHT KNOBS THAT MATTER, in the order you would reach for them:
    ///
    ///   maxYawRate                       how fast the car can rotate at full lock
    ///   highSpeedSteering                how much of that is left at racing speed
    ///   yawRateResponse                  how quickly rotation starts and stops
    ///   maxLateralAccel                  how much cornering the tyres can pay for
    ///   grip (x lateralDamping)          how fast a slide is caught
    ///   straightAlignRate                how composed a release on a straight feels
    ///   brakeStrength                    how much the brake buys back
    ///   driftYawGain / driftGripLoss     what a deliberate drift feels like
    ///
    /// THE ONE NUMBER TO REASON WITH, and the one every track difficulty question reduces
    /// to, is the curvature the car can HOLD at a given speed. Holding a line means
    /// rotating exactly as fast as the road does:
    ///
    ///     road rotation rate = curve / SegmentLength^2 * speed          (SegmentLength 200)
    ///     car rotation rate  = maxYawRate * authority(speed) * lock
    ///
    /// so at full lock the curvature that can be held is
    ///
    ///     curve_max(v) = maxYawRate * authority(v) * 40000 / v
    ///
    /// which on the shipping profile is about 6.5 at top speed, 9.1 at 80% and 10.8 at
    /// 70%. That is why the brake has a job and why it pays better than linearly: the
    /// required rotation falls with speed while the available rotation RISES, because
    /// steering authority is higher lower down.
    ///
    /// WHAT LEFT THIS FILE IN THE WORLD-HEADING REBUILD, and why, because a reader coming
    /// from an older profile asset will miss them:
    ///
    ///   maxYawAngle, yawSettleRate, yawZeta   steering asked for a road-relative ANGLE
    ///                                          and a spring pulled the nose to it. That
    ///                                          spring WAS the auto-turn: it held the car
    ///                                          aligned to the road for free.
    ///   centrifugalStrength, curveExponent    an outward push, which only made sense as a
    ///                                          counterweight to a car that could not
    ///                                          otherwise fail a corner. A car that does
    ///                                          not turn already fails it; adding a push
    ///                                          would charge the player twice and would
    ///                                          shove a car that is not turning at all.
    ///   lateralAcceleration                   replaced by maxLateralAccel, which is a
    ///                                          real acceleration in world units and not a
    ///                                          normalised one.
    ///   lowSpeedLateralFloor, maxLateralVel   lateral motion is no longer integrated, so
    ///                                          there is nothing to floor or to clamp; it
    ///                                          is resolved out of speed and heading.
    ///   driftYawLag, driftSteerLoss,          the spring and the normalised push they
    ///   offRoadSteerLoss                       modified are both gone.
    /// </summary>
    [CreateAssetMenu(fileName = "HandlingProfile", menuName = "Renault Racer/Handling Profile", order = 1)]
    public sealed class HandlingProfile : ScriptableObject
    {
        // ---------------------------------------------------------------- Longitudinal
        [Header("Longitudinal")]
        [Tooltip("Top speed, world units/sec. Calibrated for the target lap time.")]
        public float maxSpeed = 12000f;

        [Tooltip("Acceleration as a fraction of maxSpeed per second.")]
        [Range(0.05f, 0.5f)] public float acceleration = 0.20f;

        [Tooltip("Acceleration falloff across the speed range. Punchy low, tapering near top.")]
        public AnimationCurve accelerationBySpeed = DefaultAccelCurve();

        [Tooltip("Braking force as a fraction of maxSpeed per second. The brake is the " +
                 "player's only speed control (the throttle is automatic), and it is what " +
                 "makes a tight corner possible: the rotation a corner demands is " +
                 "proportional to speed, while the rotation the car can produce RISES as " +
                 "it slows, so a fifth off the entry speed buys about 40% more curvature.")]
        [Range(0.2f, 2.5f)] public float brakeStrength = 1.0f;

        [Tooltip("Brake force multiplier as the car approaches a standstill, blended in by " +
                 "speed. Below 1 the brake eases off as you slow, so it sets up a corner " +
                 "instead of behaving like an emergency stop. Holding the brake still " +
                 "brings the car to a complete stop. 1 = a flat response.")]
        [Range(0.2f, 1f)] public float brakeLowSpeedFactor = 1.0f;

        [Tooltip("Drag when neither accelerating nor braking, fraction of maxSpeed per second.")]
        [Range(0.01f, 0.30f)] public float coastDrag = 0.07f;

        [Tooltip("How fast speed above a surface's ceiling bleeds off, per second. The " +
                 "ceiling is a FORCE, not a clamp: clamping made crossing onto grass halve " +
                 "the speed in one step, and made the ceiling snap back the instant the car " +
                 "touched asphalt again.")]
        [Range(0.3f, 8f)] public float surfaceCapBleed = 2.0f;

        // -------------------------------------------------------------------- Steering
        [Header("Steering")]
        [Tooltip("Overall steering authority multiplier.")]
        [Range(0.3f, 2.5f)] public float steeringStrength = 1.0f;

        [Tooltip("Steering authority at a standstill. Multiplies maxYawRate.")]
        [Range(0.5f, 8f)] public float lowSpeedSteering = 1.30f;

        [Tooltip("Steering authority at top speed. Multiplies maxYawRate. Lower = more " +
                 "stable at pace, and directly sets the flat-out curvature limit.")]
        [Range(0.2f, 1.2f)] public float highSpeedSteering = 0.75f;

        [Tooltip("Blend from low- to high-speed steering across the speed range. " +
                 "The single highest-value curve in the project.")]
        public AnimationCurve steeringBySpeed = DefaultSteeringCurve();

        [Tooltip("How fast steering winds ON, units/sec. Lower = heavier. This matters " +
                 "more under rate steering than it did under heading steering: a held " +
                 "button now accumulates rotation instead of settling at an angle, so how " +
                 "quickly the lock arrives is how much a tap is worth.")]
        [Range(2f, 18f)] public float steeringResponse = 7.0f;

        [Tooltip("How fast steering returns to centre, units/sec. Faster than response " +
                 "gives a crisp car that still builds lock progressively.")]
        [Range(3f, 22f)] public float steeringReturnSpeed = 9.0f;

        // ------------------------------------------------------------------- Rotation
        //
        // STEERING ASKS FOR A ROTATION RATE, IN THE WORLD.
        //
        // Not a heading, and above all not a heading measured against the ROAD. Both
        // earlier models steered by angle, and the road-relative one had the defect this
        // whole rebuild exists to remove: with the wheel centred the car's target heading
        // was the road's heading, so the road turned the car. Measured at 100% of the
        // road's rotation inherited by the body and 93% by the trajectory.
        //
        // Rate steering makes the plant thumb -> rotation -> heading -> position a triple
        // integrator, which is one more than the heading model and one fewer than the
        // rate model that preceded THAT. The three things that keep it flyable on two
        // glass buttons are all here: authority that falls with speed, a first-order lag
        // rather than a spring (a spring overshoots, and an overshoot on a digital input
        // is a pendulum), and the straight-only re-alignment that answers a release.
        [Header("Rotation (steering asks for a world rotation rate)")]
        [Tooltip("Rotation rate, rad/sec, that FULL lock asks for before speed authority " +
                 "is applied. With highSpeedSteering 0.75 this gives about 100 deg/sec at " +
                 "top speed, which is the engineering value the rebuild started from. It " +
                 "sets the flat-out curvature limit directly: curve_max = this * " +
                 "authority * 40000 / speed.")]
        [Range(0.5f, 6f)] public float maxYawRate = 2.6f;

        [Tooltip("How fast the car's rotation rate answers the wheel, per second. A ONE " +
                 "POLE lag, deliberately, not a spring: it arrives and stops. 9 settles a " +
                 "change of rate in about a quarter of a second. Higher feels darty; lower " +
                 "feels like the car is on a rope.")]
        [Range(2f, 30f)] public float yawRateResponse = 13.0f;

        [Tooltip("Fraction of top speed at which rotation authority is fully available. " +
                 "Below it the car rotates proportionally less, so a stopped car does not " +
                 "pivot on the spot - rotation is something a moving car does. Low enough " +
                 "that recovering from a barrier or from grass is still prompt.")]
        [Range(0.05f, 0.6f)] public float yawAuthoritySpeed = 0.25f;

        [Tooltip("How fast the straight-only re-alignment brings the car back onto the " +
                 "road's heading, per second - a PROPORTIONAL rate, so it is firm when the " +
                 "car is badly out of line and fades to nothing as it comes back. THE ONE " +
                 "ROAD-REFERENCED TERM in the model. It exists because a true world-heading car that is steered on " +
                 "a straight and released keeps that heading for ever, which is correct " +
                 "and is not what an arcade racer should feel like. It is gated shut in " +
                 "every real corner - see straightGateCurve - and AutoTurnProbe [A4] is " +
                 "the guard that it stays that way.")]
        [Range(0f, 8f)] public float straightAlignRate = 6.0f;

        [Tooltip("Curvature at which the straight-only re-alignment is fully OFF. The " +
                 "gentlest corner on the circuit is 2.0, so anything at or below about 1.2 " +
                 "leaves every corner untouched while still covering the true straights, " +
                 "which are authored at curvature exactly zero. Raising this past 2 would " +
                 "start steering the car through corners again.")]
        [Range(0.1f, 3f)] public float straightGateCurve = 1.2f;

        // ---------------------------------------------------------------------- Grip
        //
        // GRIP ALIGNS THE CAR'S VELOCITY TO THE CAR, NOT TO THE ROAD.
        //
        // The old grip term damped lateral velocity measured in ROAD space, which is a
        // spring pulling the car's motion onto the road's tangent - road-following wearing
        // a tyre's clothes, and one of the two halves of the auto-turn. What the tyres
        // actually do is kill sideways motion measured against the car's OWN nose.
        [Header("Grip")]
        [Tooltip("Lateral acceleration the tyres can produce on dry asphalt, world " +
                 "units/sec^2. Cornering demands speed * rotation rate; anything the tyres " +
                 "cannot pay for becomes a genuine slide. Sized about 10% above what full " +
                 "lock at top speed asks for, so ordinary driving is planted and has no " +
                 "slip at all, and grass and the handbrake are what put the car sideways.")]
        [Range(2000f, 60000f)] public float maxLateralAccel = 25500f;

        [Tooltip("How fast a slide is caught, per second. THE planted-vs-ice knob. It " +
                 "damps sideways motion in the CAR's frame, so it decides how long a slide " +
                 "lasts once the cause of it has gone.")]
        [Range(1f, 18f)] public float grip = 7.0f;

        [Tooltip("Multiplier on grip. One knob, two names, kept because it is handy to " +
                 "trim a whole family of profiles without touching each.")]
        [Range(0.3f, 2f)] public float lateralDamping = 0.85f;

        [Tooltip("The self-aligning moment, rad/sec of rotation opposed per radian of slip " +
                 "angle. The further sideways the car is travelling, the harder the tyres " +
                 "push the nose back toward where it is going. It is what bounds a drift: " +
                 "the rotation and the slide settle against each other instead of the " +
                 "rotation running away into a spin. Inside the grip limit the slip angle " +
                 "is exactly zero, so this changes nothing about ordinary driving.")]
        [Range(0f, 8f)] public float yawSelfAlign = 2.0f;

        [Tooltip("How fast a change of surface is blended into the tyres' grip, per " +
                 "second. Grip sits inside a clamp on lateral force, so switching it in " +
                 "one frame is a step change in force - which is what catapulted a car " +
                 "coming back onto tarmac from a slide on the grass. About a tenth of a " +
                 "second is a tyre re-loading; much slower and grass stops biting.")]
        [Range(2f, 60f)] public float surfaceGripBlend = 10f;

        [Tooltip("Slip angle, radians, at which a slide is capped. Past about 35 degrees " +
                 "the car is spinning rather than drifting and everything downstream that " +
                 "takes an angle of it stops meaning anything.")]
        [Range(0.2f, 1.2f)] public float maxSlipAngle = 0.62f;

        [Tooltip("How much of the car's ROTATION a low-grip surface takes away, as a " +
                 "fraction of the grip it takes away. 0.35 means grass, at 40% grip, still " +
                 "rotates at about 79% - so the car turns but does not GO there, which is " +
                 "understeer and is what grass should feel like. Without it grass would " +
                 "rotate exactly as sharply as asphalt.")]
        [Range(0f, 1f)] public float offRoadYawLoss = 0.35f;

        // ------------------------------------------------------------------- Drift
        //
        // DRIFT IS A STATE THE PLAYER ASKS FOR, AND NOTHING ELSE STARTS IT.
        //
        // Not strong steering, not low grip, not grass, not braking in a corner. The
        // handbrake gesture above a speed floor is the only way in. Every one of those
        // other routes existed in an earlier model and every one of them read as the car
        // losing control by itself.
        [Header("Drift (deliberate only - the handbrake gesture is the only way in)")]
        [Tooltip("How much of the tyres' lateral grip is given up at full drift. 0.62 " +
                 "leaves 38%, which is below what the boosted rotation demands, so the car " +
                 "genuinely slides rather than merely rotating faster.")]
        [Range(0.1f, 0.9f)] public float driftGripLoss = 0.62f;

        [Tooltip("Extra rotation rate the drift asks for, as a fraction of the normal " +
                 "ceiling. 1.1 means a fully drifting car rotates a bit over twice as fast " +
                 "for the same input - the hanging-out nose that makes a drift read as one.")]
        [Range(0f, 2f)] public float driftYawGain = 0.45f;

        [Tooltip("How fast grip lets go once the gesture is held, per second.")]
        [Range(1f, 10f)] public float driftBuildUp = 5.0f;

        [Tooltip("How fast the car hooks back up, per second.")]
        [Range(0.5f, 6f)] public float driftRecovery = 2.0f;

        [Tooltip("Recovery multiplier while counter-steering. This is what makes drifting " +
                 "a skill rather than a random loss of control.")]
        [Range(1f, 4.5f)] public float counterSteerAssist = 3.0f;

        [Tooltip("Drift recovery multiplier at zero steering input. Letting go of " +
                 "everything is a request to stop drifting, and the car should answer it as " +
                 "promptly as driftBuildUp answers the request to start.")]
        [Range(1f, 4f)] public float neutralDriftRecovery = 1.7f;

        [Tooltip("Speed scrubbed off while sliding, as a fraction of maxSpeed per second " +
                 "at full slip. This is what makes a clean line faster than flailing - do " +
                 "not set it to zero. Charged on how far SIDEWAYS the car is actually " +
                 "going, so a car cornering cleanly at full lock pays nothing at all.")]
        [Range(0.05f, 1.2f)] public float driftSpeedLoss = 0.25f;

        [Tooltip("Sideways speed, in road half-widths per second, that counts as fully " +
                 "scrubbing for SlipRatio - the 0..1 number smoke, squeal and skid marks " +
                 "read. Purely presentation; no force depends on it. Lower = marks and " +
                 "smoke appear sooner.")]
        [Range(0.3f, 4f)] public float slipReference = 1.5f;

        // ---------------------------------------------------------- Surface and impact
        [Header("Off-road")]
        [Tooltip("Grip multiplier on grass. Slidey but recoverable.")]
        [Range(0.1f, 1f)] public float offRoadGrip = 0.40f;

        [Tooltip("Extra drag off-road, fraction of maxSpeed per second. Keep this BELOW " +
                 "acceleration so offRoadSpeedCap stays the binding constraint - otherwise " +
                 "drag wins at every speed and grass becomes a full stop.")]
        [Range(0f, 0.8f)] public float offRoadDrag = 0.09f;

        [Tooltip("Speed ceiling off-road, fraction of maxSpeed.")]
        [Range(0.2f, 1f)] public float offRoadSpeedCap = 0.50f;

        [Header("Collision")]
        [Tooltip("Fraction of speed lost in a full-speed barrier hit. Never a full stop.")]
        [Range(0.1f, 0.9f)] public float collisionSpeedLoss = 0.45f;

        [Tooltip("How much lateral velocity is reflected back onto the road.")]
        [Range(0f, 1f)] public float collisionBounce = 0.55f;

        [Tooltip("Rotation, rad/sec, imparted by an impact, applied AWAY from the barrier " +
                 "struck. This is a real rotation of the body in the world: hitting a wall " +
                 "with the left front turns the car right.")]
        [Range(0f, 8f)] public float collisionYawKick = 3.0f;

        [Tooltip("Constant inward acceleration while touching a barrier, in road " +
                 "half-widths per second squared. Makes resting against the wall an " +
                 "unstable state that always decays back toward the track, so the boundary " +
                 "can never trap the car.")]
        [Range(0f, 3f)] public float barrierInwardBias = 0.9f;

        [Tooltip("Rate, rad/sec, at which a car pointing INTO a barrier is rotated back " +
                 "toward the road's heading while in contact. Without this the nose stays " +
                 "aimed at the wall and the car keeps driving into it.")]
        [Range(0f, 8f)] public float barrierYawRecovery = 2.5f;

        // ------------------------------------------------------------- Default curves
        private static AnimationCurve DefaultAccelCurve()
        {
            // Strong off the line, tapering toward top speed.
            AnimationCurve c = new AnimationCurve();
            c.AddKey(new Keyframe(0f, 1.00f));
            c.AddKey(new Keyframe(0.5f, 0.70f));
            c.AddKey(new Keyframe(1f, 0.25f));
            return c;
        }

        private static AnimationCurve DefaultSteeringCurve()
        {
            // 0 = fully low-speed authority, 1 = fully high-speed authority.
            AnimationCurve c = new AnimationCurve();
            c.AddKey(new Keyframe(0f, 0f));
            c.AddKey(new Keyframe(0.35f, 0.55f));
            c.AddKey(new Keyframe(1f, 1f));
            return c;
        }

        private void Reset()
        {
            accelerationBySpeed = DefaultAccelCurve();
            steeringBySpeed = DefaultSteeringCurve();
        }

        /// <summary>
        /// The curvature this profile can HOLD at a given fraction of top speed, at full
        /// lock. The one function every track difficulty question reduces to, and it is
        /// here rather than in the track builder so that the answer comes from the same
        /// numbers the car is actually driven by.
        /// </summary>
        public float HoldableCurve(float speedFraction)
        {
            float v = Mathf.Max(1f, maxSpeed * Mathf.Clamp01(speedFraction));
            float authority = Mathf.Lerp(lowSpeedSteering, highSpeedSteering,
                                         steeringBySpeed.Evaluate(Mathf.Clamp01(speedFraction)))
                            * steeringStrength;
            float gate = Mathf.Clamp01(Mathf.Clamp01(speedFraction) / Mathf.Max(0.01f, yawAuthoritySpeed));
            float yawRate = maxYawRate * authority * gate;
            return yawRate / (v * RenaultRacer.Track.RoadGeometry.RoadYawPerCurveUnit);
        }
    }
}
