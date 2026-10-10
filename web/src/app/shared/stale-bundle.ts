import { NavigationError } from '@angular/router';

// Each deploy renames the lazy page bundles, so a tab opened before it fails to load a page it
// hasn't visited yet (Chrome, Firefox and Safari word the error differently).
export function isStaleBundle(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error);
  return /dynamically imported module|Importing a module script failed/i.test(message);
}

// How soon after a reload the same URL failing again counts as a loop (a bundle that is really
// missing), not another deploy.
const loopWindowMs = 30_000;

// Loads the page fresh instead, unless this URL was just reloaded for the same reason.
export function reloadOnStaleBundle(event: NavigationError): void {
  if (!isStaleBundle(event.error)) return;
  const marker = `techavail.reloaded:${event.url}`;
  try {
    if (Date.now() - Number(sessionStorage.getItem(marker) ?? 0) < loopWindowMs) return;
    sessionStorage.setItem(marker, String(Date.now()));
  } catch {
    // Without session storage a really missing bundle would reload forever; leave the link dead.
    return;
  }
  // The router's URL is relative to the app's base href.
  location.assign(new URL(event.url.replace(/^\//, ''), document.baseURI).href);
}
