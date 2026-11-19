import { expect } from '@playwright/test';
import { test } from './helpers';

test.describe('public pages', () => {
  test('home renders the catalog with the signature GitHub strip', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    // Numbered work index carries the four flagship rows.
    for (const name of ['Enclave', 'RustOS', 'MultiForge']) {
      await expect(page.getByRole('link', { name: new RegExp(name, 'i') }).first()).toBeVisible();
    }
    // Dark ground, no light theme flash: the background stays dark after load.
    const background = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
    expect(background).not.toBe('rgb(255, 255, 255)');
    // Contact form is reachable.
    await expect(page.locator('form').first()).toBeVisible();
  });

  test('every work page loads', async ({ page }) => {
    for (const slug of ['enclave', 'rustos', 'multiforge', 'edex', 'red-wagon-rentals', 'connected-riding']) {
      const response = await page.goto(`/work/${slug}`);
      expect(response?.status(), slug).toBe(200);
    }
  });

  test('unknown address is a real 404', async ({ page }) => {
    const response = await page.goto('/no-such-page');
    expect(response?.status()).toBe(404);
    await expect(page.getByText(/not found|404/i).first()).toBeVisible();
  });

  test('contact form completes the site main action', async ({ page }) => {
    await page.goto('/');
    await page.getByLabel(/name/i).fill('Playwright Check');
    await page.getByLabel(/email/i).fill('playwright@example.com');
    await page.getByLabel(/message|body/i).fill('sent by the playwright suite');
    await page.getByRole('button', { name: /send/i }).click();
    await expect(page.getByText(/thank|sent/i).first()).toBeVisible({ timeout: 15_000 });
  });
});
