import { defineConfig, devices } from '@playwright/test';

// The suite runs against a REAL stack (api + web containers or local processes).
// ARI_BASE_URL points it anywhere; a stub is fine while building pages, but a
// pass on a stub says nothing about the backend.
const baseURL = process.env.ARI_BASE_URL ?? 'http://localhost:8090';

export default defineConfig({
  testDir: '.',
  timeout: 30_000,
  workers: 1, // suites share one stack
  reporter: [['list']],
  use: {
    baseURL,
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'phone', use: { ...devices['Pixel 7'] } },
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1920, height: 1080 } } },
  ],
});
