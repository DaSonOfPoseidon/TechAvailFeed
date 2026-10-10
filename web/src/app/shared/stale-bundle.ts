import { NavigationError } from '@angular/router';

// Each deploy renames the lazy page bundles, so a tab opened before it fails to load a page it
// hasn't visited yet (Chrome, Firefox and Safari word the error differently).
export function isStaleBundle(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error);
  return /dynamically imported module|Importing a module script failed/i.test(message);
}

// Loads the page fresh instead, once per URL so a real 404 can't loop.
export function reloadOnStaleBundle(event: NavigationError): void {
  if (!isStaleBundle(event.error)) return;
  const marker = `techavail.reloaded:${event.url}`;
  try {
    if (sessionStorage.getItem(marker)) return;
    sessionStorage.setItem(marker, '1');
  } catch {
    // No session storage: reload anyway; a loop needs the bundle to be missing after a reload too.
  }
  location.assign(event.url);
}
