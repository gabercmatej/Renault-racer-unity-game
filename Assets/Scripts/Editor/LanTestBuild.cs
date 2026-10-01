using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Produces a Web build that can actually be loaded by a phone over plain HTTP on
    /// the local network. Testing infrastructure only - it never changes what ships.
    ///
    /// WHY THIS EXISTS
    ///
    /// Production is Brotli with Decompression Fallback OFF, which is the right call:
    /// it is the smallest payload and the live site will serve it over HTTPS with a
    /// Content-Encoding header (plan 17, 21.4). But a browser only advertises `br` on a
    /// TRUSTWORTHY origin. Measured, in Chrome:
    ///
    ///     http://127.0.0.1:8130      Accept-Encoding: gzip, deflate, br, zstd
    ///     http://192.168.64.107:8130 Accept-Encoding: gzip, deflate
    ///
    /// localhost is trustworthy, a bare LAN IP over http is not. So the production
    /// build loads perfectly on the dev machine and dies on the phone with
    /// ERR_CONTENT_DECODING_FAILED - the server hands over Brotli that the browser
    /// never agreed to decode. iOS Safari over plain http does the same thing.
    ///
    /// Gzip has no such restriction: every browser advertises it on every origin. So
    /// the device-test build differs from production in exactly ONE axis, compression,
    /// and is otherwise the same scenes, the same template and the same settings - which
    /// is what keeps the device test representative of what will ship.
    ///
    /// The player settings are restored in a finally block, so a failed or cancelled
    /// build cannot leave the project on Gzip. Verify with the menu item below.
    ///
    /// ONE OUTPUT FOLDER, ALWAYS THE CURRENT ONE
    ///
    /// This used to build to Builds/WebPhase6LAN, and later sessions kept passing
    /// different paths in by hand while the menu item and serve-build.py's default both
    /// still pointed at the Phase 6 folder. A device test run with no arguments
    /// therefore served a build from before the brake moved and before the minimap
    /// existed, and the only evidence was the layout on the screen. There is now exactly
    /// one LAN folder, it is named after nothing that can go out of date, and it is
    /// DELETED before every build so nothing from a previous one can survive inside it.
    /// </summary>
    public static class LanTestBuild
    {
        /// <summary>
        /// The one and only LAN device-test output. Never version this name: a folder
        /// called WebPhase6LAN is how a Phase 9 game came to be tested as a Phase 6 one.
        /// </summary>
        public const string LanOutput = "Builds/WebCurrentLAN";
        private const string Scene = "Assets/Scenes/Main.unity";

        [MenuItem("Renault Racer/Build Web — LAN device test (Gzip)", false, 20)]
        public static void BuildForLan()
        {
            string path = BuildLan(LanOutput);
            if (!string.IsNullOrEmpty(path))
            {
                Debug.Log("[LanTestBuild] " + BuildStamp.LastStamp + "\n"
                          + "Serve it with:\n"
                          + "    python serve-build.py\n"
                          + "(no arguments - it defaults to " + LanOutput + " on :8130)\n"
                          + "then open the printed LAN URL on the phone. The SHA in the debug "
                          + "HUD must match `git rev-parse --short HEAD`.");
            }
        }

        /// <summary>Batch entry point, so a LAN build can be made without the Editor.</summary>
        public static void RunFromCommandLine()
        {
            string path = null;
            try { path = BuildLan(LanOutput); }
            catch (Exception e) { Console.WriteLine("[LanTestBuild] threw: " + e); }
            Console.WriteLine(string.IsNullOrEmpty(path)
                ? "[LanTestBuild] BUILD FAILED"
                : "[LanTestBuild] " + BuildStamp.LastStamp + "  ->  " + path);
            EditorApplication.Exit(string.IsNullOrEmpty(path) ? 1 : 0);
        }

        [MenuItem("Renault Racer/Verify Web settings are PRODUCTION (Brotli)", false, 21)]
        public static void VerifyProductionSettings()
        {
            Debug.Log("[LanTestBuild] " + DescribeSettings()
                      + (IsProduction()
                         ? "   OK - production settings"
                         : "   *** NOT production - expected Brotli + fallback OFF ***"));
        }

        public static bool IsProduction()
        {
            return PlayerSettings.WebGL.compressionFormat == WebGLCompressionFormat.Brotli
                && PlayerSettings.WebGL.decompressionFallback == false;
        }

        public static string DescribeSettings()
        {
            return "compression=" + PlayerSettings.WebGL.compressionFormat
                 + " decompressionFallback=" + PlayerSettings.WebGL.decompressionFallback
                 + " template=" + PlayerSettings.WebGL.template;
        }

        /// <summary>
        /// Build to <paramref name="outputPath"/> with Gzip, then put every setting back
        /// exactly as it was. Returns the output path, or null if the build failed.
        /// </summary>
        public static string BuildLan(string outputPath)
        {
            // Identity FIRST, before a single setting changes - see BuildStamp.Commit.
            BuildStamp.Begin("LAN GZIP");

            WebGLCompressionFormat originalFormat = PlayerSettings.WebGL.compressionFormat;
            bool originalFallback = PlayerSettings.WebGL.decompressionFallback;

            Debug.Log("[LanTestBuild] before: " + DescribeSettings());

            string result = null;
            try
            {
                // Nothing from a previous build may survive into this one. Unity
                // overwrites the files it produces and leaves every other file exactly
                // where it was, so a folder is not a build - it is the union of every
                // build ever made into it.
                BuildStamp.CleanOutput(outputPath);

                // Gzip, and fallback stays OFF: serve-build.py sends the correct
                // Content-Encoding: gzip, and every browser advertises gzip everywhere,
                // so the JS decompressor would be dead weight even in a test build.
                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
                PlayerSettings.WebGL.decompressionFallback = false;

                // Keep the debug HUD and the profile switcher. This is the build that
                // gets driven on a real phone, and the HUD is the instrument - a device
                // test that cannot read the car's state is a test of the graphics only.
                // It is NOT a development build: those are slower and larger, which
                // would make the performance reading meaningless (plan 9.6b).
                ProductionStrip.KeepDevToolsForThisBuild = true;

                var options = new BuildPlayerOptions
                {
                    scenes = new[] { Scene },
                    locationPathName = outputPath,
                    target = BuildTarget.WebGL,
                    targetGroup = BuildTargetGroup.WebGL,
                    // Never None: see BuildStamp.Options for what the cache did.
                    options = BuildStamp.Options
                };

                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;

                Debug.Log("[LanTestBuild] build " + summary.result
                          + "   " + (summary.totalSize / 1024f / 1024f).ToString("F2") + " MB"
                          + "   " + summary.totalTime.TotalSeconds.ToString("F0") + " s"
                          + "   errors " + summary.totalErrors
                          + "   warnings " + summary.totalWarnings);

                if (summary.result == BuildResult.Succeeded)
                {
                    BuildStamp.AssertStamped();
                    result = outputPath;
                    BuildStamp.WriteSidecar(outputPath, "LAN GZIP");
                    Debug.Log("[LanTestBuild] " + BuildStamp.LastStamp);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[LanTestBuild] build threw: " + e.Message);
            }
            finally
            {
                // Unconditional. A thrown, failed or cancelled build must never leave the
                // project on the test compression - the next production build would
                // silently ship Gzip.
                ProductionStrip.KeepDevToolsForThisBuild = false;
                BuildStamp.End();
                PlayerSettings.WebGL.compressionFormat = originalFormat;
                PlayerSettings.WebGL.decompressionFallback = originalFallback;
                AssetDatabase.SaveAssets();

                Debug.Log("[LanTestBuild] restored: " + DescribeSettings()
                          + (IsProduction() ? "   OK" : "   *** RESTORE FAILED ***"));
            }

            return result;
        }
    }
}
