namespace RenaultRacer.InputSystem
{
    /// <summary>
    /// The entire input surface the simulation sees (plan section 9).
    /// CarSimulation consumes only this - it never learns whether a human used a
    /// thumb, a key or a gamepad, so controls can be retuned without touching physics.
    /// </summary>
    public interface IDriveInput
    {
        /// <summary>Raw steering demand, -1 (left) .. +1 (right).</summary>
        float SteerAxis { get; }

        /// <summary>True while the brake is held.</summary>
        bool BrakeHeld { get; }

        /// <summary>True while a deliberate drift is being provoked.</summary>
        bool HandbrakeHeld { get; }
    }
}
