using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using RenaultRacer.Track;
using RenaultRacer.Vehicle;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Ways to run <see cref="GlideProbe"/>:
    ///   Unity.exe -batchmode -nographics -quit -projectPath . \
    ///             -executeMethod RenaultRacer.EditorTools.GlideProbeMenu.RunFromCommandLine -logFile Logs/glide.log
    /// Writes Logs/glide-probe.txt (+ Logs/glide-probe-traces.txt).
    /// </summary>
    public static class GlideProbeMenu
    {
        private const string ProfileFolder = "Assets/ScriptableObjects/Handling";
        private const string ShippingProfile = "Production";
        private const string ReportPath = "Logs/glide-probe.txt";
        private const string TracePath = "Logs/glide-probe-traces.txt";

        [MenuItem("Renault Racer/Glide probe - heading carry and wall ping-pong", false, 45)]
        public static void Run()
        {
            string traces;
            Debug.Log(Build(out traces));
        }

        public static void RunFromCommandLine()
        {
            string report, traces = "";
            try { report = Build(out traces); }
            catch (Exception e)
            {
                Console.WriteLine("glide probe threw: " + e);
                EditorApplication.Exit(2);
                return;
            }
            Console.WriteLine(report);
            try
            {
                Directory.CreateDirectory("Logs");
                File.WriteAllText(ReportPath, report);
                File.WriteAllText(TracePath, traces);
                Console.WriteLine("[glide probe] written to " + ReportPath + " and " + TracePath);
            }
            catch (Exception e) { Console.WriteLine("could not write the report: " + e.Message); }
            bool clean = report.IndexOf("**FAIL**", StringComparison.Ordinal) < 0;
            Console.WriteLine("[glide probe] DONE");
            EditorApplication.Exit(clean ? 0 : 1);
        }

        private static string Build(out string traces)
        {
            traces = "";
            HandlingProfile shipping = Load(ShippingProfile);
            if (shipping == null) return "no " + ShippingProfile + " profile under " + ProfileFolder;
            return GlideProbe.RunAll(shipping, TrackBuilder.BuildMainTrack(), out traces);
        }

        private static HandlingProfile Load(string profileName)
        {
            string[] guids = AssetDatabase.FindAssets("t:HandlingProfile " + profileName, new[] { ProfileFolder });
            for (int i = 0; i < guids.Length; i++)
            {
                var p = AssetDatabase.LoadAssetAtPath<HandlingProfile>(AssetDatabase.GUIDToAssetPath(guids[i]));
                if (p != null && p.name == profileName) return p;
            }
            return null;
        }
    }
}
