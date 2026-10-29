import { runStepsDownload } from '@olve/api';
import { api } from './client.js';

/** One stored file of an attempt, handed to the browser as a download. */
export async function downloadFile(
  file: {
    pipelineId: string;
    number: number;
    step: string;
    attempt: number;
    path: string;
  },
  fileName: string,
): Promise<void> {
  const { pipelineId, number, step, attempt, path } = file;
  const { data } = await runStepsDownload({
    client: api,
    path: { pipelineId, number, step, path },
    query: { attempt },
    parseAs: 'blob',
    throwOnError: true,
  });
  const objectUrl = URL.createObjectURL(data);
  const a = document.createElement('a');
  a.href = objectUrl;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(objectUrl), 10_000);
}
