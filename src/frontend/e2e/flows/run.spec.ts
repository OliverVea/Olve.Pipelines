import { expect, runStatus, runTimeout, test } from '../support/test.ts';

test('open a pipeline, trigger a run and follow it to its step logs', async ({
  page,
  api,
}) => {
  const pipeline = await api.newPipeline('trigger');
  await api.setSecret(pipeline, 'FIXTURE_SECRET', 'set');

  await page.goto('/');
  await page.getByRole('link', { name: pipeline.name, exact: true }).click();
  await page.getByRole('button', { name: 'Trigger' }).click();
  await page.getByLabel('message').fill('from the browser');
  await page.getByRole('button', { name: 'Run', exact: true }).click();

  await expect(page.getByRole('heading', { name: 'Run #1' })).toBeVisible();
  await expect(runStatus(page)).toHaveText('succeeded', runTimeout);

  await page.locator('[data-step="consume"]').click();
  await expect(page.locator('step-sheet')).toContainText('from the browser');

  await page.locator('[data-step="produce"]').click();
  await expect(page.locator('step-sheet h3')).toHaveText('produce');
  await page.getByRole('link', { name: 'Logs' }).click();
  await expect(page.getByRole('heading', { name: 'produce' })).toBeVisible();
  await expect(page.locator('log-pane')).toContainText('produced the message');

  await page.getByRole('link', { name: pipeline.name }).click();
  await page.getByRole('link', { name: 'Runs', exact: true }).click();
  const row = page.getByRole('row', { name: /#1/ });
  await expect(row).toContainText('succeeded');
  await expect(row).toContainText('manual');
});

test('a failed step says why, and rerunning it after the fix finishes the run', async ({
  page,
  api,
}) => {
  const pipeline = await api.newPipeline('fix');
  const run = await api.startRun(pipeline);

  await page.goto(`/pipeline/${pipeline.id}/run/${run}`);
  await expect(runStatus(page)).toHaveText('failed', runTimeout);
  await page.locator('[data-step="consume"]').click();
  await expect(page.locator('step-sheet')).toContainText(
    "the handler threw (exit 1): step 'consume' failed: secret FIXTURE_SECRET is not set (the harness reads it from the environment variable FIXTURE_SECRET)",
  );

  await page.getByRole('link', { name: pipeline.name }).click();
  await page.getByRole('link', { name: 'Secrets', exact: true }).click();
  const secret = page.locator('.row', { hasText: 'FIXTURE_SECRET' });
  await expect(secret).toContainText('used by consume');
  await expect(secret).toContainText('not set');
  await secret.getByRole('button', { name: 'Set' }).click();
  await secret.getByPlaceholder('Value').fill('now set');
  await secret.getByRole('button', { name: 'Save' }).click();
  await expect(secret.locator('.state')).toHaveText('set');

  await page.goBack();
  await page.goBack();
  await expect(page.locator('step-sheet h3')).toHaveText('consume');
  await page.getByRole('button', { name: 'Rerun and continue' }).click();
  await expect(runStatus(page)).toHaveText('succeeded', runTimeout);
  await expect(page.getByRole('tab', { name: '@1 failed' })).toBeVisible();
  await expect(page.getByRole('tab', { name: '@2 succeeded' })).toBeVisible();
});

test('a blocked step holds a run until it is unblocked', async ({ page, api }) => {
  const pipeline = await api.newPipeline('block');
  await api.setSecret(pipeline, 'FIXTURE_SECRET', 'set');

  await page.goto(`/pipeline/${pipeline.id}`);
  await page.locator('[data-step="consume"]').click();
  await page.getByRole('button', { name: 'Block', exact: true }).click();
  await expect(page.locator('step-sheet .pill')).toHaveText('blocked');
  await page.getByRole('button', { name: 'Trigger' }).click();
  await page.getByRole('button', { name: 'Run', exact: true }).click();

  await expect(page.getByText('Held before')).toHaveText(
    'Held before consume: the step is blocked. Unblock it on the pipeline page to let the run continue.',
    runTimeout,
  );
  await expect(runStatus(page)).toHaveText('running');

  await page.locator('[data-step="consume"]').click();
  await page.getByRole('button', { name: 'Rerun step' }).click();
  await expect(page.getByRole('alert')).toContainText(
    "Step 'consume' is blocked; unblock it first.",
  );

  await page.getByRole('link', { name: 'pipeline page' }).click();
  await page.locator('[data-step="consume"]').click();
  await expect(page.locator('step-sheet')).toContainText(
    'Holding #1. Unblock to let it continue.',
  );
  await page.getByRole('button', { name: 'Unblock' }).click();
  await page.locator('step-sheet').getByRole('link', { name: '#1' }).first().click();
  await expect(runStatus(page)).toHaveText('succeeded', runTimeout);
});
