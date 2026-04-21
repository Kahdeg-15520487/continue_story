import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.ts',
  timeout: 600_000,
  expect: { timeout: 10_000 },
  fullyParallel: false, // tests share Docker state
  retries: 0,
  reporter: 'list',
  use: {
    baseURL: 'http://localhost:5173',
    actionTimeout: 10_000,
  },
  projects: [
    { name: 'chromium', use: { browserName: 'chromium' } },
  ],
});
