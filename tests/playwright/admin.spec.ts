import { expect } from '@playwright/test';
import { adminReady, signIn, test } from './helpers';

// The admin needs a one-time owner created by the CLI at deploy; this suite
// runs against a stack whose owner credentials arrive by env. Without them it
// skips, and the skip is reported — never counted as a pass.
test.describe('admin (needs owner credentials)', () => {
  test.skip(!adminReady, 'set ARI_E2E_EMAIL, ARI_E2E_PASSWORD and ARI_E2E_ADMIN_URL to run the admin suite');

  test('sign-in shows the dashboard and permission-filtered nav', async ({ page }) => {
    await signIn(page);
    await expect(page.getByText(/dashboard/i).first()).toBeVisible();
    for (const item of ['Content', 'Media', 'Statistics', 'Users', 'Audit']) {
      await expect(page.getByRole('link', { name: new RegExp(item, 'i') }).first()).toBeVisible();
    }
  });

  test('publish then see it live', async ({ page }) => {
    await signIn(page);
    await page.goto('/content/page:home');
    const intro = page.getByLabel(/intro/i).first();
    await intro.fill(`Published by Playwright at ${Date.now()}`);
    await page.getByRole('button', { name: /save/i }).first().click();
    await page.getByRole('button', { name: /publish/i }).first().click();
    await expect(page.getByText(/published/i).first()).toBeVisible({ timeout: 15_000 });

    // The public site (same baseURL through the proxy) shows the change.
    await page.goto('/');
    await expect(page.getByText(/published by playwright/i).first()).toBeVisible({ timeout: 15_000 });
  });

  test('admin is noindexed', async ({ page }) => {
    const response = await page.goto(adminURL!);
    expect(response?.headers()['x-robots-tag']).toContain('noindex');
  });
});
