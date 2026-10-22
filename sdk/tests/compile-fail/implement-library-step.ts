import { implement } from "@olivervea/pipelines";
import { test } from "../fixtures";

// @ts-expect-error -- expect: library steps already carry their implementation
implement(test, async () => ({ passed: 1, failed: 0 }));
