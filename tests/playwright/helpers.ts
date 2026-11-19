import { test as base, expect } from '@playwright/test';

// Both projects run every test; most traffic is phones.
//
// Admin tests need a signed-in owner. The stack's owner credentials come from
// env (ARI_E2E_EMAIL, ARI_E2E_PASSWORD, ARI_E2E_ADMIN_URL); without them the
// admin suite skips with a stated reason — a skip is not a pass and is
// reported as such.
const email = process.env.ARI_E2E_EMAIL;
const password = process.env.ARI_E2E_PASSWORD;
export const adminURL = process.env.ARI_E2E_ADMIN_URL;
export const adminReady = Boolean(email && password && adminURL);

export const test = base.extend({
  // eslint-disable-next-line no-empty-pattern
  page: async ({ page }, use, testInfo) => {
    const errors: string[] = [];
    page.on('console', message => {
      if (message.type() !== 'error') return;
      // A resource load reporting the HTTP status (e.g. the expected 404 page)
      // is not a page defect; CSP violations and script errors still fail.
      if (/^Failed to load resource/.test(message.text())) return;
      errors.push(message.text());
    });
    page.on('pageerror', error => errors.push(String(error)));
    await use(page);
    // CSP violations and script errors surface as console errors.
    expect(errors, `console errors on ${testInfo.title}`).toEqual([]);
  },
});

export async function signIn(page: import('@playwright/test').Page) {
  if (!email || !password || !adminURL) throw new Error('admin env missing');
  await page.goto(`${adminURL}/`);
  await page.getByLabel('EMAIL').fill(email);
  await page.getByLabel('PASSWORD').fill(password);
  await page.getByRole('button', { name: 'SIGN IN' }).click();
  await expect(page.getByRole('navigation')).toBeVisible();
}
