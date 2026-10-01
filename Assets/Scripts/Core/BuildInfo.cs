using UnityEngine;

namespace RenaultRacer.Core
{
    /// <summary>
    /// WHICH BUILD IS THIS, exactly.
    ///
    /// The bug this exists to make impossible: a phone on the LAN was running a build
    /// produced three phases earlier, and there was no way to tell from the screen. The
    /// evidence had to be reconstructed from where the brake button happened to be. A
    /// build that cannot identify itself turns every device test into an argument about
    /// whether the fix is missing or the build is.
    ///
    /// The fields are BAKED AT BUILD TIME by <c>BuildStamp</c>, which runs against the
    /// copy of the scene being built and writes the Git SHA, the timestamp and the kind
    /// of build into them. In the Editor they are empty, so <see cref="Summary"/> asks
    /// Git directly - which means the Editor HUD and the phone HUD print the same kind
    /// of string and can be compared at a glance.
    ///
    /// Player-facing UI never shows this. It reaches the screen only through
    /// <c>DebugHud</c>, and <c>ProductionStrip</c> deletes that from a production build.
    /// The one thing production keeps is the single console line in Awake, because a
    /// support question about the live site is answered by a browser console, not by a
    /// pixel.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class BuildInfo : MonoBehaviour
    {
        [Tooltip("Baked by BuildStamp. Short Git SHA of HEAD, plus -dirty if the working " +
                 "tree had uncommitted changes when the build was made.")]
        [SerializeField] private string commit = "";

        [Tooltip("Baked by BuildStamp. Local time the build started, yyyy-MM-dd HH:mm.")]
        [SerializeField] private string builtAt = "";

        [Tooltip("Baked by BuildStamp. LAN GZIP or PRODUCTION.")]
        [SerializeField] private string kind = "";

        /// <summary>Short Git SHA the build was made from, or "?" if unknown.</summary>
        public static string Commit { get; private set; }

        /// <summary>Local build time, yyyy-MM-dd HH:mm.</summary>
        public static string BuiltAt { get; private set; }

        /// <summary>LAN GZIP, PRODUCTION, or EDITOR.</summary>
        public static string Kind { get; private set; }

        /// <summary>One line: <c>BUILD fbfe117 · LAN GZIP · 2026-09-17 10:30</c>.</summary>
        public static string Summary
        {
            get
            {
                if (Commit == null) Resolve(null, null, null);
                return "BUILD " + Commit + " \u00b7 " + Kind + " \u00b7 " + BuiltAt;
            }
        }

        private void Awake()
        {
            Resolve(commit, builtAt, kind);
            Debug.Log("[BuildInfo] " + Summary);
        }

        private static void Resolve(string c, string t, string k)
        {
            bool baked = !string.IsNullOrEmpty(c);
            Commit  = baked ? c : EditorCommit();
            BuiltAt = !string.IsNullOrEmpty(t) ? t : "editor";
            Kind    = !string.IsNullOrEmpty(k) ? k : "EDITOR";
        }

        /// <summary>
        /// In the Editor there is no baked stamp, so ask Git. Editor-only on purpose:
        /// a player has no repository and no shell, and spawning a process from a Web
        /// build is not a thing that exists.
        /// </summary>
        private static string EditorCommit()
        {
#if UNITY_EDITOR
            return GitShortSha();
#else
            return "?";
#endif
        }

#if UNITY_EDITOR
        /// <summary>
        /// Short SHA of HEAD, with "-dirty" appended when the working tree differs from
        /// it. The suffix is not decoration: "builds from current HEAD" is a promise the
        /// build cannot keep while there are uncommitted changes, and pretending
        /// otherwise is exactly the class of confusion this file exists to end.
        /// </summary>
        public static string GitShortSha()
        {
            string sha = Git("rev-parse --short HEAD");
            if (string.IsNullOrEmpty(sha)) return "no-git";
            return string.IsNullOrEmpty(Git("status --porcelain")) ? sha : sha + "-dirty";
        }

        private static string Git(string args)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git", args);
                psi.WorkingDirectory = System.IO.Directory.GetCurrentDirectory();
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(4000);
                    return output.Trim();
                }
            }
            catch { return ""; }
        }
#endif
    }
}
