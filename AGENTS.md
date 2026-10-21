# Agent Instructions

This policy supplements and does not override existing approved requirements, specifications, ADRs, project conventions, architectural decisions, quality gates, or security constraints.

## Engineering Simplicity

Prefer the simplest complete solution that satisfies the approved requirements and constraints.

- Reuse existing code, utilities, and established patterns before introducing new implementations.
- Avoid speculative features, premature abstractions, unnecessary dependencies, and redundant architectural layers.
- Introduce additional complexity only when justified by a concrete requirement, demonstrated risk, or measurable operational need.
- Preserve readability, correctness, security, maintainability, testability, observability, reliability, and rollback capabilities.
- Respect existing specifications, ADRs, project conventions, and architectural boundaries.
- Prefer small, focused, independently verifiable, and reversible changes.
- Do not sacrifice required behavior, validation, error handling, security controls, or test coverage merely to reduce code size.
- Apply SOLID, DDD, Clean Architecture, CQRS, and design patterns when they solve a real problem, not as mandatory rituals.
- For significant new abstractions or dependencies, briefly explain the requirement they satisfy and why a simpler alternative is insufficient.
- Do not refactor unrelated code simply because a shorter implementation is possible.

Simplicity is a design constraint, not a substitute for engineering judgment.
