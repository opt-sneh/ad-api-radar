# Ad API Radar — Claude vs Codex debate

Started 2026-09-29. Optmyzr repo read-only (branch `release`). No project code written.

---

## Round 1

### Claude — proposal (as sent to Codex, verbatim)

## Project: "API Upgrades on Autopilot" (Ad API Radar)

Brief: A new ad platform API version drops. By the time anyone looks, the breaks
are found and the fixes are ready. A radar reads upstream changes, traces each
one to the exact Optmyzr code it affects (including APIs that quietly return
empty data), agents draft fixes, and tests prove them.
Benchmark: replay our last Google Ads upgrade (v23 → v25, PR #2593, 650 files).
Question: can the next API upgrade be mostly done before anyone starts on it?

Platforms: Google Ads (primary), Microsoft Ads (stretch), Meta Ads (stretch).
Repo: C:\server\code\optmyzr (monorepo; C# backend in code/backend). READ-ONLY.

## What the current plan claims (verify, don't trust)

Codebase observations (release branch, 2026-09-29):
- Directory.Packages.props: Google.Ads.GoogleAds 26.1.0 (line 49),
  Google.Protobuf 3.31.1 (line 69), Microsoft.BingAds.SDK 13.0.28 (line 93).
- ~3,267 refs to Google.Ads.GoogleAds.V25 across ~645 files; ~114 refs to V8–V20
  (e.g. OptmyzrHelpers/ReportFetch/CustomClasses/GoogleAdsRowsListV12.cs,
  BulkMutateJobDaemon/Shopping/OldAPI/). Assumed dead code: UNVERIFIED.
- Meta version pinned as "v24.0" in OptmyzrHelpers/Social/PlatformVersions.cs,
  used by 5 call sites. FacebookSDK/FacebookClient.cs sends field names as raw strings.
- Commit 2c71e38ba55 removed campaign.video_brand_safety_suitability (removed from
  Google protos in v24) from 9 files, mostly field-name STRINGS, e.g.
  InternalProjects/internalutilities/GoogleAdsReportFields.cs and
  CQS.Shared/PlatformFieldsRegistry/Adwords/AdwordsAdFieldRegistry.cs.
- A throwaway regex scan found 18,421 field-like string refs in C#; 105 distinct
  ones don't exist in the v25 schema. Many look like false positives (Meta
  campaign.effective_status, SA360 customer.engine_id, internal campaign.intent).
  Possible real ones: segments.product_bidding_category_level1-5. UNVERIFIED.
- The repo already uses: net10.0 (86 projects), Roslyn Workspaces 4.11.0,
  Grpc.Tools 2.57.0, HtmlAgilityPack, MySql.Data, AWSSDK.S3, OpenAI 2.10.0
  (31 files), MSTest 4.0.2, Serilog.

Upstream sources (observed 2026-09-29):
- googleapis/googleapis → google/ads/googleads has folders v22–v25 only.
- NuGet: Google.Ads.GoogleAds latest 27.4.0; Microsoft.BingAds.SDK latest 13.0.30.
- Meta: facebook-business-sdk-codegen api_specs/specs/*.json (auto-generated field
  specs, last commit 2026-09-17).
- Google's sunset-dates and deprecations pages contain plain HTML tables that
  parse without a browser. The v25 protos have 7 `[deprecated = true]` markers.
- The deprecations table has a 2026-09-09 row saying developer tokens are
  deprecated and have been sunset. Impact on Optmyzr: UNKNOWN.

Input from Ramneek (who did the last upgrade): he gave Claude Google's migration
document and had it analyse the whole codebase. Protobuf and SDK versions must
be kept compatible. Microsoft likely follows the same approach; Meta is unknown.

## Proposed architecture

1. Collectors poll structured sources (protos, NuGet, Meta specs, sunset and
   deprecations HTML tables) and snapshot them.
2. Schema history keeps EVERY version, not just the last two.
3. Code index: Roslyn MSBuildWorkspace over the real .csproj files; every field
   string or property reference is tagged by platform.
4. Checks:
   a) Change diff, N vs N+1 (what's coming)
   b) Conformance, code vs LATEST schema (already broken, any age)
   c) Lifecycle: our version vs sunset dates
   d) Compatibility: SDK vs Google.Protobuf (.nuspec dependencies)
5. Lineage: for each missing ref, find the version that removed it, plus a doc link.
6. Optional AI: explain vague prose entries; draft a fix and test.
7. Verify: build and test in a separate git worktree.
8. Report: New / Upcoming / Long-standing (by age) / Unknown, persisted across runs.

Proposed stack: standalone .NET 10 solution (Radar.Core, Radar.Sources,
Radar.CodeIndex, Radar.Store, Radar.Cli, Radar.Tests), a CLI run by a GitHub
Actions schedule in optmyzr-pipeline, MySQL for findings, S3 for snapshots.
No dashboard yet.

### Codex — critique (verbatim)

- **Wrong/unverified facts found**
  - **VERIFIED** ([PlatformVersions.cs:11](C:/server/code/optmyzr/code/backend/OptmyzrProcessorService/OptmyzrHelpers/Social/PlatformVersions.cs:11)): Meta is pinned to `v24.0`. A repository search found **four direct references** to that constant, not the claimed five: [three OAuth helpers](C:/server/code/optmyzr/code/backend/OptmyzrProcessorService/ReportingFramework/FacebookOAuthHelper.cs:39) and [MetaCustomAudienceApplier.cs:353](C:/server/code/optmyzr/code/backend/OptmyzrProcessorService/SocialAdsManager/CustomAudience/MetaCustomAudience/MetaCustomAudienceApplier.cs:353). Indirect consumers remain unverified.
  - **VERIFIED** ([GoogleAdsRowsListV12.cs:35](C:/server/code/optmyzr/code/backend/OptmyzrProcessorService/OptmyzrHelpers/ReportFetch/CustomClasses/GoogleAdsRowsListV12.cs:35), [OptmyzrHelpers.csproj:11](C:/server/code/optmyzr/code/backend/OptmyzrProcessorService/OptmyzrHelpers/OptmyzrHelpers.csproj:11)): the cited V12 file references V12, but its project disables default compile inclusion and does not list that file. It is evidence that raw reference counts overstate upgrade work. Whether other old-version paths execute is **UNVERIFIED**.
  - **VERIFIED** ([Google's developer-token guide](https://developers.google.com/google-ads/api/docs/api-policy/developer-token)): Google says existing calls may keep sending the token. Servers ignore it, and existing code should keep working. Whether Optmyzr's Cloud project has the expected access is **UNVERIFIED**.
  - **UNVERIFIED**: the 3,267/645 and 18,421/105 scan totals, PR #2593's 650-file benchmark total, and the claim that `segments.product_bidding_category_level1–5` are broken Google Ads references were not reproduced. **VERIFIED** ([SA360 field reference](https://developers.google.com/search-ads/reporting/api/reference/fields/v0/customer), [file.cs:276](C:/server/code/optmyzr/code/backend/InternalProjects/internalutilities/file.cs:276)): those field names also exist in Search Ads 360, so their strings alone cannot establish a Google Ads finding.
  - The Codex log shows it verified the package pins and the nine-file removal commit `2c71e38ba55`. Its final answer doesn't list the pin lines individually, so treat that as partly verified.

- **Flaws and risks**
  - **VERIFIED** ([Google Ads field-service docs](https://developers.google.com/google-ads/api/docs/concepts/field-service)): GAQL validity depends on selectability and field compatibility, not just presence in a proto. A proto-name match is an incomplete conformance test.
  - **VERIFIED** ([v25 release notes](https://developers.google.com/google-ads/api/docs/release-notes)): the `customer_metrics` response change is behavioral. Schema diffs and a successful build cannot establish that downstream reports keep their meaning.
  - **UNVERIFIED**: a scheduled full-solution Roslyn scan, MySQL findings store, S3 snapshots, HTML collectors and a separate build worktree will save more upgrade effort than they consume. The plan gives no measured runner cost, precision target or maintenance budget.

- **Per-point rebuttals**
  - **Point 1 (UNVERIFIED):** "Beats migration doc + AI + manual" has no measured margin. Count correct, actionable findings and engineer minutes saved, including time spent dismissing alerts. The nine-file removal commit ([`2c71e38ba55`](C:/server/code/optmyzr/code/backend/InternalProjects/internalutilities/GoogleAdsReportFields.cs)) is a useful example, not proof of overall recall.
  - **Point 2 (UNVERIFIED):** deterministic indexing may give repeatable findings, but an AI-first pass can cover prose and cross-file intent sooner. The case for building the index first depends on a blinded head-to-head result.
  - **Point 5 (VERIFIED)** ([v25 release notes](https://developers.google.com/google-ads/api/docs/release-notes), [testing guidance](C:/server/code/optmyzr/instructions/test/testing.md)): release notes contain behavior changes, and repository guidance calls for real account data and empty-account cases. "Detects quiet empty data" is overclaimed without comparable live-data workflow checks. Demo it as a flagged behavior risk plus a tested fixture, with unknowns stated.
  - **Points 3–4 and 6–9 (UNVERIFIED):** production .NET infrastructure, full Roslyn coverage, multi-platform scope and durable storage are premature until the replay establishes precision. Start with platform-tagged call paths and generated GAQL candidates. Label dynamically built queries and unexecuted code "unknown." Freeze the pre-upgrade code and inputs before scoring PR #2593, to limit leakage.

- **Better alternative (UNVERIFIED, proposed experiment)**
  - Run a Google-only, read-only prototype against a frozen pre-v25 revision.
  - Give one team the migration docs and AI baseline. Give the other the same inputs plus a small radar that checks package/API-version lifecycle, extracts obvious GAQL and typed references, and links each finding to official docs.
  - Keep snapshots and findings as local artifacts.
  - Score both against a separately curated set of PR changes and missed issues. Metrics: actionable recall, precision, reviewer minutes, build failures, and one real-data "unexpected zero rows" workflow.
  - Add Roslyn, persistent history, CI scheduling or a second platform only where the measured misses justify them.

Codex thread: 01a0ec92-670a-7f80-b646-c193a9410522. Codex made no edits (read-only commands and `git show`/`git log` only).

### Claude — reply to Codex round 1

Evidence was gathered with read-only `grep`, `git show` and `curl` on 2026-09-29. Paths are relative to `code/backend/`.

**Facts**
- **F1 Meta references: DEFEND (minor).** There are 5 references in 4 files: `ReportingFramework/FacebookOAuthHelper.cs:39`, `FacebookPagesOAuthHelper.cs:25`, `InstagramOAuthHelper.cs:28`, and `SocialAdsManager/.../MetaCustomAudienceApplier.cs:353` **and :420**. Codex counted files. Neither count matters to the design.
- **F2 V12 file not compiled: CONCEDE.** `OptmyzrHelpers/OptmyzrHelpers.csproj:11` sets `EnableDefaultCompileItems=false`, and `GoogleAdsRowsListV12` has 0 matches in that csproj. `BulkMutateJobDaemon.csproj` has 0 `OldAPI` matches. So raw namespace counts (3,267 / ~114) overstate the work and are dropped from the plan.
- **F3 Developer token: CONCEDE.** The Google guide says: "You can continue sending developer tokens … optional and ignored … existing code will continue working." We still send one (`NewGoogleAdsApiOperations/AuthHelpers.cs`). Risk downgraded to "check Cloud project access" (UNVERIFIED).
- **F4 product_bidding_category: CONCEDE (my candidate was wrong), with a stronger finding.**
  - Every runtime hit is **commented out**, for example `Optimizations/Apply/KeywordLasso.cs:374` (`// … removed in v13`) and `Report_Field_Value_Getter.cs:2333` (`// "…product_bidding_category…"`).
  - `file.cs:276` defines the legacy `ACCOUNT_PERFORMANCE_REPORT.segments_product_bidding_category_level1`, and that constant has **0 usages**.
  - v25 `segments.proto:347` has `product_category_level1`.
  - So my regex scan counted comments and unused constants. The **105 figure is not trustworthy** and is withdrawn.
- **F5 SA360 overlap: DEFEND (already in the plan).** The overlap is exactly why platform tagging exists. `SearchAds360ReportFields.cs` is compiled alongside `GoogleAdsReportFields.cs` (`OptmyzrHelpers.csproj:54,57`).

**Flaws**
- **Selectability (field service): CONCEDE.** Name presence ≠ a valid GAQL query. A selectability check needs API access to `GoogleAdsFieldService`. **UNRESOLVED:** can the tool get read-only credentials? Until then, label conformance "presence only".
- **customer_metrics: partly DEFEND.**
  - The v25 release-notes row is labelled "Type change / behavioral shift". A **type change is visible in a proto diff**, and the change-type column is structured, so "behavioral shift" rows can be flagged mechanically.
  - Optmyzr has **0 files** using `GenerateBenchmarksMetrics` or `BenchmarksService`, so the correct radar output is "no impact". That's noise the "document + AI" baseline has to read through by hand.
  - I concede that a build or diff can't prove report meaning is unchanged.
- **Infrastructure cost: CONCEDE.** MySQL, S3, GitHub Actions and the worktree verifier are removed from the hackathon. Use local JSON artifacts.

**Points**
- **P1 and P2 (baseline, AI-first): CONCEDE on method.** Adopt Codex's blinded head-to-head: *document + LLM* vs *document + LLM + radar findings*, scored on a curated answer key. No margin is claimed until it's measured.
- **P3 (Python vs C#): DEFEND C#, but not "production grade" yet.** Three false-positive classes found today need MSBuild evaluation plus syntax trees:
  1. Files that aren't compiled (V12, `OldAPI`).
  2. Commented-out references.
  3. **Linked files.** `internalutilities/GoogleAdsReportFields.cs`, `file.cs` and `SearchAds360ReportFields.cs` live under `InternalProjects/` but are compiled into runtime `OptmyzrHelpers` via `<Compile Include="..\..\InternalProjects\…"><Link>` (`OptmyzrHelpers.csproj:46,54,57`). A folder-based "skip internal tools" heuristic would silently drop the file that held the brand_safety field.
- **P4 (Roslyn vs string search): DEFEND.** Queries are composed from typed constants, for example `GOOGLE_ADS_REPORT.campaign_name` in `SmartCpnOptimization/Data_Operations/Cpn_Report_Operations.cs:25`, used across **128 files**. A string search finds only the one definition. Roslyn `FindReferences` finds the features that use it, which is the actual impact. Also, `"SELECT " + parameters` in `AccountHealthEmailProcessor/PostOptimization/PostOptimizationHelpers.cs:95` is **MySQL, not GAQL**, so a text search would misclassify it. Anything still dynamic is labelled "unknown".
- **P5 (quiet empty data): CONCEDE on the claim.** The demo won't claim to detect it. It will list "behavioral shift" entries plus Optmyzr usage, as runtime checks still needed. One fixture test is optional.
- **P6 (precision):** target ≥ 70% actionable in the hackathon (UNVERIFIED as the right bar). Every finding shows evidence and can be dismissed.
- **P7 (leakage): CONCEDE.** Freeze the pre-`2c71e38ba55` and pre-#2593 revisions and upstream snapshots. Someone other than the tool author builds the answer key from the PR diff, and it stays hidden while developing.
- **P8 (scope): CONCEDE.** Google only.
- **P9 (operations):** ops risk is reduced by dropping the infrastructure. Old versions: `googleapis` master has only v22–v25, so git history is needed for v23 → v24 (v23 exists now, so the benchmark is safe).

**Revised plan:** a Google-only .NET CLI that works from local files only. It has four parts:
- **Snapshotter:** protos, plus the sunset, deprecations and release-notes tables
- **MSBuild + Roslyn index:** compiled files only, no comments, constants resolved, tagged by platform
- **Checks:** change diff, conformance (presence only), lifecycle, compatibility
- **JSON and HTML report**

It's evaluated by a blinded head-to-head against the document + LLM baseline. The AI fix-drafting step is a stretch goal.

---

## Round 2

### Codex — critique (verbatim)

- **F1 — AGREE, VERIFIED.** I missed the second use in MetaCustomAudienceApplier.cs:420. The constant has five direct references in four files: two in that file, plus FacebookOAuthHelper.cs:39, FacebookPagesOAuthHelper.cs:25 and InstagramOAuthHelper.cs:28 (all under OptmyzrProcessorService/ReportingFramework/, except the applier under SocialAdsManager/CustomAudience/MetaCustomAudience/). **Settled.**

- **F5 — AGREE, VERIFIED.** OptmyzrHelpers.csproj:46,54,57 explicitly includes file.cs, GoogleAdsReportFields.cs and SearchAds360ReportFields.cs as linked compile items. Folder-only platform tagging would misclassify these.
  - **Unresolved:** co-compilation does not tell you which platform a given use belongs to. Each use still needs its own label.
  - **What settles it:** a reviewed sample of resolved uses.

- **customer_metrics — PARTLY AGREE.**
  - VERIFIED: a read-only search found no GenerateBenchmarksMetrics or BenchmarksService text in code/backend. Google's v25 release notes label the change "Type change / behavioral shift" (https://developers.google.com/google-ads/api/docs/release-notes).
  - **DISAGREE, UNVERIFIED:** "no impact" is stronger than "no direct textual use", and a proto type diff does not prove downstream behavior. Report "no direct use found" until resolved call paths or service-call telemetry say otherwise.

- **P3 — AGREE on the three examples, VERIFIED.**
  - OptmyzrHelpers.csproj:11 disables default compile items.
  - KeywordLasso.cs:374 holds a commented-out reference.
  - The linked files above are compiled.
  - **DISAGREE:** these need evaluated Compile items plus parsed syntax trees. They do not need MSBuildWorkspace or a full solution compilation. This is my reasoning from the MSBuild and Roslyn docs; I did not test a lighter evaluator against the workspace.
  - **What settles it:** compare a lighter evaluator/parser against workspace output on the same projects.

- **P4 — AGREE on the pattern, VERIFIED.**
  - Cpn_Report_Operations.cs:25 uses GOOGLE_ADS_REPORT.campaign_name. Its definition is at InternalProjects/internalutilities/GoogleAdsReportFields.cs:1887.
  - PostOptimizationHelpers.cs:95 is a MySQL command, not GAQL.
  - **DISAGREE, VERIFIED:** campaign_name is a `public static` object initialized with "campaign.name". It is not a C# constant. Roslyn's constant-value API alone will not resolve the mapping, so the plan needs custom resolution of the initializer.
  - Text search finds uses, syntax parsing strips comments, and semantic references may improve disambiguation.
  - **What settles it:** measure misses and false matches on a set of representative fields.

- **MSBuildWorkspace feasibility — UNVERIFIED.** I ran no workspace load because this was read-only.
  - The main solution (OptmyzrProcessorService.sln:13) lists 63 projects, including the relevant net10.0 helpers. The old-style .NET Framework projects I inspected are outside it.
  - The wider tree does contain an old-style v4.7.2 project (AdWordsEditorAutoRunner/FesticketsAutoRun.csproj:12), a netcoreapp2.1 project (Lambda/OptmyzrLambdaFunctions/TestApp/TestApp.csproj:5) and a net6.0 project (DashboardSuggestionGenerator.csproj:5).
  - Microsoft.CodeAnalysis.Workspaces.MSBuild is not referenced in any of the 119 project files I inspected. It is a separate NuGet package.
  - **What settles it:** an early, timed spike that loads only the Google-related projects. Record diagnostics, missing documents, restore needs and memory. Fail closed if a project loads only partially.

- **70% precision bar — DISAGREE, UNVERIFIED.**
  - 70% allows three false alerts in ten. It also says nothing about recall or review time.
  - **What settles it:** in the blinded replay, record precision, recall, false alerts, and minutes spent per high-priority finding.

- **Head-to-head in 1–2 weeks with one developer — UNVERIFIED.**
  - One developer can build a prototype in that time.
  - The promised non-author answer key needs a second reviewer, and PR #2593's diff records the fixes made, not necessarily every real issue.
  - **What settles it:** assign the reviewer before implementation starts, and fix the answer-key procedure and time budget in advance.

- **Still unresolved and important (all UNVERIFIED):**
  - Whether project evaluation changes with build configuration or platform.
  - Whether generated or dynamically built GAQL escapes the index.
  - Whether "latest-schema presence" flags code that is valid on its currently supported API version (https://developers.google.com/google-ads/api/docs/sunset-dates).
  - Whether a report-only demo, with no fix or test evidence, answers the original question of whether the work is mostly done before anyone starts.

### Claude — reply to Codex round 2

Extra evidence (read-only): `GoogleAdsReportFields.cs:1887` reads `public static GOOGLE_ADS_REPORT campaign_name = new GOOGLE_ADS_REPORT("campaign.name");`. `SearchAds360ReportFields.cs:15` reads `public class SA360_REPORT_FIELDS`, with `customer_engine_id = new SA360_REPORT_FIELDS("customer.engine_id")` at :1132, and it's used as `SA360_REPORT_FIELDS.segments_device` in `OptmyzrHelpers/SA360/Sa360CustomClasses.cs:62`.

- **customer_metrics wording: CONCEDE.** The report says "no direct use found", never "no impact".
- **P3 lighter evaluator: CONCEDE.** Use MSBuild project evaluation (`Microsoft.Build`) to get the Compile items, including links, plus `CSharpSyntaxTree` parsing. Skip MSBuildWorkspace and semantic compilation. The evidence above shows the main pattern is syntax-resolvable:
  - **Definition:** `new GOOGLE_ADS_REPORT("…")` gives the name → GAQL string mapping from the initializer.
  - **Usage:** the member access `GOOGLE_ADS_REPORT.x` gives the call sites.
  - **Platform:** the owning class name (`GOOGLE_ADS_REPORT` vs `SA360_REPORT_FIELDS`) gives a **per-use** platform label, which also answers F5's per-use concern.

  Anything that doesn't match these patterns is labelled "unknown". A semantic model is added only if the replay shows misses that need it.
- **P4 constants: CONCEDE** that constant-value APIs don't apply. The initializer resolution above replaces them.
- **MSBuildWorkspace spike:** moot under the lighter approach. Day 1 replaces it with a spike that checks evaluated Compile items against the csproj files for the Google projects, and fails closed on evaluation errors.
- **70% bar: CONCEDE.** There's no fixed precision bar. Pre-register the metrics (precision, recall against the answer key, false alerts, reviewer minutes per high-priority finding) and a single decision rule: "radar + LLM beats LLM-only on recall **and** reviewer minutes".
- **Answer key: CONCEDE; needs a person.** A second reviewer must be assigned before implementation starts. The key covers the PR diff plus any issues the reviewer finds; its limits are stated.
- **Latest-schema presence: CONCEDE and fixed.** Conformance compares code against **the version the code targets** (V25 today, from the `using` namespaces). "Newer version" differences are reported as *Upcoming*, not *Broken*.
- **Report-only demo: partly CONCEDE.** For the benchmark's main class, removed fields, fix drafting is **in scope**: remove or replace the field reference, then compile only the affected project from a copy. Behaviour changes stay as flagged runtime checks.
- **Build configuration and dynamic GAQL: UNRESOLVED.** Both are settled only by measuring during the replay; more debate won't settle them.

**Stop decision:** after round 2, the remaining open items are people and process (the reviewer, field-service credentials) or can only be measured in the replay. None can be settled by a third round, so the debate stops here, as allowed by the rules.

---

## Final decision

### Chosen approach
A **Google-only, local-first .NET 10 CLI**. It snapshots Google's structured sources, indexes **only compiled** Optmyzr C# code using MSBuild project evaluation and Roslyn syntax trees, and runs four deterministic checks:
- change diff
- conformance against the targeted version
- lifecycle
- SDK ↔ protobuf compatibility

It writes a JSON and HTML report where every finding has evidence. For removed fields, an LLM drafts the removal and the affected project is compiled from a copy. Value is judged by a **blinded head-to-head** on the frozen v23 → v25 replay: migration document + LLM vs the same + radar findings. No MySQL, S3, GitHub Actions, dashboard or second platform until the benchmark shows value.

Components:
1. **Snapshotter:** googleapis protos (from git history for old versions); Google sunset, deprecations and release-notes HTML tables; NuGet `.nuspec` dependencies. Saved as local files.
2. **Schema model:** fields, enums, types, `[deprecated = true]`, per version.
3. **Code index:** MSBuild-evaluated Compile items (including linked files), then syntax trees with comments ignored. Maps `new GOOGLE_ADS_REPORT("…")` to its GAQL name, finds `GOOGLE_ADS_REPORT.x` uses, and tags platform by owning class. Everything else is labelled "unknown".
4. **Checks:** change (N → N+1), conformance (vs the targeted version, presence only), lifecycle (sunset table), compatibility (SDK ↔ `Google.Protobuf`), plus a list of "behavioral shift" rows with any direct Optmyzr use.
5. **Report:** JSON and HTML, grouped into Broken, Upcoming, Behaviour-check-needed and Unknown, with evidence links and a "not checked" section.
6. **Fix drafter (removed fields only):** an LLM patch, compiled from a copy of the affected project.
7. **Evaluation harness:** frozen revisions and snapshots, a hidden answer key, and pre-registered metrics.

### Which ideas came from whom
- **Claude:**
  - four checks, including conformance for old breaks and lineage
  - structured sources instead of doc scraping (sunset, deprecations and release-notes tables; proto `deprecated` markers)
  - per-use platform tagging
  - the linked-file evidence (`OptmyzrHelpers.csproj:46,54,57`) for why folder heuristics fail
  - class-name platform tagging (`GOOGLE_ADS_REPORT` vs `SA360_REPORT_FIELDS`)
  - fix drafting limited to removed fields
- **Codex:**
  - the blinded head-to-head against the "document + LLM" baseline
  - dropping infrastructure for the hackathon
  - selectability ≠ presence (field service)
  - "no direct use found" wording
  - evaluator + syntax trees instead of MSBuildWorkspace
  - custom initializer resolution, because the fields are static objects, not constants
  - no fixed 70% bar
  - the non-author answer key and its limits
  - conformance against the targeted version
  - checking whether a report-only demo answers the brief
- **Found together during verification:** the 105-reference scan was invalid (comments and unused constants); the V12 and `OldAPI` files aren't compiled; the developer-token sunset is low risk.

### Alternatives rejected
- **Full-scale production build now (MySQL, S3, Actions, worktree verifier):** its cost can't be justified until the benchmark shows value (Codex).
- **Python regex prototype:** demonstrably wrong on this repo. It counted comments, uncompiled files and unused constants, and it can't read csproj links.
- **MSBuildWorkspace + semantic model:** heavier and harder to load; not needed for the dominant pattern. Kept as a fallback if the replay shows misses.
- **AI-first only (document + LLM):** this is the *baseline* being measured against, not rejected outright. The head-to-head will show whether the radar adds value.
- **Multi-platform:** Microsoft and Meta add sources without benchmark evidence.

### Facts
**Verified:**
- Package versions: `Directory.Packages.props:49,69,93`.
- Meta constant: 5 references in 4 files.
- Uncompiled files: the V12 file and `OldAPI` (`OptmyzrHelpers.csproj:11`, no entries).
- Linked compile items: `OptmyzrHelpers.csproj:46,54,57`.
- Static field objects: `GoogleAdsReportFields.cs:1887`.
- SA360 class: `SearchAds360ReportFields.cs:15,1132`.
- Commented-out product_bidding references: `KeywordLasso.cs:374`, `Report_Field_Value_Getter.cs:2333`.
- MySQL `SELECT`, not GAQL: `PostOptimizationHelpers.cs:95`.
- No Benchmarks service usage.
- Upstream sources:
  - The brand_safety field is in v23 and absent in v24.
  - googleapis master has v22–v25.
  - Sunset and deprecations tables parse as HTML.
  - The developer token is ignored by Google (official guide).

**Unverified:**
- the true number of long-standing broken references
- how many GAQL queries are dynamically built
- MSBuild evaluation behaviour across configurations
- whether field-service credentials are available
- Cloud-project API access after the token change
- PR #2593's 650-file total (not re-counted)
- whether the head-to-head fits in the time available

### Still unresolved
- **Reviewer:** who builds the answer key (needs a person).
- **Selectability:** read-only `GoogleAdsFieldService` credentials; conformance is presence-only until then.
- **Coverage:** dynamic or generated GAQL coverage (measured in the replay).
- **Build configuration** effects on the compile set (measured on day 1).

### Key risks → mitigations
- **False positives erode trust** → per-use tags, compiled-only scan, "unknown" bucket, evidence on every finding, precision reported.
- **Overfitting to PR #2593** → frozen inputs, hidden key built by a non-author, pre-registered metrics.
- **Overclaiming behaviour detection** → behaviour-change entries listed as "runtime check needed"; nothing claimed about empty data.
- **Upstream format changes / old versions removed** → local snapshots, git history for protos, fail-closed parsing that marks the source as stale.
- **Evaluation fails on some projects** → day-1 spike; fail closed and list what was skipped.

### Honest demo claim
- **Can claim:** "On a frozen replay of our v23 → v25 upgrade, the radar automatically found X of Y removed or changed fields that our compiled code referenced. It showed each one with official evidence and file:line, drafted compiling fixes for Z of them, and changed LLM-only recall and reviewer time from A → B."
- **Must NOT claim:**
  - detecting quiet empty data or behaviour changes
  - completeness ("no findings = safe")
  - Microsoft or Meta support
  - production readiness
  - any margin not measured

### Success metrics (pre-registered)
- **Recall:** against the hidden answer key.
- **Precision:** actionable findings ÷ total findings.
- **False alerts:** count.
- **Reviewer minutes:** per high-priority finding.
- **Fixes:** drafted fixes that compile ÷ attempted.
- **Decision rule:** radar + LLM beats LLM-only on recall **and** reviewer minutes.

### First 5 tasks (one day each)
1. **Spike:** load the Google-related projects with MSBuild evaluation, list Compile items (including links), and diff against the csproj files. Record errors and time. Assign the answer-key reviewer and freeze the benchmark revisions (the commit before `2c71e38ba55`, the parent of PR #2593).
2. **Snapshotter:** fetch v23, v24 and v25 protos from googleapis git history, the sunset, deprecations and release-notes tables, and the `.nuspec` dependencies into a local folder.
3. **Schema model and change diff:** v23 → v24 must list `campaign.video_brand_safety_suitability`. This is the first MSTest test.
4. **Code index:** syntax-parse the compiled files, resolve `new GOOGLE_ADS_REPORT("…")` names, find uses, tag platforms, and exclude comments. Check it finds the 9 brand_safety files on the frozen revision.
5. **Checks and report:** conformance against the targeted version, lifecycle and compatibility, output as JSON and HTML with evidence. Then run the LLM-only baseline on the same frozen inputs.

**Status:** approved by the user on 2026-09-29. No project code has been written yet.
