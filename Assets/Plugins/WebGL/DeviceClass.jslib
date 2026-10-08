mergeInto(LibraryManager.library, {
  // CSS px short side of the visual viewport (what a person perceives as screen size).
  RR_CssShortSide: function () {
    try {
      var vv = window.visualViewport;
      var w = vv ? vv.width : window.innerWidth, h = vv ? vv.height : window.innerHeight;
      return Math.min(w, h);
    } catch (e) { return 0; }
  },
  RR_CoarsePointer: function () {
    try { return (window.matchMedia && window.matchMedia("(pointer: coarse)").matches) ? 1 : 0; }
    catch (e) { return 0; }
  }
});
