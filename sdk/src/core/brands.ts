// Runtime markers. `Symbol.for` (not `Symbol()`) so that two copies of the SDK in one process — e.g.
// a dev runner and the user's installed package — still recognise each other's objects.
export const STEP = Symbol.for('olve.pipelines.step');
export const IMPLEMENTATION = Symbol.for('olve.pipelines.implementation');
export const ORIGIN = Symbol.for('olve.pipelines.origin');
export const SKIPPED = Symbol.for('olve.pipelines.skipped');
export const PIPELINE = Symbol.for('olve.pipelines.pipeline');
export const REF_PATH = Symbol.for('olve.pipelines.refPath');

// Type-only brands (never exist at runtime).
export declare const ModelBrand: unique symbol;
export declare const RefBrand: unique symbol;
export declare const StepBrand: unique symbol;
export declare const SkippedBrand: unique symbol;
export declare const ImplementationBrand: unique symbol;
