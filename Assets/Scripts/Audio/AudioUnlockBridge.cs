using System.Runtime.InteropServices;

namespace RenaultRacer.Audio
{
    /// <summary>State of the browser's AudioContext, as the .jslib reports it.</summary>
    public enum AudioCtxState
    {
        None = 0,         // no context (audio unsupported/disabled)
        Suspended = 1,    // created, waiting for a user gesture
        Running = 2,
        Interrupted = 3,  // iOS: call, Siri, tab switch
        Closed = 4,
        Stalled = 5       // says running but its clock is not advancing
    }

    /// <summary>What the player should be told about sound.</summary>
    public enum SoundStatus { Muted, WaitingForTap, On, Blocked }

    /// <summary>
    /// Thin bridge to Assets/Plugins/WebGL/AudioUnlock.jslib. The jslib owns the DOM
    /// listeners that actually resume the context (C# runs in Unity's frame loop, not in
    /// the DOM event, so it cannot). Outside a WebGL player there is no browser: the
    /// state is whatever <see cref="Simulated"/> says, default Running, so the Editor
    /// and the probes can drive the whole state machine.
    /// </summary>
    public static class AudioUnlockBridge
    {
        /// <summary>Editor/standalone stand-ins for the browser values.</summary>
        public static AudioCtxState Simulated = AudioCtxState.Running;
        public static int SimulatedGesturesSinceLoss = 0;
        public static int SimulatedMsSinceGesture = 1000000000;
        public static bool SimulatedHidden = false;
        public static int SimulatedHideCount = 0;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void RRAudioInit();
        [DllImport("__Internal")] private static extern int RRAudioState();
        [DllImport("__Internal")] private static extern int RRAudioGesturesSinceLoss();
        [DllImport("__Internal")] private static extern int RRAudioMsSinceGesture();
        [DllImport("__Internal")] private static extern int RRAudioGestureCount();
        [DllImport("__Internal")] private static extern int RRAudioSessionMode();
        [DllImport("__Internal")] private static extern int RRAudioResumeErrors();
        [DllImport("__Internal")] private static extern int RRAudioPageHidden();
        [DllImport("__Internal")] private static extern int RRPageHideCount();
        [DllImport("__Internal")] private static extern void RRAudioResume();

        public static void Init() { try { RRAudioInit(); } catch (System.Exception) { } }
        public static AudioCtxState State { get { try { return (AudioCtxState)RRAudioState(); } catch (System.Exception) { return AudioCtxState.None; } } }
        public static int GesturesSinceLoss { get { try { return RRAudioGesturesSinceLoss(); } catch (System.Exception) { return 0; } } }
        public static int MsSinceGesture { get { try { return RRAudioMsSinceGesture(); } catch (System.Exception) { return 1000000000; } } }
        public static int GestureCount { get { try { return RRAudioGestureCount(); } catch (System.Exception) { return 0; } } }
        /// <summary>0 unsupported, 1 'playback' set, 2 failed.</summary>
        public static int SessionMode { get { try { return RRAudioSessionMode(); } catch (System.Exception) { return 0; } } }
        public static int ResumeErrors { get { try { return RRAudioResumeErrors(); } catch (System.Exception) { return 0; } } }
        public static bool PageHidden { get { try { return RRAudioPageHidden() != 0; } catch (System.Exception) { return false; } } }
        /// <summary>Times the page has gone hidden. GameDirector pauses when it moves.</summary>
        public static int PageHideCount { get { try { return RRPageHideCount(); } catch (System.Exception) { return 0; } } }
        public static void Resume() { try { RRAudioResume(); } catch (System.Exception) { } }
        public static bool IsBrowser { get { return true; } }
#else
        public static void Init() { }
        public static AudioCtxState State { get { return Simulated; } }
        public static int GesturesSinceLoss { get { return SimulatedGesturesSinceLoss; } }
        public static int MsSinceGesture { get { return SimulatedMsSinceGesture; } }
        public static int GestureCount { get { return SimulatedGesturesSinceLoss; } }
        public static int SessionMode { get { return 0; } }
        public static int ResumeErrors { get { return 0; } }
        public static bool PageHidden { get { return SimulatedHidden; } }
        public static int PageHideCount { get { return SimulatedHideCount; } }
        public static void Resume() { }
        public static bool IsBrowser { get { return false; } }
#endif

        /// <summary>A gesture this recent still counts as "the browser is deciding".</summary>
        public const int GraceMs = 1500;

        /// <summary>
        /// The state machine behind the sound button. Pure, so the probe drives the same
        /// code the game does. Status is based on the REAL context state, never on an
        /// AudioSource's isPlaying.
        ///
        /// Muted wins. Running is On. Anything else is WaitingForTap until at least one
        /// activating gesture has happened since the context stopped running AND it has
        /// had <see cref="GraceMs"/> to take effect; after that it is Blocked.
        /// </summary>
        public static SoundStatus Evaluate(bool muted, AudioCtxState ctx, int gesturesSinceLoss, int msSinceGesture)
        {
            if (muted) return SoundStatus.Muted;
            if (ctx == AudioCtxState.Running) return SoundStatus.On;
            if (gesturesSinceLoss > 0 && msSinceGesture >= GraceMs) return SoundStatus.Blocked;
            return SoundStatus.WaitingForTap;
        }
    }
}
