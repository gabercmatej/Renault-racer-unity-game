using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using RenaultRacer.Core;
using RenaultRacer.Vehicle;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Ways to actually RUN <see cref="RaceProbe"/>: from the menu, and from the
    /// command line so a handling change can be checked against the RACE rules without
    /// opening the Editor.
    ///
    ///   Unity.exe -batchmode -quit -projectPath . \
    ///             -executeMethod RenaultRacer.EditorTools.RaceProbeMenu.RunFromCommandLine \
    ///             -logFile -
    ///
    /// It existed only as a library before, which meant the one suite that checks the
    /// lap clock, the checkpoint order and the anti-cheat validator against the CURRENT
    /// car could only be run by hand. A handling change moves lap times and corner
    /// speeds, so it is exactly the suite most likely to be quietly broken by one.
    /// </summary>
    public static class RaceProbeMenu
    {
        private const string ReportPath = "Logs/race-probe.txt";

        [MenuItem("Renault Racer/Race probe - shipping profile", false, 42)]
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
                Console.WriteLine("race probe threw: " + e);
                EditorApplication.Exit(2);
                return;
            }

            Console.WriteLine(report);
            try
            {
                Directory.CreateDirectory("Logs");
                File.WriteAllText(ReportPath, report);
                Console.WriteLine("[race probe] written to " + ReportPath);
            }
            catch (Exception e) { Console.WriteLine("could not write the report: " + e.Message); }

            int failures = 0;
            for (int i = report.IndexOf("**FAIL**", StringComparison.Ordinal); i >= 0;
                 i = report.IndexOf("**FAIL**", i + 1, StringComparison.Ordinal)) failures++;

            Console.WriteLine("[race probe] " + (failures == 0 ? "ALL PASS"
                                                              : failures + " FAILURE(S)"));
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        private static string BuildReport()
        {
            var profile = Load<HandlingProfile>("Assets/ScriptableObjects/Handling/Production.asset");
            var config = Load<GameConfig>("Assets/ScriptableObjects/Config/GameConfig.asset");
            if (profile == null) return "no Production HandlingProfile";
            if (config == null) return "no GameConfig";
            return RaceProbe.RunAll(profile, config);
        }

        private static T Load<T>(string path) where T : ScriptableObject
        {
            return AssetDatabase.LoadAssetAtPath<T>(path);
        }
    }
}
