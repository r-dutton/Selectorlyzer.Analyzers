# Selectize Support Planning

## Context Summary
- Repo cloned for reference: `/tmp/theproclaimer` (FlowBuilder + GraphKit).
- Selectorlyzer currently parses a CSS4-inspired syntax but lacks several selectors, attribute matchers, and semantic linkage helpers required to match Selectize language expectations.
- FlowBuilder/GraphKit demonstrates graph-based linking between controllers, services, data operations, and workspace context that we want to expose in Selectorlyzer via selectors/rules.

## Existing Capabilities
- Supports basic CSS4 constructs: compound/complex selectors, combinators, `:is`, `:has`, `:not`, `:nth-child`, `:first-child`, `:last-child`.
- Property matchers: equality, substring/prefix/suffix, whitespace-separated tokens, numeric comparisons for custom properties.
- Pseudo-classes limited to Roslyn shorthand (`:class`, `:method`, etc.).
- Context limited to syntax nodes + optional semantic model; no project/solution or config awareness.

## Gaps vs Selectize Requirements
- Attribute matcher coverage missing `|=` (dash-match), `!=`, and explicit case-insensitive modifiers.
- No boolean logic for attribute expressions (`and`, `or`) beyond chaining selectors; need chained conditions akin to FlowBuilder filters.
- Missing pseudo-classes for symbol metadata (e.g., accessibility, static/async, inheritance) and project/solution-level scoping.
- Cannot reference semantic symbols, containing projects, or cross-config graph (FlowBuilder analog).
- No support for `:where`, `:scope`, `:root`, or typed relationship filters (e.g., `:nth-of-type`, `:only-child`).
- Need ability to navigate Roslyn symbol graph (method calls, type usages) similar to FlowBuilder edges.

## Proposed Workstreams
1. **Selector Grammar Enhancements**
   - Extend attribute matcher parsing to include `|=`, `!=`, `!^=`, etc., and interpret `i` modifier for case-insensitive comparisons.
   - Add additional pseudo-classes from CSS4 + Roslyn-specific ones (e.g., `:where`, `:scope`, `:root`, `:assembly`, `:project`).
   - Support relational pseudos for symbol graph traversal (`:calls(...)`, `:references(...)`).
   - Implement arithmetic/boolean expressions inside attribute selectors (e.g., `[Parameters.Count >= 2 && Modifiers~='async']`).

2. **Semantic & Workspace Context**
   - Extend `SelectorMatcherContext` to carry `SemanticModel`, `ISymbol`, `Project`, `Solution`, and config references.
   - Introduce helper APIs to fetch related symbols and FlowBuilder-style reachability sets.

3. **GraphLink Selectors**
   - Model FlowBuilder/GraphKit style flows as selectors: endpoints, data operations, service injections.
   - Provide combinators to follow edges (call graph, data flow) with max depth similar to FlowBuilder.

4. **Config & Rule Linking**
   - Allow selectors to reference named rules/config entries, enabling reuse and chaining (Selectize union semantics).
   - Add ability to parameterize selectors (templated values from config, e.g., `${Project.Name}`).

5. **Testing & Samples**
   - Expand unit tests mirroring FlowBuilder scenarios.
   - Document new syntax and provide examples.

## Open Questions / Research Items
- Confirm Selectize language specification (CSS4 superset?) to avoid diverging semantics.
- Determine performance implications for deep symbol graph traversals.
- Evaluate compatibility with existing rules (`selectorlyzer.json`).

## Next Steps Checklist
- [x] Define data structures for new context (workspace graph, semantic caches).
- [x] Implement grammar + parser updates for new matchers/pseudos.
- [ ] Build matcher implementations hooking into Roslyn + FlowBuilder-inspired graph utilities.
- [ ] Update configuration parsing to include linking metadata.
- [x] Write comprehensive tests + docs.

## Recent Progress
- Added semantic property resolution hooks so selectors can inspect `Type`, `ConvertedType`, constant values, and declared symbols via the semantic model.
- Normalized enumerable property values (including Roslyn symbol collections) so Selectize rules can reason about interface implementations and generic type arguments.
- Authored FlowBuilder-inspired selector tests covering controller patterns, service/repository usage, HTTP client operations, and DI registrations to verify rule coverage.

