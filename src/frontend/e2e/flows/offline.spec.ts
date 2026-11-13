import { expect, test } from '../support/test.ts';

test('a page opened without a connection shows the offline page, and Try again comes back', async ({
  page,
  context,
}) => {
  await page.goto('/');
  await page.evaluate(() => navigator.serviceWorker.ready);
  // The first load isn't controlled by the worker yet; claim() reaches it, a reload makes sure.
  await page.reload();
  await expect(
    page.getByRole('heading', { name: 'Pipelines', exact: true }),
  ).toBeVisible();

  await context.setOffline(true);
  await page.reload();
  await expect(
    page.getByRole('heading', { name: "Can't reach Olve Pipelines" }),
  ).toBeVisible();
  await page.getByRole('button', { name: 'Try again' }).click();
  await expect(page.getByRole('status')).toHaveText('Still no connection.');

  await context.setOffline(false);
  await page.getByRole('button', { name: 'Try again' }).click();
  await expect(
    page.getByRole('heading', { name: 'Pipelines', exact: true }),
  ).toBeVisible();
});
