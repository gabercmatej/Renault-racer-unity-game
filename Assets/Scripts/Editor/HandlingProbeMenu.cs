using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using RenaultRacer.Track;
using RenaultRacer.Vehicle;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Ways to actually RUN <see cref="HandlingProbe"/>: from the menu while tuning,
    /// and from the command line so a handling change can be checked without opening
    /// the Editor at all.
    ///
    ///   Unity.exe -batchmode -quit -projectPath . \
    ///             -executeMethod RenaultRacer.EditorTools.HandlingProbeMenu.RunFromCommandLine \
    ///             -logFile -
    ///
    /// The report goes to Logs/handling-probe.txt as well as the console, because
    /// batch-mode stdout interleaves with everything else Unity has to say.
    /// </summary>
    public static class HandlingProbeMenu
    {
        private const string ProfileFolder = "Assets/ScriptableObjects/Handling";
        private const string ShippingProfile = "Production";
        private const string ReportPath = "Logs/handling-probe.txt";

        [MenuItem("Renault Racer/Handling probe - shipping profile", false, 40)]
        public static void RunShipping()
        {
            Debug.Log(BuildReport(true));
        }

        [MenuItem("Renault Racer/Handling probe - every profile", false, 41)]
        public static void RunEverything()
        {
            Debug.Log(BuildReport(false));
        }

        /// <summary>Batch entry point. Exits non-zero if anything failed.</summary>
        public static void RunFromCommandLine()
        {
            string report;
            bool clean;
            try
            {
                report = BuildReport(false);
                // Summary lines only. Every test group ends in "<CLAIM> = PASS/FAIL",
                // and the dev reference profiles legitimately miss individual PRODUCTION
                // targets - that is what "[invariants only - dev reference]" means - so
                // counting every sub-item would make this permanently red.
                //
                // And the exit code is decided by the SHIPPING profile alone. The dev
                // references are A/B comparison artifacts that are stripped from every
                // production build (ProductionStrip); plan 8.15.6 already recorded three
                // of them failing invariants and deliberately did not retune them,
                // because retuning a dead asset buys nothing. Letting them hold the
                // build red would mean the one number that matters - does the car that
                // ships still pass - stops being readable. They are still printed, and
                // still summarised below, so a real regression in one is visible.
                clean = ShippingSection(report).IndexOf("= **FAIL**", StringComparison.Ordinal) < 0;
            }
            catch (Exception e)
            {
                Console.WriteLine("handling probe threw: " + e);
                EditorApplication.Exit(2);
                return;
            }

            Console.WriteLine(report);
            try
            {
                Directory.CreateDirectory("Logs");
                File.WriteAllText(ReportPath, report);
                Console.WriteLine("[handling probe] written to " + ReportPath);
            }
            catch (Exception e) { Console.WriteLine("could not write the report: " + e.Message); }

            int devFailures = 0;
            for (int i = report.IndexOf("= **FAIL**", StringComparison.Ordinal); i >= 0;
                 i = report.IndexOf("= **FAIL**", i + 1, StringComparison.Ordinal)) devFailures++;
            int shipFailures = 0;
            string ship = ShippingSection(report);
            for (int i = ship.IndexOf("= **FAIL**", StringComparison.Ordinal); i >= 0;
                 i = ship.IndexOf("= **FAIL**", i + 1, StringComparison.Ordinal)) shipFailures++;

            Console.WriteLine("[handling probe] " + ShippingProfile + ": "
                              + (shipFailures == 0 ? "ALL PASS" : shipFailures + " FAILURE(S)")
                              + "   |   dev references: " + (devFailures - shipFailures)
                              + " failure(s), not gating");
            Console.WriteLine(clean ? "[handling probe] ALL PASS" : "[handling probe] FAILURES ABOVE");
            EditorApplication.Exit(clean ? 0 : 1);
        }

        /// <summary>
        /// The part of the report that belongs to the shipping profile: its verification
        /// block plus the lap report, which is written for it and no one else. Everything
        /// after the next profile header is a dev reference.
        /// </summary>
        private static string ShippingSection(string report)
        {
            const string Header = "profile: ";
            int start = report.IndexOf(Header + ShippingProfile, StringComparison.Ordinal);
            if (start < 0) return report;
            int next = report.IndexOf("\n" + Header, start + 1, StringComparison.Ordinal);
            // The lap report sits between the shipping block and the first dev reference,
            // which is exactly where we want the cut.
            return next < 0 ? report.Substring(start) : report.Substring(start, next - start);
        }

        private static string BuildReport(bool shippingOnly)
        {
            StringBuilder sb = new StringBuilder(32768);

            HandlingProfile shipping = Load(ShippingProfile);
            if (shipping == null) return "no " + ShippingProfile + " profile under " + ProfileFolder;

            sb.AppendLine(HandlingProbe.RunAll(shipping, true));
            sb.AppendLine(HandlingProbe.LapReport(shipping, TrackBuilder.BuildMainTrack()));

            if (!shippingOnly)
            {
                string[] guids = AssetDatabase.FindAssets("t:HandlingProfile", new[] { ProfileFolder });
                for (int i = 0; i < guids.Length; i++)
                {
                    var p = AssetDatabase.LoadAssetAtPath<HandlingProfile>(
                        AssetDatabase.GUIDToAssetPath(guids[i]));
                    // The dev references are personality experiments, not shipping
                    // candidates: they only have to hold the invariants.
                    if (p == null || p.name == ShippingProfile) continue;
                    sb.AppendLine(HandlingProbe.RunAll(p, false));
                }
            }
            return sb.ToString();
        }

        private static HandlingProfile Load(string profileName)
        {
            string[] guids = AssetDatabase.FindAssets("t:HandlingProfile " + profileName,
                                                      new[] { ProfileFolder });
            for (int i = 0; i < guids.Length; i++)
            {
                var p = AssetDatabase.LoadAssetAtPath<HandlingProfile>(
                    AssetDatabase.GUIDToAssetPath(guids[i]));
                if (p != null && p.name == profileName) return p;
            }
            return null;
        }
    }
}
