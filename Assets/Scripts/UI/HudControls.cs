using UnityEngine;

namespace RenaultRacer.UI
{
    /// <summary>
    /// The one place that answers "is this screen point on an interactive HUD control?".
    /// Raw touch and mouse readers (TouchDriveInput.Classify, GameDirector.TapStarted) ask
    /// it, so a press that starts on the fullscreen button, the sound button, the pause button or the pause
    /// overlay's NADALJUJ button belongs to that control for its whole life and can never
    /// steer, brake, start or restart. A new control is added here once.
    /// </summary>
    public static class HudControls
    {
        /// <summary>
        /// Half the clear space between two stacked buttons, in screen px, minus a hair:
        /// the shared vertical edges of the column grow by this much and never touch.
        /// </summary>
        public static float HalfGapPx(float canvasScale)
        {
            return Mathf.Max(0f, PauseView.Gap * 0.5f * canvasScale - 0.01f);
        }

        public static bool IsOverInteractive(Vector2 screenPoint)
        {
            return FullscreenView.IsOverButton(screenPoint) || SoundToggleView.IsOverButton(screenPoint) || PauseView.IsOverControl(screenPoint);
        }
    }
}
