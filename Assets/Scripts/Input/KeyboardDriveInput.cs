using UnityEngine;

namespace RenaultRacer.InputSystem
{
    /// <summary>
    /// Desktop input. Legacy Input API by design (plan section 9) - two keys and raw
    /// touch is the whole requirement, so the Input System package is dead weight.
    ///
    /// Output is deliberately raw and binary. All smoothing happens once, in
    /// CarSimulation, so keyboard and touch feel identical.
    /// </summary>
    public sealed class KeyboardDriveInput : IDriveInput
    {
        public float SteerAxis
        {
            get
            {
                float axis = 0f;
                if (UnityEngine.Input.GetKey(KeyCode.LeftArrow)  || UnityEngine.Input.GetKey(KeyCode.A)) axis -= 1f;
                if (UnityEngine.Input.GetKey(KeyCode.RightArrow) || UnityEngine.Input.GetKey(KeyCode.D)) axis += 1f;
                return axis;
            }
        }

        public bool BrakeHeld
        {
            get
            {
                return UnityEngine.Input.GetKey(KeyCode.DownArrow)
                    || UnityEngine.Input.GetKey(KeyCode.S)
                    || UnityEngine.Input.GetKey(KeyCode.Space);
            }
        }

        public bool HandbrakeHeld
        {
            get
            {
                return UnityEngine.Input.GetKey(KeyCode.LeftShift)
                    || UnityEngine.Input.GetKey(KeyCode.RightShift);
            }
        }
    }
}
