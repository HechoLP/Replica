# License decision

## Status

**Open — no license selected.**

No `LICENSE` file is added at the foundation stage because choosing a license changes the rights granted by the repository owner and cannot be inferred from the implementation stack or public visibility. Without an explicit license, copyright remains with the owner and others do not receive general permission to copy, modify, or distribute the work.

## Decision owner and timing

The repository owner, HechoLP, must approve the license before outside contributions are solicited or a stable public release is described as open source. The approved choice should be recorded here, followed by the canonical license text in root `LICENSE` and any required source/installer notices.

## Questions to resolve

- Should commercial use and closed-source redistribution be permitted?
- Should derivative works be required to use the same license?
- Is an explicit patent grant desired?
- Will Replica link or distribute dependencies with notice or reciprocal-license requirements?
- How should contributions be licensed, and is a contributor agreement or developer certificate needed?
- Are the name, logo, and installer subject to a separate trademark policy?

## Candidate categories for owner/legal review

- Permissive licenses such as MIT or Apache-2.0 favor broad reuse; Apache-2.0 includes an express patent license and notice obligations.
- Weak copyleft licenses can require changes to covered components to remain available while allowing some larger combined works.
- Strong copyleft licenses can require corresponding source for distributed derivatives and need careful installer/dependency analysis.
- Source-available terms are not open-source licenses and should not be described as such.

This document is not legal advice. The owner should consider the intended distribution model and obtain qualified advice if patent, commercial, or reciprocal-license concerns are material.

## Completion checklist

1. Inventory planned and actual dependencies and their licenses.
2. Record owner approval and rationale in a focused PR.
3. Add the unmodified canonical license text as `LICENSE`.
4. Add copyright year/holder and required notices.
5. Update README, contribution guidance, installer, About view, source packages, and release notes.
6. Configure automated dependency/license checks and document exception handling.
