# Unit Test Audit & Fix Prompt

You are an expert .NET software engineer. Your task is to audit and then fix the unit tests for the `TalkingPointsSummary` project. Work in two explicit phases. Do not skip ahead — complete Phase 1 before starting Phase 2.

---

## Context

**Tech stack:** .NET 10, xUnit 2.9.3, FluentAssertions 8.8.0, Moq 4.20.72, EF Core 10 InMemory, C# 13.

**Test project:** `tests/TalkingPointsSummary.Tests/` — references the main app project directly.

**Guiding principles:**
- Test **your code only**. Do not test 3rd-party libraries (Markdig, MailKit, EF Core, Anthropic SDK, etc.).
- **Unit tests only** — no real HTTP calls, no real DB, no real SMTP. Mock everything external.
- **No interfaces exist** in this codebase. Services take `HttpClient` directly. Where mocking is needed, use `HttpMessageHandler` subclassing (Moq-compatible fake handler). Do **not** introduce interfaces unless you have verified they are the right solution — ask yourself whether the test can be structured around `HttpMessageHandler` first.
- Pragmatic coverage: the meaningful execution paths in your own logic. Not every edge case. Not trivial property-bag DTOs.
- EF Core InMemory is acceptable for data-access logic in `MessageDeduplicator` (it is the right tool there). But it is **not** a substitute for mocking higher-level orchestration dependencies.
- Use `NullLogger<T>` or `Mock<ILogger<T>>` for all logger dependencies.

---

## Phase 1 — Audit

Read ALL of the following files before writing a single line of test code:

**Source:**
- `src/TalkingPointsSummary/Services/` — every `.cs` file
- `src/TalkingPointsSummary/Pipeline/PipelineOrchestrator.cs`
- `src/TalkingPointsSummary/Pipeline/WeeklyPipelineService.cs`
- `src/TalkingPointsSummary.Core/Models/` — all models
- `src/TalkingPointsSummary.Core/Data/AppDbContext.cs`

**Tests:**
- `tests/TalkingPointsSummary.Tests/` — every `.cs` file

Then produce a **written audit report** (in your response, before writing any code) that lists, for each existing test file:
1. What it actually tests (be honest — "tests DTO property assignment" counts)
2. What it should test
3. Whether it should be deleted, rewritten, or extended
4. Specific problems: duplicate logic, tests that shadow production code, tests of 3rd-party internals, missing mock wiring, hardcoded assumptions

Then list all **untested production code** that has real, non-trivial logic worth covering.

---

## Phase 2 — Fix

After the audit, implement the fixes. Follow these specific rules:

### Delete or gut these tests — they test nothing useful

- **`MarkdownConverterTests`** — all tests verify Markdig rendering rules, not your code. Either delete the file or replace with a test that verifies your `ToHtml` wrapper returns a non-empty string for non-empty input.
- **`TalkingPointsApiClientTests`** — all tests are DTO property assignments. Delete them and replace with real tests (see below).
- **`PipelineOrchestratorTests`** — the class never exercises `PipelineOrchestrator`. Delete the duplicate `MessageDeduplicator` tests. Replace with actual orchestrator tests (see below).
- **`WeeklyPipelineServiceTests`** — the `ShouldRun` method is duplicated verbatim inside the test class instead of calling the real class. This test can never catch a regression. Fix it to test the actual `ShouldRun` logic. Use `InternalsVisibleTo` or make `ShouldRun` `internal` to enable direct testing without reflection.
- **`MessageCategorizerTests`** — the existing tests duplicate the `[GeneratedRegex]` pattern in the test body instead of calling through `CategorizeAsync`. Delete the regex duplication tests. Add real tests (see below).

### Rewrite or extend these

- **`MessageDeduplicatorTests`** — add: cross-parent isolation test (messages from other parents are not returned by `GetUnprocessedAsync`), ordering-by-`SentAt` test, field mapping completeness test (verify `StudentName`, `FromName` are mapped correctly from `TalkingPointsMessage`).
- **`SummaryPromptBuilderTests`** — add: explicit test for `BuildPreviousSummaries` returning `"None"` when list is empty, test for empty `children` list, test that all 7 template tokens are replaced (none remain as `{{...}}` in output).
- **`MessageCategorizationPromptBuilderTests`** — add: test for special characters in `MessageText` (angle brackets, ampersands — verify they are not escaped in the prompt since it's going to an LLM, not HTML), test that `{{DATE_SENT}}` is formatted with ISO 8601 round-trip format.

### Write new tests for these (currently zero coverage)

#### `TalkingPointsApiClient.FetchMessagesAsync`
- Use a custom `HttpMessageHandler` (via Moq or a simple stub) to return mocked JSON responses.
- Test: correct URL and all required headers (`x-token`, `x-contactid`, `x-app-version`, `x-language`, `x-mobile-platform`) are sent on the request.
- Test: successful response is deserialized and returned correctly, including `StudentName` from `ContactInfo` and `FromName` from `From.User.Signature`.
- Test: when API returns empty `data.messages`, returns empty list (not null, not exception).
- Test: when API returns non-success status, the method throws (via `EnsureSuccessStatusCode`).

#### `MessageCategorizer.CategorizeAsync`
- Use a `HttpMessageHandler` stub that returns a hardcoded Anthropic-format JSON response.
- Test: successful categorization — verify the returned `CategorizationResult` has the correct field values.
- Test: JSON wrapped in markdown code fences (`` ```json ... ``` ``) is correctly stripped and parsed.
- Test: malformed JSON response triggers the fallback `CategorizationResult` (`IsNewsItself=true`, `HasNewsletterUrl=false`, `Summary="Unable to categorize"`).

#### `NewsletterScraper.ScrapeAsync`
- Use a `HttpMessageHandler` stub.
- Test: correctly extracts text from the nested JSON path `data[0].results[0].text`.
- Test: when the HTTP call throws, method returns `null` (not re-throws).
- Test: when the response JSON has unexpected structure (missing `data` array), method returns `null`.

#### `PipelineOrchestrator.RunAsync`
- This class has 8 concrete dependencies, none with interfaces. To test it properly, you need `Mock`-able seams. The right approach here is to introduce interfaces for the five service dependencies that have side effects: `ITalkingPointsApiClient`, `IMessageDeduplicator`, `IMessageCategorizer`, `INewsletterScraper`, `ISummaryGenerator`. `MarkdownConverter` and `EmailSender` may also need interfaces for orchestrator-level testing. Verify this design is the minimum needed — do not over-engineer. Register the interfaces in `Program.cs` and update the existing constructor injections.
- Test: when `SummaryGenerator` returns `null`, the pipeline returns early without calling `EmailSender` or saving a `Summary`.
- Test: when one message's `ProcessMessageAsync` throws, the exception is caught and logged, and the pipeline continues to process remaining messages.
- Test: when a message has `HasNewsletterUrl=true` and the scraper returns scraped text, the saved `NewsItem` has `SourceType=NewsletterUrl`.
- Test: when a message has `HasNewsletterUrl=true` but the scraper returns `null`, the saved `NewsItem` falls back to `SourceType=MessageText`.
- Test: when a message `IsNewsItself=true`, a `NewsItem` with `SourceType=MessageText` is saved.
- Test: the full happy path saves a `Summary` entity and calls `EmailSender.SendAsync`.

#### `WeeklyPipelineService`
- Fix `ShouldRun` tests to call the real method (make it `internal` and add `[assembly: InternalsVisibleTo("TalkingPointsSummary.Tests")]` to the main project).
- Test: `TryRunFullPipelineAsync` returns `AlreadyRunning` when called concurrently (acquire semaphore in one task, call again while held).
- Test: `TryRunFullPipelineAsync` returns `ParentNotFound` when no matching active parent exists in the DB.
- Test: `IsRunInProgress` is `true` while a run is in progress and `false` after it completes.

---

## Constraints

- Do **not** rename or move existing passing tests unless they are being replaced with better ones.
- Do **not** test `CommandHandler` — it is CLI wiring with no unit-testable logic.
- Do **not** test `StartupValidator` — it is an integration-only class by design.
- Do **not** test `AppDbContext` — it is EF Core configuration.
- Do **not** test `Program.cs` or `AppHost`.
- All tests must pass with `dotnet test` after your changes.
- After implementing all changes, run the tests and fix any failures before stopping.
