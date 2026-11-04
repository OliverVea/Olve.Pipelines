import { afterAll } from 'bun:test';
import { removeTmpDirs } from './runtime/helpers.ts';

afterAll(removeTmpDirs);
