namespace RenaultRacer.Core
{
    /// <summary>
    /// Fixed-step accumulator (plan section 8.11).
    ///
    /// The simulation runs on this, NOT on Update's delta and NOT on FixedUpdate -
    /// we decouple from FixedUpdate entirely because Physics2D is disabled. The result
    /// is driving behaviour that is identical at 30 and 60 FPS, which is mandatory
    /// given the weak device tier renders at 30.
    /// </summary>
    public sealed class FixedStepClock
    {
        private readonly float _step;
        private readonly int _maxSteps;
        private float _accumulator;

        /// <param name="step">Fixed timestep, seconds.</param>
        /// <param name="maxSteps">Spiral-of-death guard: substeps allowed per frame.</param>
        public FixedStepClock(float step, int maxSteps)
        {
            _step = step > 0f ? step : 1f / 60f;
            _maxSteps = maxSteps > 0 ? maxSteps : 4;
        }

        public float Step { get { return _step; } }

        /// <summary>
        /// Fraction of a step left over, 0..1. Use ONLY to interpolate the view -
        /// never to advance simulation state.
        /// </summary>
        public float Alpha { get { return _accumulator / _step; } }

        /// <summary>
        /// Feed a frame delta and receive how many fixed steps to run.
        /// A long stall (tab backgrounded, GC spike) is clamped rather than replayed.
        /// </summary>
        public int Advance(float deltaTime)
        {
            if (deltaTime > 0.25f) deltaTime = 0.25f;
            _accumulator += deltaTime;

            int steps = 0;
            while (_accumulator >= _step && steps < _maxSteps)
            {
                _accumulator -= _step;
                steps++;
            }

            // Hit the guard: drop the backlog instead of spiralling.
            if (_accumulator > _step * _maxSteps) _accumulator = 0f;
            return steps;
        }

        public void Reset()
        {
            _accumulator = 0f;
        }
    }
}
