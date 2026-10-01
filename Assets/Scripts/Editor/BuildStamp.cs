using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
using RenaultRacer.Core;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Bakes WHICH COMMIT this build came from into the build, so a phone can prove what
    /// it is running (plan 8.21).
    ///
    /// The bug it closes: a LAN device test was run against `Builds/WebPhase6LAN`, a
    /// build made three phases and four commits earlier, and the only way to tell was
    /// that the brake was on the wrong side. Every minute spent on that was spent
    /// debugging code that had already been fixed.
    ///
    /// Same mechanism as <see cref="ProductionStrip"/> and for the same reason: an
    /// <see cref="IProcessSceneWithReport"/> callback writes into the COPY of the scene
    /// being built, never the saved asset, so a build can never dirty the project and a
    /// thrown build can never leave a stale stamp behind. Nothing is generated into
    /// Assets/, so no build triggers a recompile of anything.
    ///
    /// It runs AFTER ProductionStrip (higher callbackOrder), which only removes things.
    /// </summary>
    public sealed class BuildStamp : IProcessSceneWithReport
    {
        /// <summary>
        /// What kind of build is being made - "LAN GZIP" or "PRODUCTION". Set by the
        /// build entry points around the call to BuildPipeline, and cleared in their
        /// finally blocks. Empty means someone hit Ctrl+B, which is stamped as such
        /// rather than guessed at.
        /// </summary>
        public static string Kind;

        /// <summary>
        /// The commit identity, captured by the build entry point BEFORE it touches any
        /// setting. Reading Git from inside the build is wrong: the LAN build switches
        /// compression to Gzip and Unity writes that into ProjectSettings.asset for the
        /// duration of the build, so a clean checkout reads as "-dirty" at exactly the
        /// moment the stamp is taken, and is clean again by the time anyone looks.
        /// Null means an ad-hoc build, which falls back to asking Git live.
        /// </summary>
        public static string Commit;

        /// <summary>The stamp the last build baked, for the report the build prints.</summary>
        public static string LastStamp = "";

        public int callbackOrder { get { return 10; } }

        /// <summary>
        /// Call first thing in a build entry point, before any PlayerSettings change.
        /// </summary>
        public static void Begin(string kind)
        {
            Kind = kind;
            Commit = BuildInfo.GitShortSha();
            LastStamp = "";
        }

        /// <summary>
        /// The options every stamped build must use. CleanBuildCache is NOT optional.
        ///
        /// Measured: without it, a LAN build made straight after another one finished in
        /// two seconds, never called OnProcessScene, and shipped the PREVIOUS build's
        /// scene data from Library/Bee - so the HUD said `b4564e3-dirty 11:35` while the
        /// sidecar beside it said `6d4cc1b`. Deleting the output folder does not help;
        /// the stale bytes live in the build cache, not the folder. A build that can lie
        /// about what it is costs more than the two minutes a clean one takes.
        /// </summary>
        public const BuildOptions Options = BuildOptions.CleanBuildCache;

        /// <summary>
        /// Throws if the build did not actually stamp itself with the identity captured
        /// in Begin. The only acceptable outcome for an unstamped build is a failed one.
        /// </summary>
        public static void AssertStamped()
        {
            if (string.IsNullOrEmpty(LastStamp)
                || (!string.IsNullOrEmpty(Commit) && LastStamp.IndexOf(Commit, StringComparison.Ordinal) < 0))
            {
                throw new BuildFailedException(
                    "[BuildStamp] the build did not stamp itself with " + Commit
                    + " (last stamp: '" + LastStamp + "'). Its scene data may be a cached "
                    + "copy of an older build. Refusing to call it a success.");
            }
        }

        /// <summary>Call in the entry point's finally block.</summary>
        public static void End()
        {
            Kind = null;
            Commit = null;
        }

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null) return;   // Play Mode - not a build, leave it alone.

            string commit = !string.IsNullOrEmpty(Commit) ? Commit : BuildInfo.GitShortSha();
            string builtAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            string kind = string.IsNullOrEmpty(Kind) ? "AD-HOC" : Kind;

            BuildInfo info = null;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int r = 0; r < roots.Length && info == null; r++)
                info = roots[r].GetComponentInChildren<BuildInfo>(true);

            if (info == null)
            {
                // A build that cannot identify itself is the thing this file exists to
                // prevent, so add the component rather than shrug. It lands on the scene
                // copy only.
                GameObject host = roots.Length > 0 ? roots[0] : new GameObject("[Systems]");
                info = host.AddComponent<BuildInfo>();
            }

            var so = new SerializedObject(info);
            so.FindProperty("commit").stringValue = commit;
            so.FindProperty("builtAt").stringValue = builtAt;
            so.FindProperty("kind").stringValue = kind;
            so.ApplyModifiedPropertiesWithoutUndo();

            LastStamp = "BUILD " + commit + " \u00b7 " + kind + " \u00b7 " + builtAt;
            Debug.Log("[BuildStamp] " + LastStamp);

            if (commit.EndsWith("-dirty", StringComparison.Ordinal))
                Debug.LogWarning("[BuildStamp] the working tree has uncommitted changes, so "
                                 + "this build is NOT reproducible from " + commit
                                 + ". The stamp says -dirty for exactly that reason.");
        }

        /// <summary>
        /// Write the same identity NEXT TO the build - not inside it - so tooling can
        /// read it without parsing a wasm. `serve-build.py` uses this to refuse to serve
        /// a build that does not match the checked-out commit, which is the other half
        /// of never testing a stale build again.
        /// </summary>
        public static void WriteSidecar(string outputPath, string kind)
        {
            try
            {
                string commit = !string.IsNullOrEmpty(Commit) ? Commit : BuildInfo.GitShortSha();
                string json = "{\n"
                            + "  \"commit\": \"" + commit + "\",\n"
                            + "  \"builtAt\": \"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\",\n"
                            + "  \"kind\": \"" + kind + "\",\n"
                            + "  \"unity\": \"" + Application.unityVersion + "\"\n"
                            + "}\n";
                File.WriteAllText(Path.Combine(outputPath, "build-identity.json"), json);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BuildStamp] could not write build-identity.json: " + e.Message);
            }
        }

        /// <summary>
        /// Delete the output folder before building into it.
        ///
        /// Unity does NOT clear a Web output directory: it overwrites the files it
        /// produces this time and leaves everything else exactly where it was. A build
        /// whose compression changed, or whose template dropped a file, therefore leaves
        /// the old one sitting next to the new one - and the browser is perfectly happy
        /// to load it. Stale bytes surviving a rebuild is the same failure as testing a
        /// stale folder, one level down.
        /// </summary>
        public static void CleanOutput(string outputPath)
        {
            try
            {
                if (Directory.Exists(outputPath))
                {
                    Directory.Delete(outputPath, true);
                    Debug.Log("[BuildStamp] cleaned " + outputPath);
                }
                Directory.CreateDirectory(outputPath);
            }
            catch (Exception e)
            {
                throw new BuildFailedException(
                    "[BuildStamp] could not clean " + outputPath + " - refusing to build "
                    + "into a directory that may still hold an older build. " + e.Message);
            }
        }
    }
}
