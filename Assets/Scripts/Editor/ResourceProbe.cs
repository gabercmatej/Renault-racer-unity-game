using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using RenaultRacer.Core;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Runs <see cref="ArtIntegrity"/> on its own, so "is the art actually there" can be
    /// answered in five seconds without building anything.
    ///
    /// Deliberately thin. The list of what production needs lives in ArtIntegrity and
    /// nowhere else - a probe that kept its own copy would be a second claim about the
    /// same thing, and the second claim is always the one that goes stale.
    ///
    ///   Unity.exe -batchmode -quit -projectPath . \
    ///             -executeMethod RenaultRacer.EditorTools.ResourceProbe.RunFromCommandLine \
    ///             -logFile -
    /// </summary>
    public static class ResourceProbe
    {
        private const string ReportPath = "Logs/resource-probe.txt";

        [MenuItem("Renault Racer/Resource probe - is the production art there", false, 44)]
        public static void Run()
        {
            string report = BuildReport();
            if (report.IndexOf("**FAIL**", StringComparison.Ordinal) >= 0) Debug.LogError(report);
            else Debug.Log(report);
        }

        public static void RunFromCommandLine()
        {
            string report;
            try { report = BuildReport(); }
            catch (Exception e)
            {
                Console.WriteLine("resource probe threw: " + e);
                EditorApplication.Exit(2);
                return;
            }

            Console.WriteLine(report);
            try
            {
                Directory.CreateDirectory("Logs");
                File.WriteAllText(ReportPath, report);
                Console.WriteLine("[resource probe] written to " + ReportPath);
            }
            catch (Exception e) { Console.WriteLine("could not write the report: " + e.Message); }

            bool clean = report.IndexOf("**FAIL**", StringComparison.Ordinal) < 0;
            Console.WriteLine(clean ? "[resource probe] ALL PASS" : "[resource probe] FAILURES ABOVE");
            EditorApplication.Exit(clean ? 0 : 1);
        }

        private static string BuildReport()
        {
            string detail;
            bool ok = ArtIntegrity.Verify(out detail);

            var sb = new System.Text.StringBuilder(1024);
            sb.AppendLine("================ PRODUCTION RESOURCES ================");
            sb.AppendLine("Every atlas below is loaded through Resources, and every loader");
            sb.AppendLine("degrades to a plain generated shape when its atlas is absent. That");
            sb.AppendLine("fallback is silent and plausible - which is how an Editor session");
            sb.AppendLine("came to draw a flat red circle where the brake button belongs while");
            sb.AppendLine("the web build of the same commit drew the real one.");
            sb.AppendLine();
            sb.AppendLine("Each entry is LOADED, not merely looked up, and each atlas has its");
            sb.AppendLine("metadata parsed - three different failures, and the last is the one");
            sb.AppendLine("that hides: json regenerated against a different texture loads");
            sb.AppendLine("perfectly and draws nothing.");
            sb.AppendLine();
            sb.Append(detail);
            sb.AppendLine();
            sb.AppendLine("ALL PRODUCTION ART PRESENT AND READABLE = " + (ok ? "PASS" : "**FAIL**"));
            sb.AppendLine("=====================================================");
            return sb.ToString();
        }
    }
}
