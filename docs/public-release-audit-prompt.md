# Public Release Audit Prompt

Use this prompt with an AI agent to audit the repository for public-release readiness without making any changes.

## Prompt

```text
You are a senior release engineer reviewing a private repository before it is made public on GitHub.

Your task is to perform a full audit of the repository and produce a professional written report.

This is an audit-only pass.
Do not edit, delete, rename, move, commit, or push anything.
Do not propose speculative fixes without first grounding them in repository evidence.
Base every conclusion on files you actually inspected.

## Objectives

Review the entire repository for public-release readiness, with emphasis on:

1. Privacy and anonymization
- Check whether any real-looking personal names, student names, family names, school names, district names, email addresses, tokens, IDs, or other identifying values remain in tracked files.
- Specifically check for sample student names such as StudentOne and StudentTwo, and any real school names.
- Search beyond source code: include docs, tests, prompts, JSON files, sample commands, config files, exports, logs, and example output.

2. README quality
- Review the README as a public GitHub landing page.
- Confirm whether it clearly explains what the project does, who it is for, how it runs, and how a developer can evaluate it.
- Identify missing, outdated, redundant, overly internal, or confusing content.

3. Documentation quality and relevance
- Review all documentation files and classify each as one of:
  - public-facing documentation
  - contributor/maintainer documentation
  - internal-only documentation that should not remain in a public repo
- Determine whether the docs are complete, current, and limited to relevant information.
- Flag outdated docs, duplicated setup instructions, stale examples, or docs that expose private details.

4. Docker and deployment file organization
- Review Docker-related files and determine whether they are in the right location and whether the repo structure makes sense to an outside contributor.
- Distinguish between files that are correctly placed versus files that are poorly explained.
- Pay special attention to the relationship between Docker Compose and any local development orchestration project.

5. Source-control hygiene
- Identify tracked files that do not belong in a public repository.
- Check for committed local overrides, secrets, credentials, logs, build output, IDE cache files, generated files, or internal exports.
- Review .gitignore coverage and identify any tracked files that should instead be ignored or removed.

## Required workflow

1. Inventory the repository contents.
2. Search the full repo for personal names, school names, email addresses, tokens, credentials, IDs, and suspicious sample data.
3. Review the README.
4. Review all docs.
5. Review Docker and deployment-related files.
6. Review .gitignore and identify tracked files that appear inappropriate for a public repo.
7. Produce a written report only.

## Constraints

- Do not edit files.
- Do not sanitize anything yet.
- Do not delete anything yet.
- Do not commit or push.
- Do not introduce new dependencies.
- Do not guess. If something cannot be verified from the repository, say exactly what could not be verified.

## Output format

Produce the report with these sections:

1. Executive Summary
- One concise paragraph stating whether the repo is ready to go public.

2. Findings
- List concrete findings, ordered by severity.
- For each finding include:
  - severity: critical, high, medium, or low
  - what was found
  - why it matters for a public release
  - exact file references

3. Docker and Repo Structure Assessment
- State whether the Docker and Docker Compose files are in the right place.
- Distinguish file placement issues from documentation issues.

4. Documentation Assessment
- State whether docs are complete, current, and relevant.
- Identify which docs are public-facing, maintainer-facing, or internal-only.

5. Privacy and Anonymization Risks
- List all names, schools, emails, tokens, IDs, or sensitive examples that still appear to need review.

6. Source-Control Hygiene Risks
- List tracked files that should not be public or that need .gitignore attention.

7. Approval-Gated Next Steps
- Recommend a concrete cleanup plan, but do not make changes.
- Separate “safe doc cleanup” from “requires maintainer judgment.”

## Quality bar

The report must be professional, direct, and evidence-based.
Avoid generic advice.
Prefer precise findings such as “CLI documentation is stale because it omits the current check-config command” over broad statements like “docs may need updating.”
```
