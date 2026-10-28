import type { Command } from '../registry';
import { attemptCommands } from './attempt';
import { bindingCommands } from './binding';
import { configCommands } from './config';
import { clusterCommands } from './cluster';
import { devCommands } from './dev';
import { loginCommand } from './login';
import { pipelineCommands } from './pipeline';
import { profileCommands } from './profile';
import { runCommands } from './run';
import { secretCommands } from './secret';
import { stepCommands } from './step';
import { waitCommands } from './wait';
import { watchCommand } from './watch';

/** Every command, in the order `pl help` lists them. */
export const commands: readonly Command[] = [
  loginCommand,
  ...profileCommands,
  ...waitCommands,
  ...pipelineCommands,
  ...runCommands,
  ...stepCommands,
  ...attemptCommands,
  watchCommand,
  ...secretCommands,
  ...configCommands,
  ...bindingCommands,
  ...devCommands,
  ...clusterCommands,
];
