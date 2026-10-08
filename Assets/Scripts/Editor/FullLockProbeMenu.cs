using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using RenaultRacer.Track;
using RenaultRacer.Vehicle;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Ways to run <see cref="FullLockProbe"/>: from the menu, and from the command line.
    ///
    ///   Unity.exe -batchmode -quit -projectPath . \
    ///             -executeMethod RenaultRacer.EditorTools.FullLockProbeMenu.RunFromCommandLine \
    ///             -logFile -
    /// </summary>
    public static class FullLockProbeMenu
    {
        private const string ProfileFolder = "Assets/ScriptableObjects/Handling";
        private const string ShippingProfile = "Production";
        private const string ReportPath = "Logs/full-lock-probe.txt";

        [MenuItem("Renault Racer/Full-lock probe - holding one button for ever", false, 43)]
        public static void Run()
        {
            Debug.Log(BuildReport());
        }

        public static void RunFromCommandLine()
        {
            string report;
            try
            {
                report = BuildReport();
            }
            catch (Exception e)
            {
                Console.WriteLine("full-lock probe threw: " + e);
                EditorApplication.Exit(2);
                return;
            }

            Console.WriteLine(report);
            try
            {
                Directory.CreateDirectory("Logs");
                File.WriteAllText(ReportPath, report);
                Console.WriteLine("[full-lock probe] written to " + ReportPath);
            }
            catch (Exception e) { Console.WriteLine("could not write the report: " + e.Message); }

            bool clean = report.IndexOf("**FAIL**", StringComparison.Ordinal) < 0;
            Console.WriteLine(clean ? "[full-lock probe] ALL PASS" : "[full-lock probe] FAILURES ABOVE");
            EditorApplication.Exit(clean ? 0 : 1);
        }

        private static string BuildReport()
        {
            HandlingProfile shipping = Load(ShippingProfile);
            if (shipping == null) return "no " + ShippingProfile + " profile under " + ProfileFolder;
            return FullLockProbe.RunAll(shipping, TrackBuilder.BuildMainTrack());
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
