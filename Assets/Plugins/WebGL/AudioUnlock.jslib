// Mobile web-audio state reader.
//
// The unlock itself lives in the page template (Assets/WebGLTemplates/ShellRacer/
// index.html, the first <script> in <head>): it records every AudioContext through the
// standard constructor, resumes them from real user-activation events (touchend /
// pointerup / click / keydown, capture phase), sets navigator.audioSession to 'playback'
// on Safari 17+, and retries after the page returns from the background. It depends on
// browser APIs only - nothing here or there touches Unity's internal audio objects.
//
// Why the page and not C#: StartFromReady() runs in Unity's frame loop, outside the DOM
// event, so it can never satisfy the browser's user-activation rule. Only a DOM listener
// can. Unity's own resume path listens to touchstart/mousedown, which Android Chrome does
// not count as activation, and it ignores iOS's 'interrupted' state.
//
// Every function tolerates a page without window.__rrAudio (another template): the state
// then reads 0 ("no context"), which the sound button shows as "tap to enable".

mergeInto(LibraryManager.library, {

  RRAudioInit: function () {
    if (!window.__rrAudio) console.warn('[RRAudio] page template has no audio unlock script');
  },

  // 0 none, 1 suspended, 2 running, 3 interrupted, 4 closed, 5 running-but-clock-frozen
  RRAudioState: function () {
    try { return window.__rrAudio ? window.__rrAudio.rawState() : 0; } catch (e) { return 0; }
  },

  RRAudioGesturesSinceLoss: function () {
    try { var g = window.__rrAudio; return g ? (g.gestures - g.gesturesAtLoss) : 0; } catch (e) { return 0; }
  },

  // Milliseconds since the last activating event (huge when none).
  RRAudioMsSinceGesture: function () {
    try {
      var g = window.__rrAudio;
      return g ? Math.min(1000000000, Math.floor(performance.now() - g.lastGestureMs)) : 1000000000;
    } catch (e) { return 1000000000; }
  },

  RRAudioGestureCount: function () {
    try { return window.__rrAudio ? window.__rrAudio.gestures : 0; } catch (e) { return 0; }
  },

  RRAudioSessionMode: function () {
    try { return window.__rrAudio ? window.__rrAudio.session : 0; } catch (e) { return 0; }
  },

  RRAudioResumeErrors: function () {
    try { return window.__rrAudio ? window.__rrAudio.resumeErrors : 0; } catch (e) { return 0; }
  },

  RRAudioPageHidden: function () {
    try { return document.visibilityState === 'hidden' ? 1 : 0; } catch (e) { return 0; }
  },

  // How many times the page has gone hidden since the first call. A counter, not a
  // state: a hidden page gets no frames, so by the time C# runs again it is visible and
  // only a count that moved can say it was ever away. The listener is added on first use.
  RRPageHideCount: function () {
    try {
      if (!window.__rrHide) {
        window.__rrHide = { n: 0 };
        document.addEventListener('visibilitychange', function () {
          if (document.visibilityState === 'hidden') window.__rrHide.n++;
        });
      }
      return window.__rrHide.n;
    } catch (e) { return 0; }
  },

  // Best-effort retry from C#. Only effective when the browser allows it; the DOM
  // listeners in the template are what actually satisfy the user-activation rule.
  RRAudioResume: function () {
    try { if (window.__rrAudio) window.__rrAudio.tryResume(false); } catch (e) {}
  }
});
