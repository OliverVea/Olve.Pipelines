// Shared chrome for the mocks. Include on every mock page with:
//   <script src="footer.js" defer></script>
//
// The mocks are a desktop dev-tool prototype (not a PWA), so this stays small:
// just the load-in animation-style switch. If a genuinely cross-page
// interactive pattern shows up on multiple screens, lift it here the same way;
// otherwise keep mock JS inline and disposable.
(() => {
  // Pick the load-in animation with ?anim=fade|cascade|pop (default cascade)
  // so styles can be compared without editing CSS. Sets a class on <body> the
  // .anim-* rules in mocks.css key off.
  const anim = new URLSearchParams(location.search).get('anim') || 'cascade';
  const known = { fade: 'anim-fade', cascade: 'anim-cascade', pop: 'anim-pop' };
  document.body.classList.add(known[anim] || known.cascade);
})();
