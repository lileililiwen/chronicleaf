import { useCallback, useEffect, useState } from 'react';

/**
 * A route reader with no router dependency.
 *
 * The runtime already serves the built client for every non-API path, so a deep link such as
 * `/projects/<id>` is answered with the same document as `/`. All this adds is the small amount of
 * history handling a single-page view needs: read the current path, follow browser back and forward,
 * and push a new path. Nothing here decides what a path means - the views do.
 */

function currentPath(): string {
  const path = window.location.pathname;

  // A trailing slash is the same place; without this a hand-typed `/projects/` would be a
  // different route from `/projects`.
  return path.length > 1 && path.endsWith('/') ? path.slice(0, -1) : path;
}

export interface Route {
  readonly path: string;
  readonly navigate: (to: string) => void;
}

export function useRoute(): Route {
  const [path, setPath] = useState<string>(currentPath);

  useEffect(() => {
    const follow = () => setPath(currentPath());

    window.addEventListener('popstate', follow);

    return () => window.removeEventListener('popstate', follow);
  }, []);

  const navigate = useCallback((to: string) => {
    if (to !== currentPath()) {
      window.history.pushState(null, '', to);
    }

    setPath(to);
    window.scrollTo(0, 0);
  }, []);

  return { path, navigate };
}

export interface Segment {
  readonly head: string;
  readonly rest: string;
}

/** Splits a path into its segments, ignoring empty ones. */
export function segmentsOf(path: string): readonly string[] {
  return path.split('/').filter((segment) => segment.length > 0);
}
