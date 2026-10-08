using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// ONE source file drives the brand logo everywhere:
    ///   Assets/Art/Resources/UI/brand_logo.png   (transparent PNG)
    /// The HUD loads it as a Sprite (UI/BrandLogo). The web loader cannot (it paints before
    /// Unity exists), so this tool re-inlines the same PNG, as base64, into the #bootEmblem
    /// img of Assets/WebGLTemplates/ShellRacer/index.html. Run it after swapping the PNG:
    ///   menu  Renault Racer > Brand > Sync logo into index.html
    ///   or    -executeMethod RenaultRacer.EditorTools.BrandLogoSync.RunFromCommandLine
    /// The inlined copy is capped at LoaderMaxWidth px wide (the loader shows it at most
    /// 400 css px wide, so 1.2x = 480; the wordmark is about 3.5:1) and re-encoded only when the source is larger.
    /// </summary>
    public static class BrandLogoSync
    {
        public const string LogoPath = "Assets/Art/Resources/UI/brand_logo.png";
        public const string HtmlPath = "Assets/WebGLTemplates/ShellRacer/index.html";
        private const int LoaderMaxWidth = 480;

        [MenuItem("Renault Racer/Brand/Sync logo into index.html")]
        public static void Sync() { Run(); }

        public static void RunFromCommandLine()
        {
            try { Run(); }
            catch (Exception e) { Console.WriteLine("[BrandLogoSync] FAILED: " + e); EditorApplication.Exit(2); return; }
            EditorApplication.Exit(0);
        }

        private static void Run()
        {
            if (!File.Exists(LogoPath)) throw new FileNotFoundException(LogoPath);
            byte[] png = File.ReadAllBytes(LogoPath);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(png)) throw new Exception("not a readable image: " + LogoPath);
            if (tex.width > LoaderMaxWidth)
            {
                int w = LoaderMaxWidth, h = Mathf.Max(1, Mathf.RoundToInt(tex.height * (float)w / tex.width));
                var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                tex.filterMode = FilterMode.Bilinear;
                Graphics.Blit(tex, rt);
                var prev = RenderTexture.active; RenderTexture.active = rt;
                var small = new Texture2D(w, h, TextureFormat.RGBA32, false);
                small.ReadPixels(new Rect(0, 0, w, h), 0, 0); small.Apply();
                RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
                png = small.EncodeToPNG();
            }
            string uri = "data:image/png;base64," + Convert.ToBase64String(png);
            string html = File.ReadAllText(HtmlPath);
            var rx = new Regex("(<img id=\"bootEmblem\"[^>]*?src=\")[^\"]*(\")");
            if (!rx.IsMatch(html)) throw new Exception("#bootEmblem <img> not found in " + HtmlPath);
            html = rx.Replace(html, m => m.Groups[1].Value + uri + m.Groups[2].Value, 1);
            File.WriteAllText(HtmlPath, html);
            Debug.Log("[BrandLogoSync] inlined " + LogoPath + " (" + png.Length + " bytes, " + tex.width + "x" + tex.height + ") into " + HtmlPath);
        }
    }
}
