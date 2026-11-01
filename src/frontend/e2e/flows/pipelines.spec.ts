import { newName, repoOf } from '../support/api.ts';
import { expect, test } from '../support/test.ts';

test('add a pipeline from the list and delete it', async ({ page }) => {
  const name = newName('add');
  await page.goto('/');
  page.once('dialog', (dialog) => dialog.accept(repoOf(name)));
  await page.getByRole('button', { name: 'Add pipeline' }).click();
  await page.getByRole('link', { name, exact: true }).click();
  await expect(page.locator('[data-step="produce"]')).toBeVisible({ timeout: 60_000 });

  await page.getByRole('link', { name: 'Olve Pipelines' }).click();
  const row = page.locator('.pipeline-row', { hasText: name });
  await row.getByRole('button', { name: 'Delete' }).click();
  const dialog = page.getByRole('dialog');
  const confirm = dialog.getByRole('button', { name: 'Delete' });
  await expect(dialog).toContainText(`Delete ${name}?`);
  await expect(confirm).toBeDisabled();
  await dialog.getByRole('textbox').fill(`delete ${name}`);
  await confirm.click();
  await expect(row).toHaveCount(0);
  await page.reload();
  await expect(
    page.getByRole('heading', { name: 'Pipelines', exact: true }),
  ).toBeVisible();
  await expect(row).toHaveCount(0);
});

test('adding a repo the server refuses says why', async ({ page }) => {
  await page.goto('/');
  page.once('dialog', (dialog) => dialog.accept('   '));
  await page.getByRole('button', { name: 'Add pipeline' }).click();
  await expect(
    page.getByText("Binding requires a repository ('owner/name')."),
    'the list view drops the refusal of Add pipeline: _create has no error handling',
  ).toBeVisible();
});
