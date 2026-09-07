# ADR 005-schema: OpenAPI SSOT

Date: 2026-09-07
Status: Accepted design; implementation tracked separately.

## Decision

Use OpenAPI 3.1 JSON, deterministic wire-model generation and shared fixtures.

## Alternatives and rationale

Separate handwritten DTOs drift. Large generic API generator stacks add runtime behavior and obscure streaming/cancellation. Our small generator only supports the explicit object/array/scalar subset and fails on unsupported types.

## Consequences

Generated models are checked in and CI verifies no drift. Runtime validation and security constraints remain explicit application code. Route/schema contract tests must accompany implemented endpoints.
