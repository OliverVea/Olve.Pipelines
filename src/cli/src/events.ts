import type { ChangeEvent } from '@olve/api';
import { changeEvents } from '@olve/client';
import { streamError } from './api';
import type { CommandContext } from './registry';

/**
 * Reads the change events (of one pipeline, or all) until the server ends the stream or `signal`
 * aborts (then it returns quietly). A non-2xx answer is a {@link CliError}; a lost connection is a
 * retryable one.
 */
export async function readChanges(
  ctx: Pick<CommandContext, 'api'>,
  pipelineId: string | undefined,
  onEvent: (event: ChangeEvent) => void,
  signal: AbortSignal,
): Promise<void> {
  try {
    for await (const event of changeEvents(ctx.api, signal, pipelineId)) onEvent(event);
  } catch (error) {
    if (signal.aborted) return;
    throw streamError(error, 'The event stream');
  }
}
