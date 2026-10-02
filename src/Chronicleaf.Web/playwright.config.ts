import { defineConfig, devices } from '@playwright/test';

/**
 * Browser fixtures run against the real built and published application started by
 * scripts/e2e.sh, not against the Vite dev server or a mock. Override the origin with
 * CHRONICLEAF_BASE_URL when the shell runs on another loopback port.
 *
 * Two fixture directories are picked up: e2e/ for the runtime shell and tests/ for the feature
 * packages, which is the path the design names for them.
 */
const baseURL = process.env.CHRONICLEAF_BASE_URL ?? 'http://127.0.0.1:5081';

export default defineConfig({
  testDir: './',
  testMatch: ['e2e/**/*.spec.ts', 'tests/**/*.spec.ts'],
  testIgnore: ['node_modules/**', 'dist/**', 'test-results/**', 'playwright.config.ts'],
  fullyParallel: false,
  forbidOnly: true,
  retries: 0,
  workers: 1,
  reporter: [['list']],
  use: {
    baseURL,
    trace: 'off',
    screenshot: 'off',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } },
    },
  ],
});
