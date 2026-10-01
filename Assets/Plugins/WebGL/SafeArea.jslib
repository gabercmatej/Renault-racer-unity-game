// Safe-area bridge (plan section 14 / Phase 6).
//
// Unity WebGL does NOT populate Screen.safeArea. On every browser it returns the full
// canvas, so a notch, a Dynamic Island or an Android cutout is invisible to C# and the
// brake button ends up underneath one. The insets only exist in CSS, as
// env(safe-area-inset-*), and only when the page opts in with viewport-fit=cover.
//
// The ShellRacer template measures them into window.__rrSafeInsets (CSS pixels). This
// reads them back and converts to the canvas backing-store pixels that Screen.width and
// Screen.height are expressed in, which is the coordinate space C# actually works in.
//
// Returns 0 anywhere the page has not measured them - a phone with no cutout, a desktop
// browser, or a host page that is not ours - so the C# side degrades to "full screen is
// safe", which is the correct answer in all of those cases.

mergeInto(LibraryManager.library, {

  // side: 0 = top, 1 = right, 2 = bottom, 3 = left
  RRGetSafeInset: function (side) {
    try {
      var s = window.__rrSafeInsets;
      if (!s) return 0;

      var css = 0;
      if (side === 0) css = s.top;
      else if (side === 1) css = s.right;
      else if (side === 2) css = s.bottom;
      else if (side === 3) css = s.left;

      if (!(css > 0)) return 0;

      // Screen.width in Unity is canvas.width, the backing store, not the CSS size.
      var canvas = document.querySelector("#unity-canvas");
      var scale = 1;
      if (canvas && canvas.clientWidth > 0) scale = canvas.width / canvas.clientWidth;
      if (!(scale > 0)) scale = 1;

      return css * scale;
    } catch (e) {
      return 0;
    }
  },

  // 1 when the page has actually measured insets, 0 when C# should fall back.
  RRHasSafeArea: function () {
    return (window.__rrSafeInsets && window.__rrSafeInsets.measured) ? 1 : 0;
  },

  // Device pixel ratio cap, for QualityDirector (plan 20).
  //
  // This is the one quality knob C# cannot reach on its own: the backing-store size of
  // the canvas is the page's business, not the engine's, and it is also the single
  // biggest lever on a cheap phone. A Redmi 9A at DPR 2 renders four times the pixels
  // of DPR 1 onto a panel that cannot show the difference, and this game is fill-rate
  // bound, not geometry bound.
  //
  // The template owns the cap and the resize handler; this only moves the number and
  // asks for a re-layout. Hosted by a page that is not ours, it does nothing at all -
  // which is the correct outcome, not a failure.
  RRSetMaxDpr: function (cap) {
    try {
      if (typeof window.__rrSetMaxDpr === "function") window.__rrSetMaxDpr(cap);
    } catch (e) { /* a host page without our template: the other knobs still apply */ }
  },

  // Best-effort fullscreen, asked for on the player's first deliberate tap.
  //
  // Best-effort is the whole contract. The Fullscreen API must be called from inside a
  // user gesture, and even then iPhone Safari does not implement it for anything that
  // is not a <video> - so on the single most important device in this campaign it will
  // simply not happen, and no amount of cleverness changes that. Chrome on Android and
  // iPad Safari do grant it.
  //
  // Nothing downstream is allowed to depend on it. The layout is driven by the visual
  // viewport either way (see the template's resize()), so the game fills whatever space
  // the browser leaves and browser chrome staying visible costs nothing but pixels.
  RRRequestFullscreen: function () {
    try {
      if (typeof window.__rrRequestFullscreen === "function")
        return window.__rrRequestFullscreen() ? 1 : 0;
    } catch (e) { }
    return 0;
  },

  RRIsFullscreen: function () {
    try {
      return (document.fullscreenElement || document.webkitFullscreenElement) ? 1 : 0;
    } catch (e) { return 0; }
  },

  // Device memory in GB, for QualityDirector's opening guess - or 0 for "would not say".
  //
  // THE REASON THIS EXISTS. Unity's SystemInfo.systemMemorySize on WebGL does not report
  // the device's RAM. It reports the WebAssembly heap, which is a number this project
  // chose in its own player settings and which is the same on a Redmi 9A and on a desktop
  // workstation. It came back at or under 2048 MB, so EVERY WebGL device was classified
  // Weak at boot - and Weak had the skid marks switched off, which is why no build has
  // ever shown a skid mark on a phone.
  //
  // navigator.deviceMemory is the real thing, where it exists. It is coarse by design
  // (the spec rounds to 0.25/0.5/1/2/4/8) and iOS Safari and Firefox do not implement it
  // at all, which is fine: 0 means "no opinion" and the C# side then starts optimistic
  // and lets the frame-time measurement decide. Not knowing is not evidence of being slow.
  RRDeviceMemoryGb: function () {
    try {
      var gb = navigator.deviceMemory;
      return (typeof gb === "number" && gb > 0) ? gb : 0;
    } catch (e) { return 0; }
  },

  // Logical processors, same contract: 0 where the browser will not say.
  RRHardwareConcurrency: function () {
    try {
      var n = navigator.hardwareConcurrency;
      return (typeof n === "number" && n > 0) ? n : 0;
    } catch (e) { return 0; }
  }
});
