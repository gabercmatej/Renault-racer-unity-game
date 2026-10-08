using System;
using UnityEngine;

namespace RenaultRacer.Vehicle
{
    /// <summary>
    /// Chooses which of the eleven perspective car views to draw (Phase 10).
    ///
    /// PRESENTATION ONLY. It reads input / simulation state and returns an index; nothing
    /// here is ever fed back into CarSimulation.
    ///
    /// Frame index is signed: 0 = straight, -1..-5 = L1..L5, +1..+5 = R1..R5.
    ///
    /// TWO MODES.
    ///
    /// <see cref="Mode.SteeringInput"/> (the default, the user's direction of 2026-09-17):
    /// the view follows the steering the player is PRESSING and how long it has been held.
    /// A tap shows L1/R1; holding walks through the views on a timer (0.2 s per view by
    /// default, L5/R5 from 0.8 s); letting go walks back through every view to straight
    /// (0.07 s per view: L5 -> straight in 0.35 s) and never leaves the car parked on an
    /// angled view with no input. Reversing walks down
    /// through straight before climbing the other side, so it never jumps L3 -> R3.
    ///
    /// <see cref="Mode.BodyHeading"/> (kept for comparison): the view follows the body's
    /// smoothed heading (Yaw plus a short YawRate lead) with a dead zone and hysteresis.
    /// Truer to a sliding car, but the model keeps its heading when steering is released,
    /// so the car can stay visibly angled with no input - which is exactly what the
    /// input mode exists to avoid.
    ///
    /// Neither mode reads road curvature.
    ///
    /// 2026-10-08 (visual only): <see cref="Settings.maxVisualIndex"/> = 3 caps the view at
    /// L3/R3, so L4/R4 and L5/R5 are never shown (Maj: too much rotation; 4 was the first
    /// step). The sprites and MaxIndex are untouched; set maxVisualIndex to 4 to restore the
    /// previous approved cap, or 5 to restore every view.
    /// </summary>
    public sealed class CarFrameSelector
    {
        public const int MaxIndex = 5;

        public enum Mode { SteeringInput = 0, BodyHeading = 1 }

        [Serializable]
        public sealed class Settings
        {
            public Mode mode = Mode.SteeringInput;

            [Tooltip("Outermost view shown, 1..MaxIndex. 3 = production since 2026-10-08 (L3/R3); " +
                     "4 = the previous approved cap (drops only L5/R5); " +
                     "5 = every view, the original animation exactly.")]
            [Range(1, MaxIndex)] public int maxVisualIndex = 3;

            [Header("Steering input mode")]
            [Tooltip("Seconds of holding a direction per view: L1/R1 for the first step, " +
                     "then one view further per step, up to L5/R5.")]
            [Range(0.05f, 0.5f)] public float secondsPerView = 0.2f;

            [Tooltip("Seconds per view when walking back toward straight after release " +
                     "(or through straight on a reversal). The return is animated one view " +
                     "at a time and starts on release: 0.07 gives L1->S 0.07 s, L3->S " +
                     "0.21 s, L5->S 0.35 s - visibly settling, never snapping, never stuck.")]
            [Range(0.01f, 0.2f)] public float returnSecondsPerView = 0.07f;

            [Tooltip("Steering axis magnitude that counts as pressed.")]
            [Range(0.01f, 0.9f)] public float inputThreshold = 0.1f;

            [Header("Body heading mode")]
            [Tooltip("Seconds of YawRate added to Yaw.")]
            [Range(0f, 1f)] public float yawRateLead = 0.10f;

            [Tooltip("How fast the visual heading follows the body, per second.")]
            [Range(1f, 40f)] public float smoothing = 12f;

            [Tooltip("Visual heading, degrees, at which L1/R1 .. L5/R5 are entered.")]
            public float[] enterDegrees = { 2.5f, 6f, 11f, 17f, 25f };

            [Range(0f, 5f)] public float hysteresisDegrees = 1.5f;

            [Range(0f, 0.3f)] public float minDwellSeconds = 0.08f;
        }

        private readonly Settings _s;
        private float _heading;        // radians, smoothed (heading mode)
        private int _index;
        private bool _primed;
        private float _dwell;
        private int _heldDir;          // direction currently held (input mode)
        private float _heldTime;
        private float _stepTimer;

        public CarFrameSelector(Settings settings)
        {
            _s = settings ?? new Settings();
        }

        public int Index { get { return _index; } }
        public float Heading { get { return _heading; } }
        public Settings Config { get { return _s; } }

        public void Reset()
        {
            _heading = 0f;
            _index = 0;
            _primed = false;
            _dwell = 0f;
            _heldDir = 0;
            _heldTime = 0f;
            _stepTimer = 0f;
        }

        /// <summary>Advance by one rendered frame. Allocation-free.</summary>
        /// <param name="steerInput">Steering axis the player is pressing, -1..1 (0 = none).</param>
        public int Update(float steerInput, float yaw, float yawRate, float dt)
        {
            dt = Mathf.Max(0f, dt);
            // The heading is tracked in both modes, so switching mode never starts cold.
            float target = yaw + yawRate * _s.yawRateLead;
            if (!_primed) { _heading = target; _primed = true; }
            else _heading += (target - _heading) * (1f - Mathf.Exp(-_s.smoothing * dt));

            if (_s.mode == Mode.SteeringInput) return UpdateInput(steerInput, dt);

            _dwell += dt;
            int next = Select(_index, _heading * Mathf.Rad2Deg);
            if (next != _index && _dwell >= _s.minDwellSeconds)
            {
                _index += next > _index ? 1 : -1;
                _dwell = 0f;
            }
            return _index;
        }

        private int UpdateInput(float steer, float dt)
        {
            int dir = steer > _s.inputThreshold ? 1 : (steer < -_s.inputThreshold ? -1 : 0);
            if (dir != _heldDir)
            {
                _heldDir = dir;
                _heldTime = 0f;
            }
            else if (dir != 0)
            {
                _heldTime += dt;
            }

            // Where the view wants to be. A fresh press shows the first view at once.
            int wanted = dir == 0 ? 0
                       : dir * Mathf.Min(Mathf.Clamp(_s.maxVisualIndex, 1, MaxIndex), 1 + Mathf.FloorToInt(_heldTime / Mathf.Max(0.01f, _s.secondsPerView)));

            if (wanted == _index)
            {
                _stepTimer = 0f;
                return _index;
            }

            // Moving AWAY from straight on the held side follows the hold timer directly
            // (the first view is immediate). Moving TOWARD straight - release, or the
            // first half of a reversal - walks one view per returnSecondsPerView.
            bool outward = _index == 0 ? wanted != 0
                         : (Math.Sign(wanted) == Math.Sign(_index) && Mathf.Abs(wanted) > Mathf.Abs(_index));
            if (outward)
            {
                _index += wanted > _index ? 1 : -1;
                _stepTimer = 0f;
                return _index;
            }

            _stepTimer += dt;
            float step = Mathf.Max(0.001f, _s.returnSecondsPerView);
            while (_stepTimer >= step && _index != wanted)
            {
                _stepTimer -= step;
                // Toward straight first; only climb the other side once there.
                if (_index != 0 && Math.Sign(wanted) != Math.Sign(_index)) _index -= Math.Sign(_index);
                else _index += wanted > _index ? 1 : -1;
                if (_index == 0 && wanted != 0) break;   // the other side resumes next frame
            }
            return _index;
        }

        /// <summary>Heading-mode threshold walk with hysteresis, exposed for the probe.</summary>
        public int Select(int current, float headingDegrees)
        {
            float[] t = _s.enterDegrees;
            int count = Mathf.Min(Mathf.Clamp(_s.maxVisualIndex, 1, MaxIndex), t != null ? t.Length : 0);
            if (count == 0) return 0;

            int side = current == 0 ? 0 : (current > 0 ? 1 : -1);
            int level = Mathf.Abs(current);
            if (side == 0) side = headingDegrees >= 0f ? 1 : -1;
            float v = headingDegrees * side;

            while (level > 0 && v < t[level - 1] - _s.hysteresisDegrees) level--;

            if (level == 0)
            {
                side = headingDegrees >= 0f ? 1 : -1;
                v = Mathf.Abs(headingDegrees);
            }

            while (level < count && v >= t[level]) level++;
            return level * side;
        }
    }
}
