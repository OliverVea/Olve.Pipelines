# Design

Olve.Pipelines is a lightweight CI/CD pipeline configuration and orchestration service.

## Pipeline

A **pipeline** is the top-level entity. It groups sources, builds, and processing steps.

## Sources

A pipeline has one or more named **sources** (e.g. a GitHub repository). When any source detects a change, all sources are snapshotted together and fed to the build step. Sources can also be triggered manually.

## Builds

A pipeline has one or more named **builds**. A build takes the source snapshot as input and produces **artifacts**. Each build defines the set of named artifacts it outputs (e.g. "Docker Image").

## Processing Steps

A pipeline has an ordered list of **processing steps** (e.g. "Deploy to Staging", "Deploy to Production"). Each processing step:

- Takes a build's artifacts as input.
- Runs a main action (e.g. deployment).
- Runs a list of **verification steps** (e.g. health check, smoke test).
- On success, promotes the artifacts to the next processing step.
- On verification failure, blocks further progress until it succeeds.

## Manual Triggers

Each step in the pipeline (source snapshot, build, processing) can be triggered independently with a "run this step with whatever is currently at your input" action.

## Current Scope

For now, only pipeline **configuration** is modeled (entities, CRUD endpoints). Actual execution (source polling, build runners, S3 storage, artifact bundling) will be added later.
