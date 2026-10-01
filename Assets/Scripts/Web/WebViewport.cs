using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace RenaultRacer.Web
{
    /// <summary>
    /// The browser-side viewport controls Unity has no access to (plan section 14.2).
    ///
    /// Exactly one thing here is a request rather than a command: FULLSCREEN. The
    /// Fullscreen API must be invoked from inside a user gesture, and iPhone Safari does
    /// not implement it for anything that is not a &lt;video&gt; element - so on the
    /// single most important device for a campaign like this one, it will not happen.
    ///
    /// **Nothing may depend on it succeeding.** The canvas is sized from the visual
    /// viewport by the page (see the ShellRacer template's resize()), so the game already
    /// fills whatever space the browser leaves it, with or without a URL bar. Fullscreen
    /// buys extra pixels; it is never the thing that makes the layout correct. Building
    /// it the other way round - a layout that only works once the chrome is gone - is
    /// how a game ends up with its brake button behind Safari's toolbar.
    /// </summary>
    public static class WebViewport
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern int RRRequestFullscreen();
        [DllImport("__Internal")] private static extern int RRIsFullscreen();
#endif

        /// <summary>
        /// Ask the browser for fullscreen. Call it ONLY from the frame that handled a
        /// real tap or click - anywhere else the browser will refuse, and correctly so.
        /// Returns whether the request was even made; granting it is asynchronous and
        /// the browser may still decline.
        /// </summary>
        public static bool RequestFullscreen()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            bool asked = RRRequestFullscreen() != 0;
            Debug.Log("[WebViewport] fullscreen request " + (asked ? "made" : "unavailable"));
            return asked;
#else
            return false;
#endif
        }

        /// <summary>True while the browser reports the page is fullscreen.</summary>
        public static bool IsFullscreen
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return RRIsFullscreen() != 0;
#else
                return false;
#endif
            }
        }
    }
}
