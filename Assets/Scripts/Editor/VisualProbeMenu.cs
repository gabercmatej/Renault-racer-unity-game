using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Runs <see cref="VisualProbe"/> from the menu and from the command line.
    ///
    ///   Unity.exe -batchmode -quit -projectPath . \
    ///             -executeMethod RenaultRacer.EditorTools.VisualProbeMenu.RunFromCommandLine \
    ///             -logFile -
    /// </summary>
    public static class VisualProbeMenu
    {
        private const string ReportPath = "Logs/visual-probe.txt";

        [MenuItem("Renault Racer/Visual probe - crests and the horizon", false, 43)]
        public static void Run()
        {
            Debug.Log(VisualProbe.RunAll());
        }

        public static void RunFromCommandLine()
        {
            string report;
            try { report = VisualProbe.RunAll(); }
            catch (Exception e)
            {
                Console.WriteLine("visual probe threw: " + e);
                EditorApplication.Exit(2);
                return;
            }

            Console.WriteLine(report);
            try
            {
                Directory.CreateDirectory("Logs");
                File.WriteAllText(ReportPath, report);
                Console.WriteLine("[visual probe] written to " + ReportPath);
            }
            catch (Exception e) { Console.WriteLine("could not write the report: " + e.Message); }

            bool clean = report.IndexOf("**FAIL**", StringComparison.Ordinal) < 0;
            Console.WriteLine(clean ? "[visual probe] ALL PASS" : "[visual probe] FAILURES ABOVE");
            EditorApplication.Exit(clean ? 0 : 1);
        }
    }
}
