/**
 * Svelte action: mirror the visual viewport (which shrinks when the mobile
 * keyboard opens and when browser chrome is shown) into CSS custom properties.
 *
 * Exposes on the node:
 *   --vvh  visible viewport height in px
 *   --vvt  visible viewport offset from the layout viewport top in px
 *   --vvw  visible viewport width in px
 *
 * Works without the API (older browsers) by leaving the vars unset, in which
 * case CSS falls back to its previous declaration.
 */
export function visualViewport(node: HTMLElement) {
  if (typeof window === 'undefined') return {};
  const vv = window.visualViewport;
  if (!vv) return {};

  let raf = 0;
  const apply = () => {
    raf = 0;
    node.style.setProperty('--vvh', `${Math.round(vv.height)}px`);
    node.style.setProperty('--vvt', `${Math.round(vv.offsetTop)}px`);
    node.style.setProperty('--vvw', `${Math.round(vv.width)}px`);
  };
  const update = () => {
    if (!raf) raf = requestAnimationFrame(apply);
  };

  apply();
  vv.addEventListener('resize', update);
  vv.addEventListener('scroll', update);

  return {
    destroy() {
      if (raf) cancelAnimationFrame(raf);
      vv.removeEventListener('resize', update);
      vv.removeEventListener('scroll', update);
    }
  };
}
