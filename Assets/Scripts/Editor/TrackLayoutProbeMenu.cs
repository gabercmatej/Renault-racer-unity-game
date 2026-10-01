using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using RenaultRacer.Vehicle;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Ways to actually RUN <see cref="TrackLayoutProbe"/>: from the menu, and from
    /// the command line so a layout change can be checked without opening the Editor.
    ///
    ///   Unity.exe -batchmode -quit -projectPath . \
    ///             -executeMethod RenaultRacer.EditorTools.TrackLayoutProbeMenu.RunFromCommandLine \
    ///             -logFile -
    ///
    /// Same shape as RaceProbeMenu, and for the same reason: a suite nobody can run
    /// in one command is a suite that stops being run.
    /// </summary>
    public static class TrackLayoutProbeMenu
    {
        private const string ReportPath = "Logs/track-layout-probe.txt";

        [MenuItem("Renault Racer/Track layout probe - production circuit", false, 43)]
        public static void Run()
        {
            Debug.Log(BuildReport());
        }

        /// <summary>Batch entry point. Exits non-zero if anything failed.</summary>
        public static void RunFromCommandLine()
        {
            string report;
            try { report = BuildReport(); }
            catch (Exception e)
            {
                Console.WriteLine("track layout probe threw: " + e);
                EditorApplication.Exit(2);
                return;
            }

            Console.WriteLine(report);
            try
            {
                Directory.CreateDirectory("Logs");
                File.WriteAllText(ReportPath, report);
                Console.WriteLine("[track layout probe] written to " + ReportPath);
            }
            catch (Exception e) { Console.WriteLine("could not write the report: " + e.Message); }

            int failures = 0;
            for (int i = report.IndexOf("**FAIL**", StringComparison.Ordinal); i >= 0;
                 i = report.IndexOf("**FAIL**", i + 1, StringComparison.Ordinal)) failures++;

            Console.WriteLine("[track layout probe] " + (failures == 0 ? "ALL PASS"
                                                                      : failures + " FAILURE(S)"));
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        private static string BuildReport()
        {
            var profile = AssetDatabase.LoadAssetAtPath<HandlingProfile>(
                "Assets/ScriptableObjects/Handling/Production.asset");
            return TrackLayoutProbe.RunAll(profile);
        }
    }
}
