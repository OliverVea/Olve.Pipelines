import { afterAll, afterEach, beforeEach } from 'bun:test';
import { removeTmpDirs, testEnded, testStarted } from './runtime/helpers.ts';

beforeEach(testStarted);
afterEach(testEnded);
afterAll(removeTmpDirs);
