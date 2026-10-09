# Application feature baseline

Scope: application behavior at version **0.29.0**. **Status: existing-feature inventory with documented revisions below.** Features below are implemented unless explicitly marked **Planned**; planned entries remain future scope until requested.

This document is the development baseline and takes precedence over conflicting roadmap or spec scope. Detailed contracts remain in the linked documentation.

Before implementing any feature or change, including fixes, refactoring, configuration, infrastructure or removals:

1. Update the affected requirements and interactions here, identifying the requested change and its implementation status.
2. Record the revision date and user request reference. The user’s request authorizes its scope; no separate baseline approval is required. Retain historical approval records.
3. Implement only the requested scope, keep supporting specs/docs aligned, and verify against the documented requirements. Document scope changes before implementing them.

Baseline process revision dated **2026-10-09**: remove the separate explicit approval gate while retaining required baseline updates before implementation. User request reference: **“Remove the explicit approval. Just make sure it is added to the document. Approved”**. Historical approval records below remain intact.

Revision **workflow-management-layout**, dated **2026-10-09**: **Implemented in 0.27.0**. Fix output-port label overlap in running-workflow graphs, reorganize Workflow Management around a compact execution browser and adjacent execution detail, and relocate Manual Start into the selected workflow definition's panel. Approver: **Product owner (conversation user)**. Approval date: **2026-10-09**. Approval reference: user message **“Remove the explicit approval. Just make sure it is added to the document. Approved”**.

Revision **workflow-editor-groups**, dated **2026-10-09**: **Approved; implemented in 0.28.0**. Add named visual groups to the workflow editor with multi-node membership, dragging all members together, editable names displayed on the canvas, and background colors selected from a preset palette. Requirements and interactions are specified under Definitions and visual editor below. Approver: **Product owner (conversation user)**. Approval date: **2026-10-09**. Approval reference: user message **“Approved”** responding to the drafted group requirements.

Revision **variable-ports-and-control-cycles**, dated **2026-10-09**: **Approved for implementation**. User request reference: **“I cant connect multiple outputs to a variable. Out parameters should show their type when hovering. Sequential graph contains an undeclared cycle at step 'step1-2'. We should allow cycles”**. Reported failing parameter clarified by the product owner: **GitHub Issue commented → body (string)**. Cycle behavior confirmed by the product owner: **“Just allow them. When someone wants to build infinite loops, let them.”** Approver: **Product owner (conversation user)**. Approval date: **2026-10-09**. Approval reference: user message **“Approved”** following the revised requirements and comment-body reproduction. This revision supersedes control-cycle rejection for its scope; all historical approval records remain intact.

Revision **typed-data-variables**, dated **2026-10-09**: **Approved; implemented in 0.29.0**. Supersedes the unapproved `multiple-output-input-connections` draft. Introduce typed, data-only variable nodes with explicit combination rules instead of adding implicit aggregation to task inputs. Approver: **Product owner (conversation user)**. Approval date: **2026-10-09**. Approval reference: user message **“Approved”** responding to the revised typed-variable baseline.

Revision **ai-setup-default-model-dropdown**, dated **2026-10-09**: **Approved; implemented in 0.26.3**. Replace AI Setup's Default Model text input with a dropdown consistent with the existing model selector. Reuse CLI model discovery for supported saved configurations, display discovered model names and CLI defaults, retain an existing saved model as an option, and allow an unset configuration default. Explain when discovery requires saving the configuration or is unsupported, and expose discovery progress and failures. Approver: **Product owner (conversation user)**. Approval date: **2026-10-09**. Approval reference: user message **“Approved”** responding to the drafted revision approval request.

Revision **identity-provider-restart-notice**, dated **2026-10-09**: **Approved; implemented in 0.26.2**. Correct the GitHub identity-provider restart notice to reflect whether the running application's login configuration has applied the saved integration settings. Approver: **Product owner (conversation user)**. Approval date: **2026-10-09**. Approval reference: user response **“Ok”** to the drafted revision approval request.

Release integration for **identity-provider-restart-notice**, dated **2026-10-09**: **Approved** by the product owner (conversation user) under the standing release rule and explicit main/version ownership handoff instructing this workspace to fetch latest main and proceed with the next unique patch, **0.26.2**. Preserve deployed **0.26.1**, align application/chart/image versions and release/deployment documentation, push main and verify automated deployment. Implementation scope is unchanged.

Revision **codex-device-code-extraction**, dated **2026-10-09**: **Approved; implemented in 0.26.1**. Repair connect/reconnect device-code extraction under AI configuration and authentication. Decode worker log envelopes before removing terminal formatting, then extract the server-provided one-time code from the Codex login prompt without assuming a four-character/five-character split. Continue exposing the device login URL and code while a login job is running and in its final status; unrelated log text must not be mistaken for a code. Approver: **Product owner (conversation user)**. Approval date: **2026-10-09**. Approval reference: user message **“Ok”** responding to the proposed baseline revision.

Release integration for **codex-device-code-extraction**, dated **2026-10-09**: **Approved** by the product owner (conversation user), reference **“Always do that make sure the version is updated.”** Release as **0.26.1**, the next patch after latest main **0.26.0**, align application/chart/image versions and release/deployment documentation, merge latest main, push main and verify automated deployment. This instruction also authorizes recording the standing repository rule to complete merge/version/push/deployment for approved work unless the user explicitly limits the release scope. Implementation scope is unchanged.

Revision **kubernetes-e2e-image-import**, dated **2026-10-09**: **Approved for implementation**. Repair the local Kubernetes E2E image-import timeout and diagnostics under Deployment, diagnostics and future operations. Approver: **Product owner (conversation user)**. Approval date: **2026-10-09**. Approval reference: user response **“Go ahead”** to the proposed revision approval question.

Revision **agent-task-output-correction**, dated **2026-10-09**: **Approved for implementation**. Covers the agent/custom-task output-contract and correction requirements below. Approver: **Product owner (conversation user)**. Approval date: **2026-10-09**. Approval reference: user message **“lgtm”** following the proposed baseline revision `agent-task-output-correction`.

Release integration for **agent-task-output-correction**, dated **2026-10-09**: **Approved** by the product owner (conversation user), reference **“Merge main, up the version if needed and push to main”**. Preserve the latest main features and release output correction as **0.26.0**, following main release **0.25.0**.

Revision **github-issue-created-output**, dated **2026-10-09**: **Approved for implementation** for the original `issue` JSON-string output scope. Approver: **Product owner (conversation user)**. Approval date: **2026-10-09**. Approval reference: user message **“Looks good so far”** responding to the original revision summary. The expanded requirements under revision `github-issue-comment-task` below are separately approved.

Revision **github-issue-comment-task**, dated **2026-10-09**: **Approved for implementation**. Extends GitHub Issue created with an Issue id output and introduces an Add issue comment task as specified below. Approver: **Product owner (conversation user)**. Approval date: **2026-10-09**. Approval reference: user message **“lgtm”** following the expanded revision summary.

Release integration for **github-issue-comment-task**, dated **2026-10-09**: **Approved** by the product owner (conversation user), reference **“Merge main, up the version if needed and push to main”**. Merge the latest main, preserve the released wait feature, and release the approved issue outputs/comment task as **0.25.0**.

Revision **workflow-start-nodes**, dated **2026-10-08**: **Approved for implementation**. Covers the start-node requirements and interactions below only; the remaining baseline draft is unchanged. Approver: **Product owner (conversation user)**. Approval date: **2026-10-08**. Approval reference: user message **“Approved”** following the summary of revision `workflow-start-nodes`.

Revision **integration-event-nodes**, dated **2026-10-08**: **Approved for implementation**. Revises workflow start-node terminology and extensibility as specified below. Approver: **Product owner (conversation user)**. Approval date: **2026-10-08**. Approval reference: user message **“Approved”** following the revised event-node baseline and implementation plan.

Revision **github-issue-comment-waits**, dated **2026-10-08**: **Approved for implementation**. Covers the GitHub Issue commented node and durable wait requirements below, including single-instance advancement and one continuation per wait activation. Approver: **Product owner (conversation user)**. Approval date: **2026-10-08**. Approval reference: user message **“Approved”** after the clarification that multiple comments must not create parallel workflow copies and only a new wait activation may be triggered again.

Revision **github-issue-comment-waits release 0.24.0**, dated **2026-10-09**: **Approved**. Release the approved issue-comment wait feature as **0.24.0**, align application/chart/image versions and deployment documentation, and persist the rule that a new feature merged after a previous release bump receives its own semantic version increase. Approver: **Product owner (conversation user)**. Approval reference: user message **“Do it, also remember that.”** following the proposed 0.24.0 bump. Feature behavior and scope are unchanged.

Revision **managed-agent-images integration**, dated **2026-10-08**: documents implementation authorized before this baseline was introduced. Approver: **Product owner (conversation user)**. Approval references: **“Go ahed” / “Continue”** for the prepared-image implementation, followed by **“Merge main, push, and validate deployment”** for integration of the concrete verified result. This entry records that existing authorization; it does not extend implementation scope.

- **1. Workflow design and automation**
  - **Definitions and visual editor**
    - **Requirements:**

      - Create named definitions with immutable enabled/disabled versions and a selectable default.
      - Edit nodes, settings and control/data connections with selection, duplication, deletion, undo/redo, arrangement, zoom and minimap.
      - Validate prerequisites and node-specific errors before enabling a version.
      - Preserve editor layout and guard unsaved edits.
      - Pin each run to its definition version while keeping legacy definitions readable.
      - **Implemented in 0.27.0 — revision workflow-management-layout:** Show Manual Start within the selected definition's panel, with version choices restricted to that definition. Start the selected saved version; require saving or discarding dirty editor changes before starting so navigation preserves the existing unsaved-edit guard. Preserve issue URL, repository URL, base branch and model inputs, existing permission checks and enabled/manual-entry validation. Clearly explain when the selected definition has no manually startable enabled version; retain navigation to the resulting execution after a successful start.

      **Approved revision workflow-editor-groups:**

      - Create a group from selected workflow nodes, showing a containing background and visible name on the canvas. Allow adding nodes to an existing group and removing nodes from it; each node belongs to at most one group, with no nested groups.
      - Drag a group to move all its members together while preserving their relative positions and existing connections. Individual member nodes remain editable and movable within the group; group bounds accommodate their positions.
      - Edit a group's non-empty name and choose its background color from a fixed preset palette: gray, blue, green, yellow, orange, purple and pink. Show the chosen color behind member nodes while keeping names, nodes and connections readable.
      - Ungroup or delete a group container without deleting its member nodes or connections. Include group creation, membership, movement, naming, color changes and ungrouping in undo/redo and unsaved-change tracking. Preserve valid membership when nodes are deleted or duplicated, and preserve groups when using Arrange.
      - Save group identifiers, names, preset colors, membership and layout with each definition version; restore them when reopening the editor and display them when viewing saved versions. Existing workflows without groups remain compatible.

    - **Interactions with other features:**

      - Saving a version snapshots **Reusable custom tasks**, **Personas** and **Reusable profiles and inheritance** so catalog edits cannot change existing executions.
      - Node settings select configurations from **AI configuration and authentication** to determine the agent and model used during execution.
      - **Durable orchestration and runtimes** executes the saved graph, while **Execution investigation and history** displays the same pinned version.
      - **Approved revision workflow-editor-groups:** Groups organize the visual editor only; they do not become executable steps or change control/data connections, validation, scheduling, or task settings. Immutable definition versions retain their own group metadata.

  - **Manual and issue-label starts**
    - **Requirements:**

      - Start from an issue URL, repository, base branch and model, optionally selecting a definition/version.
      - Enabled start-only label triggers select connected repositories and optional branch/model overrides.
      - Validate signed deliveries, audit matches and suppress duplicate delivery/trigger pairs and existing-issue starts.

    - **Interactions with other features:**

      - **Connected repositories** supplies trigger repository selections and default branches, while **Webhooks and provider feedback** delivers signed label events for matching.
      - A matched trigger or manual start selects a version from **Definitions and visual editor** and queues execution through **Durable orchestration and runtimes**.
      - The **Built-in development workflow** checks its planning and implementation labels after a run starts, so triggering alone does not authorize those phases.

  - **Workflow start nodes — Implemented in 0.22.0; approved revision workflow-start-nodes**
    - **Requirements:**

      - Represent every workflow entrypoint as a first-class start node in the saved definition and visual editor, with explicit outgoing control connections to downstream work. Start nodes have no incoming control connections and do not launch agent workers.
      - Allow a workflow to have at most one default manual start node. Manual start enters through that node; a workflow without one is trigger-only and cannot be started manually.
      - Allow additional start nodes for webhooks and integration-provided triggers, including the existing GitHub/Gitea issue-label triggers. Keep event matching, enabled state, repository selections and supported branch/model overrides on the relevant start node; only the matching node starts that run.
      - Validate and execute each entrypoint independently: schedule the work reachable from the selected start node, without waiting for other start nodes or their exclusive paths. Preserve existing graph, join, loop and decision restrictions for each entrypoint.
      - Keep existing saved definitions and pinned executions readable. Adapt legacy manual task entrypoints to explicit manual start nodes when editing, preserve existing trigger behavior, and retain the selected start-node identity in new execution history and trigger audit records.

    - **Interactions with other features:**

      - **Definitions and visual editor** creates, configures and connects start nodes and validates their entry routes before enabling a version. Start-node configuration is pinned with that version.
      - **Connected repositories**, **GitHub and Gitea connections** and **Webhooks and provider feedback** supply provider access and validated events. Integration starts retain signature verification, delivery auditing and existing duplicate suppression; generic webhook starts require authenticated delivery and duplicate suppression as part of their approved delivery contract.
      - **Durable orchestration and runtimes**, **Ordinary task graphs** and **Typed task data** resolve scheduling and producer availability from the selected entrypoint. **Execution investigation and history** identifies the start node that launched the run; the **Built-in development workflow** retains its planning and implementation gates.

  - **Integration event nodes — Implemented in 0.22.0; approved revision integration-event-nodes**
    - **Requirements:**

      - Use **event** instead of **trigger** for the workflow entrypoint concept in the editor, application contracts and documentation. Provide a shared event-node contract and an integration registration mechanism for distinct event definitions, validation, settings and delivery matching; do not expose a universal event node with a selector containing every provider's options.
      - Provide the manual **Start** event in application code, and an authenticated **Webhook** event as a separate built-in event definition. Retain at most one manual Start event per workflow, optional for workflows started only by external events. Event nodes have no incoming control connections, launch no agent worker, and connect to one execution entry.
      - Have the GitHub integration contribute separate **Issue created** and **Label added** event nodes, matching signed `issues/opened` and `issues/labeled` deliveries respectively. Both select connected GitHub repositories and supported branch/model overrides; only Label added exposes a label filter. Retain existing Gitea label-start behavior through a separate Gitea-owned Label added event definition.
      - **Implemented in 0.25.0 — approved revision github-issue-created-output:** GitHub **Issue created** declares an output named `issue` of type string. Serialize the complete `issue` object from the validated GitHub `issues/opened` webhook as JSON, retaining all supplied fields and nested values rather than projecting a subset. Persist this string for the selected event's execution and expose it for downstream task input bindings and execution evidence. Preserve the event-time issue snapshot across restarts and retries.
      - **Implemented in 0.25.0 — approved revision github-issue-comment-task:** GitHub **Issue created** also declares an **Issue id** output (`issueId`) containing the repository-local GitHub issue number as a number. Retain this output with the event-time snapshot and make it bindable to downstream Add issue comment tasks.
      - Persist each event's stable type identity and its own configuration, pin these with the workflow version, and dispatch only matching enabled event nodes. Retain independent entry scheduling, authenticated/signed delivery validation, duplicate suppression, delivery audit and selected-event execution history.
      - Keep old serialized trigger/start definitions, pinned runs and delivery audit records readable through compatibility adapters. Adapt existing definitions in editor drafts; update the built-in workflow template to include a Start event, and create new versions when upgrading existing workflows rather than rewriting saved versions. Verify distinct event catalog entries, provider-specific settings, signed GitHub issue-created/label-added matching, webhook delivery, manual execution and compatibility with E2E coverage.

    - **Interactions with other features:**

      - **Definitions and visual editor** consumes registered built-in and integration event definitions to list distinct nodes and display only that event's settings. Integration registration supplies both backend behavior and editor metadata without a central provider-type selector.
      - **GitHub and Gitea connections**, **Connected repositories** and **Webhooks and provider feedback** supply provider registration, connected-repository selection and validated deliveries. Existing workflow progression and planning/implementation gates continue to apply after entry.
      - **Durable orchestration and runtimes**, **Typed task data** and **Execution investigation and history** resolve execution from the selected event node, retain its identity and preserve historical evidence. Compatibility adapters retain legacy serialized field names and database records where needed without destructive migration.
      - **Approved — revision github-issue-created-output:** **Webhooks and provider feedback** supplies the complete issue object after signature validation. **Definitions and visual editor** exposes the `issue` string output on GitHub Issue created nodes; **Typed task data** permits bindings from that event only where it is the guaranteed selected entrypoint. **Durable orchestration and runtimes** retains the output before downstream scheduling, and **Execution investigation and history** displays the persisted JSON string.

  - **GitHub issue-comment waits — Implemented in 0.24.0; approved revision github-issue-comment-waits**
    - **Requirements:**

      - Add a distinct GitHub-owned **Issue commented** node callable within an existing workflow through ordinary incoming and outgoing control connections. Reaching this node arms a wait; a matching comment completes the node and continues the same execution and pinned definition version. It does not create a new execution or launch an agent worker.
      - Accept a required positive issue number (the proposed meaning of “issue id”), either literal or bound through existing typed task data, scoped to one connected GitHub repository. Default to the execution repository when it is a connected GitHub repository; permit explicit selection. Resolve and freeze these inputs on activation. Validate missing, invalid or inaccessible repository/issue inputs before waiting.
      - Match the first newly created comment after the wait is durably armed, from a verified GitHub `issue_comment/created` delivery for that exact repository and issue. Exclude pull-request comments, edits, deletions and comments created before activation, including late delivery of an older comment. Human and bot comments both qualify. Expose persisted scalar outputs for comment ID, body, author login, URL and creation time to downstream bindings.
      - Provide an extensible durable node-wait/resume contract for integrations, distinguishing callable waits from entry events. Persist execution, node, attempt/iteration identity, frozen correlation inputs, activation boundary, wait state and matched evidence. Recover waits and accepted events after service restarts; waiting occupies no Kubernetes worker. Block dependent paths until matched while independent branches continue. Preserve supported graph, decision and loop restrictions; repeated loop activations receive distinct wait identities.
      - Deduplicate provider deliveries and comment identities. Atomically claim an armed wait activation for exactly one qualifying comment and persist its completion evidence; further comments cannot claim that activation, enqueue another continuation or create a parallel copy of the workflow instance. Serialize advancement of each workflow instance across webhook handlers, scheduler ticks and service replicas, including recovery after a restart. A subsequent comment can trigger the instance only after it enters a wait node again and durably arms a new activation; comments arriving while it is not waiting are not queued for a later wait, and a consumed comment cannot satisfy a later activation. Existing explicitly configured graph branches remain supported within the same execution. Handle concurrent delivery, arming, scheduling and cancellation without an event-loss gap. Operator pause retains matching evidence while preventing downstream scheduling; operator resume alone cannot satisfy a wait. Cancellation invalidates outstanding waits and prevents revival. Show waiting state, target issue and matched evidence in history; preserve successful predecessors, normal retries, version pinning and existing entry-event behavior.

    - **Interactions with other features:**

      - **Definitions and visual editor** and **Integration event nodes** distinguish entry events from callable wait nodes. GitHub owns this node's catalog metadata, settings, validation and matching; existing Start, Webhook, Issue created and Label added events remain entrypoints.
      - **Connected repositories**, **GitHub and Gitea connections** and **Webhooks and provider feedback** supply repository access, signed delivery validation and wake-up signaling. Comment deliveries resume armed waits without applying start-event existing-issue suppression.
      - **Durable orchestration and runtimes**, **Ordinary task graphs**, **Loops and explicit parallel groups** and **Pause, resume, cancel and retry** persist activation/completion, resume dependent work and reject stale or canceled waits. **Typed task data** binds issue numbers and exposes comment outputs. **Execution investigation and history** distinguishes event waiting from operator pause and retains correlation evidence.

  - **Variable connections, typed port hints and control cycles — Approved revision variable-ports-and-control-cycles**
    - **Requirements:**

      - Allow native canvas dragging and inspector editing to attach multiple distinct matching outputs to one variable input, including multiple outputs from the same producer. Adding a source retains existing sources and their saved order; duplicate identical connections remain deduplicated. Explain rejected type/availability connections rather than silently refusing them. Retain exact scalar type matching and ordinary task inputs' single-source behavior.
      - Hovering output parameter labels or ports displays the parameter name and scalar type (string, number or boolean), including event, script, custom/Agent and variable outputs. Provide equivalent accessible descriptions; input port hints expose the expected type so connection compatibility is discoverable.
      - Accept ordinary control back-edges and self-cycles without requiring an explicit Loop node, configured repeat count, maximum iteration count or overall cycle deadline. Intentionally infinite cycles are supported. Entry event nodes retain their no-incoming-control rule. Existing per-task execution timeouts remain applicable.
      - Execute every repeated task visit as a fresh durable activation with separate run/attempt identity and ordered visit evidence. A successful earlier visit does not suppress later execution. Persist activation and routing progress so restart cannot replay a completed side effect or lose the next visit. Back-edges cannot make the first cycle pass wait for an execution that has not happened yet; preserve existing acyclic fork/join semantics.
      - Keep pause/resume/cancel and consumer retry meaningful for cyclic execution. Pause prevents later scheduling, cancellation terminates active workloads/waits and prevents reactivation, and retry preserves the failed visit's frozen preparation. Resolve variables from the applicable successful producer activations and freeze source visit/attempt provenance for each consumer activation. First/Override retain saved source-order semantics. Pure variable-expression cycles are separate from control cycles and remain invalid because they have no initial value or scheduling position.

    - **Interactions:**

      - **Definitions and visual editor**, **Typed task data** and **Reusable custom tasks** share port types, multiple-variable-source behavior and actionable compatibility feedback. Native groups, parent coordinates and 0.27.0 layout remain intact.
      - **Ordinary task graphs**, **Decisions**, existing bounded **Loops and explicit parallel groups**, **Durable orchestration and runtimes** and **GitHub issue-comment waits** retain durable route/activation and entrypoint guarantees. **Execution investigation and history** distinguishes repeated visits; **Pause, resume, cancel and retry** operates on active visits without changing pinned definition versions. Historical acyclic and explicit-loop executions remain readable.

  - **Ordinary task graphs**
    - **Requirements:**

      - Support sequential tasks, multiple successors, concurrent branches, nested forks, all-input joins and multiple terminal tasks.
      - Schedule tasks after all predecessors succeed.
      - Complete an execution after every reachable task succeeds.
      - Reject cycles and unreachable join dependencies.
      - Reject mixing multi-connection graphs with loop, decision or explicit parallel controls.

    - **Interactions with other features:**

      - **Durable orchestration and runtimes** launches runnable agent and **Shell scripts** tasks concurrently and waits for every predecessor before starting a join.
      - **Typed task data** uses graph ancestry to validate producer availability, while its data connections do not schedule tasks.
      - **Pause, resume, cancel and retry** retries failed tasks without rerunning successful siblings, whose attempts remain visible in **Execution investigation and history**.

  - **Loops and explicit parallel groups**
    - **Requirements:**

      - Persist fixed-count loops with iteration/runtime bounds and iteration history.
      - Explicit parallel groups run independent planning branches and wait at a join.
      - Shared-branch writes are unsupported.
      - Validate supported nesting and task placement.

    - **Interactions with other features:**

      - Loop iterations and parallel planning branches apply pinned **AI configuration and authentication**, **Personas** and **Reusable profiles and inheritance** settings to each task.
      - **Typed task data** resolves loop outputs within the permitted iteration boundaries, while **Execution investigation and history** separates iteration and branch evidence.
      - **Pause, resume, cancel and retry** pauses subsequent scheduling or terminates active branch workers through **Durable orchestration and runtimes**.

  - **Decisions**
    - **Requirements:**

      - Choose exactly one True/False route using allowlisted typed sources/operators and explicit missing-value behavior.
      - Support nested decisions and convergence in the outer graph.
      - Reject decisions inside loop/parallel regions.
      - Persist selected routes atomically and preserve them during retry.

    - **Interactions with other features:**

      - Decisions read persisted workflow and task evidence from **Durable orchestration and runtimes** to select exactly one execution route.
      - **Typed task data** rejects bindings from producers that are not guaranteed to execute on the selected control paths.
      - **Execution investigation and history** displays the recorded condition and route, while **Pause, resume, cancel and retry** preserves successful routing decisions.

- **2. Tasks and AI behavior**
  - **Add issue comment — Implemented in 0.25.0; approved revision github-issue-comment-task**
    - **Requirements:**

      - Provide an **Add issue comment** task type in the workflow editor, with a required numeric **Issue id** input (`issueId`) and required string **Text** input (`text`). Accept literals or typed bindings, including Issue created → Issue id and text supplied by an upstream task.
      - Interpret Issue id as the positive integer issue number in the execution's connected GitHub repository; use that repository's integration credentials. Reject invalid issue numbers and empty comment text before posting.
      - Execute the task through the existing DevOps comment API without launching an agent worker. Mark success only after the provider accepts the comment; expose provider failures in task history and preserve successful task results during workflow retries.

    - **Interactions with other features:**

      - **Definitions and visual editor** configures and pins the task and its input bindings. **Typed task data** validates producer availability and input types and freezes resolved input values before execution.
      - **Connected repositories** and **GitHub and Gitea connections** provide the execution's GitHub repository and authenticated platform client. **Durable orchestration and runtimes** schedules the task on its selected route; **Execution investigation and history** retains resolved inputs and task outcomes. This revision adds GitHub comment posting only.

  - **Built-in development workflow**
    - **Requirements:**

      - Run planning, implementation, pull request creation and comment handling in that order, subject to `ready-to-plan` and `ready-to-implement` gates.
      - Publish or update marked plans and revise them from newer issue feedback.
      - Create or reuse the branch and open the pull request.
      - Process newer top-level and inline PR feedback, ignore automation comments, react when work starts and post summaries.
      - Reopen completed feedback handling when notified, complete merged PRs and cancel closed-unmerged PRs.

    - **Interactions with other features:**

      - **GitHub and Gitea connections** provides issue content, comments and source-control operations, while **Connected repositories** identifies the repository and base branch.
      - **AI configuration and authentication**, **Personas** and **Reusable profiles and inheritance** determine how planning, implementation and comment-handling workers execute.
      - **Webhooks and provider feedback** wakes orchestration for new issue or PR feedback and updates workflow outcomes when the PR merges or closes.
      - **Pause, resume, cancel and retry** reuses successful task results, while **Execution investigation and history** retains the conversation context and attempt evidence.

  - **Reusable custom tasks**
    - **Requirements:**

      - Create, edit, enable/disable and delete revisioned prompt templates with typed inputs, defaults, timeout and optional persona.
      - Run in a scratch workspace.
      - Validate and freeze resolved inputs, rendered prompt and catalog snapshot before launch.
      - Protect edits/deletion with expected revisions.
      - Retain historical snapshots.

    - **Interactions with other features:**

      - **Definitions and visual editor** adds custom-task nodes and pins their catalog revisions when saving a workflow version.
      - **Typed task data** resolves template inputs before launch and validates declared outputs before downstream consumers can use them.
      - **AI configuration and authentication**, **Personas** and **Reusable profiles and inheritance** supply the agent settings, instructions and scratch-workspace execution environment.
      - **Execution investigation and history** displays the frozen prompt, inputs and outputs so later catalog edits do not obscure what ran.

  - **Typed task data**
    - **Requirements:**

      - Declare named string/number/boolean outputs and validate completion JSON for names, required values, types and size limits.
      - Bind each input to one guaranteed prior producer with matching type or provide a literal.
      - Apply the documented optional/default rules and loop/path restrictions.
      - Keep data edges separate from scheduling.
      - Freeze producer attempt provenance and preparation across restart/retry.

    - **Implemented in 0.29.0 — revision typed-data-variables:**

      - Place named variables in a workflow definition as compact capsule-shaped data nodes, visibly smaller and distinct from task/event/control nodes. Each variable declares exactly one scalar type (string, number or boolean), one multi-connection input port and one output port supporting fan-out. Task output → variable, variable → task input and variable → variable connections require exact type matches. Task inputs retain one source or a literal. Variables have no control ports, worker, task attempt or independent scheduling behavior; unused variables do not block workflow completion.
      - Configure an ordered source list and a combination mode on each variable. **Aggregate:** strings append with a configurable separator (default: newline), numbers sum with existing numeric bounds enforced, and booleans explicitly choose **Any (OR)** or **All (AND)** (default: Any). **Override:** select the last present value in the configured source order. **First:** select the first present value in that order and ignore the other values. Source order is editable and persisted; completion timing does not determine the result. First/Override do not cancel producers or accelerate control joins.
      - Preserve empty strings, numeric zero and boolean false as present values. Omit absent optional outputs; if no source has a value, expose an absent variable output so the consumer's existing default/required/optional rules apply. Do not invent zero, false, true or empty-string results for an empty source set. Apply existing scalar and total prepared-input limits to results, and surface overflow/size errors before consumer launch.
      - Resolve variable chains during consumer input preparation using successfully validated persisted outputs. Reject duplicate source/output connections, variable cycles and type mismatches. Validate every transitive producer against the consumer's existing guaranteed-prior-execution, selected-event and loop/path restrictions, including sources ignored by First/Override. Preserve current restrictions on mixing graph/control structures; variables cannot introduce implicit scheduling dependencies, cross-iteration accumulators or mutable global state.
      - Pin variable configuration with each saved workflow version. Freeze resolved consumer values, ordered source evidence, combination settings and transitive producer run/attempt provenance before launch. Preserve frozen preparation across restart and consumer retry; new preparation must use current successful producer evidence after a producer retry. Retain old single-source definitions and pinned runs, and preserve variables/connections/order through editing, save/reload, duplication, deletion and undo/redo.

    - **Approved interactions for typed-data-variables:**

      - **Definitions and visual editor** adds a Variables palette entry, distinct shape, typed ports, mode/separator/boolean-operation settings and ordered source editing. **Reusable custom tasks**, inline Agent nodes, **GitHub Add issue comment** and **GitHub issue-comment waits** accept a compatible variable output through their existing typed input contracts. Event and script outputs may supply variables wherever their existing availability rules allow.
      - **Ordinary task graphs**, **Loops and explicit parallel groups**, **Decisions** and event entrypoints supply control-path and iteration validation. **Durable orchestration and runtimes** resolves data without launching a variable worker. **Pause, resume, cancel and retry** preserves frozen preparation. **Execution investigation and history** shows variable settings, resolved values and contributing source provenance in the pinned data graph without representing variables as executed tasks.

    - **Implemented in 0.26.0; approved revision agent-task-output-correction:**

      - For both inline Agent nodes and reusable custom tasks with declared outputs, include the pinned output format directly in the effective agent prompt: output names, scalar types, required/optional rules, strict final JSON-object instructions and existing value/size limits. Retain free-text completion for tasks without declared outputs.
      - Extract the authoritative final response and validate it against the pinned output schema before marking the task successful or releasing downstream consumers. Streaming logs and intermediate messages cannot substitute for the final response.
      - When a successful agent turn has no extractable final response or returns invalid declared outputs, send the same agent conversation a correction message containing the validation error and required output format. Request corrected final output from its completed work, without repeating the original task. Allow at most two correction turns within the original task timeout; respect cancellation and do not correct failed agent execution.
      - Persist correction progress and retain correction messages and responses in attempt evidence so orchestration restart cannot reset the correction limit or expose stale outputs. Fail with a clear output-validation reason if correction is exhausted; persist structured outputs only after successful validation.

    - **Interactions for agent-task-output-correction:**

      - **Reusable custom tasks**, inline Agent nodes, **Personas** and **Reusable profiles and inheritance** compose and retain the effective output instructions using the saved workflow snapshot.
      - **Durable orchestration and runtimes** continues the agent conversation for correction within its existing execution bounds. **Pause, resume, cancel and retry** preserves correction progress on restart and clears outputs for a new attempt; **Execution investigation and history** retains validation failures and correction evidence.
      - **Ordinary task graphs**, **Loops and explicit parallel groups** and **Decisions** consume only successfully validated outputs under the existing producer and provenance rules.

    - **Interactions with other features:**

      - **Reusable custom tasks** produces validated scalar outputs, and **Shell scripts** supplies stdout that consumers receive through explicit input bindings.
      - **Ordinary task graphs**, **Loops and explicit parallel groups** and **Decisions** determine which producers are guaranteed to complete before each consumer.
      - **Pause, resume, cancel and retry** reuses frozen consumer preparation and clears a retried producer’s outputs to prevent stale results.
      - **Execution investigation and history** exposes resolved inputs and producer attempt identities according to the [data contracts](../../docs/task-data-passing.md).

  - **Personas**
    - **Requirements:**

      - Manage revisioned named instruction profiles, active state and deletion with concurrency checks.
      - Preserve an immutable Default persona.
      - Allow workflow defaults and task overrides.
      - Snapshot effective instructions into saved workflow versions.
      - Later catalog changes do not alter existing runs.

    - **Interactions with other features:**

      - **Definitions and visual editor** resolves workflow defaults and task overrides into immutable persona snapshots when saving a version.
      - The **Built-in development workflow** and **Reusable custom tasks** compose agent prompts with those pinned instructions before launching workers.
      - **Execution investigation and history** shows the persona identity and instructions used by each task, even after the catalog changes.

  - **AI configuration and authentication**
    - **Requirements:**

      - Manage named configurations, provider/model/API endpoint, credentials and runtime options.
      - **Approved fix; revision ai-setup-default-model-dropdown:** Select the AI Setup Default Model through a dropdown using existing model-discovery behavior. Preserve saved model selections, allow an unset default, and provide discovery/refresh for supported saved configurations with progress, error and unavailable-state feedback.
      - Support OpenHands API/cloud credentials and native Codex subscription execution, credential import/connect/reconnect and model discovery.
      - **Approved fix; revision codex-device-code-extraction:** Connect and Reconnect Codex must display the one-time code emitted in the device-login prompt, including codes whose group lengths differ from the existing four-character/five-character assumption. Strip terminal formatting before extraction, preserve the emitted code, and return no code when the prompt has not supplied one.
      - Allow per-step AI selection and resolve the model from step override to workflow model to configuration default.
      - Expose credential presence without returning secrets.
      - **Planned:** Add execution for other ACP providers and Claude Pro support, as saved provider choices do not imply runtime support.

    - **Interactions with other features:**

      - **Definitions and visual editor** selects per-step configurations and models, with unset models falling back to workflow and configuration defaults.
      - **Approved fix; revision ai-setup-default-model-dropdown:** AI Setup and the existing step model selector use the same discovery API and model identifiers; saving a selected default preserves the existing step-to-workflow-to-configuration fallback order.
      - **Durable orchestration and runtimes** runs login and discovery jobs and supplies the chosen credentials and model to agent workers.
      - **Approved fix; revision codex-device-code-extraction:** Login-job status polling supplies the extracted device URL and code to the AI Setup login card and its Copy action for both connect and reconnect. Extract from login-prompt context so unrelated worker/job identifiers are not displayed as authentication codes.
      - The **Built-in development workflow** and **Reusable custom tasks** use the selected agent configuration, while **Shell scripts** executes without AI credentials.

- **3. Execution environments**
  - **Reusable profiles and inheritance**
    - **Requirements:**

      - Manage revisioned profiles, active state, history and deletion with concurrency checks while preserving the immutable Default profile.
      - Select a workflow default and a per-agent-step inherited/default/named profile.
      - Pin resolved configurations in enabled workflow versions.
      - Allow disabled drafts to retain unresolved selections.
      - Cap task runtime with the selected environment limit.

    - **Interactions with other features:**

      - **Definitions and visual editor** resolves workflow defaults and step overrides into pinned environment configurations when saving enabled versions.
      - **Durable orchestration and runtimes** applies the pinned image, tools and timeout cap when launching each task, including loop iterations and retries.
      - **Images and tool provisioning** and **MCP and browser/container capabilities** consume the selected profile to prepare the worker environment.
      - **Execution investigation and history** displays the saved profile revision so catalog updates cannot change the explanation of an earlier run.

  - **Images and tool provisioning**
    - **Requirements:**

      - Select compatible worker images, pull policy and Kubernetes image-pull Secret references.
      - Install enabled named tools in order with bounded shell commands.
      - Failed/timed-out bootstrap stops execution and emits logs.
      - Custom images preserve the worker protocol.

    - **Interactions with other features:**

      - **Reusable profiles and inheritance** supplies the pinned image and ordered installation commands used by **Durable orchestration and runtimes** when starting workers.
      - **MCP and browser/container capabilities** selects which tools are installed, while **Secret references** supplies selected aliases to worker processes.
      - Installation failures prevent agent or **Shell scripts** execution and appear in **Live and retained logs**.
      - Provisioning consumes the task deadline enforced by **Durable orchestration and runtimes**, as described in the [environment contracts](../../docs/workflow-environment-extensions.md).

  - **MCP and browser/container capabilities**
    - **Requirements:**

      - Configure native Codex/OpenHands stdio or HTTP MCP servers with selected secret aliases.
      - Inherit or explicitly select browser, nested-containers, named MCP and tool provisioning, with an empty selection provisioning none.
      - Reserve native Playwright configuration.
      - Reject unsupported runtime/capability combinations.
      - Use capabilities to control provisioning without treating them as arbitrary code isolation.

    - **Interactions with other features:**

      - **Reusable profiles and inheritance** supplies server definitions, and **Secret references** resolves selected aliases into MCP authentication settings.
      - **AI configuration and authentication** determines whether native Codex or OpenHands configuration is generated for the selected MCP servers.
      - **Durable orchestration and runtimes** provisions selected browser tools or Kubernetes nested containers and rejects capabilities unsupported by the chosen runtime.
      - Browser tools support application checks from **Deployment, diagnostics and future operations**, while worker bootstrap messages enter **Live and retained logs**.

  - **Secret references**
    - **Requirements:**

      - Select operator-managed Secret keys and non-reserved worker environment aliases.
      - Validate keys before launch and inject only selected values.
      - Support configured container-runtime equivalents.
      - Keep values out of configuration/history/evidence and mask known credentials in telemetry.
      - Preserve external Secrets after cleanup.

    - **Interactions with other features:**

      - **Durable orchestration and runtimes** validates selected keys and injects their values under configured aliases before launching worker processes.
      - **MCP and browser/container capabilities**, **Images and tool provisioning** and **Shell scripts** use those aliases for authenticated external access.
      - **Roles and administration** restricts configuration access, while **Execution investigation and history** records references rather than secret values.
      - **Live and retained logs** masks known selected values, although arbitrary task output can still expose unrelated or obfuscated secrets.

  - **Shell scripts**
    - **Requirements:**

      - Run bounded `sh`/`bash` scripts in workspace or repository directories without AI credentials.
      - Retain stdout/stderr and the actual exit code, failing on nonzero exit or timeout.
      - Expose sanitized, bounded stdout as a named string output with truncation marked.
      - Leave script changes uncommitted and unpushed unless explicitly handled elsewhere.
      - Inherit tools only and reject agent-only capabilities.

    - **Interactions with other features:**

      - **Connected repositories** supplies checkout context for repository scripts, while workspace scripts execute without a repository checkout.
      - **Images and tool provisioning** prepares selected tools and **Secret references** injects aliases before the script runs.
      - **Typed task data** binds sanitized stdout to custom-task inputs and rejects values exceeding consumer limits.
      - **Live and retained logs** receives stdout/stderr, while **Execution investigation and history** records the script exit code.
      - **Pause, resume, cancel and retry** controls script scheduling and attempts through **Durable orchestration and runtimes**.

- **4. Integrations and access management**
  - **GitHub and Gitea connections**
    - **Requirements:**

      - Manage GitHub App credentials, installation discovery and generated webhook/callback URLs.
      - Mint installation tokens for repository work.
      - Manage Gitea endpoint/token configuration and webhook secrets.
      - Allow integration editing and removal, deleting connected repository records when their integration is removed.
      - **Planned:** Add Azure DevOps work-item, repository and PR integration.

    - **Interactions with other features:**

      - **Connected repositories** uses GitHub installation access or Gitea credentials to discover and connect repositories available for workflow operations.
      - The **Built-in development workflow** uses provider adapters to read issues, manage branches, open PRs and publish plans or feedback summaries.
      - **Webhooks and provider feedback** uses integration secrets to validate deliveries before starting or waking workflows.
      - **Identity and invitations** uses GitHub App callbacks to establish authenticated users through external login.
      - Removing an integration also removes its **Connected repositories** records so they are no longer available for new trigger selections.

  - **Connected repositories**
    - **Requirements:**

      - Discover GitHub installation repositories.
      - Connect Gitea repositories, retain URL/default branch metadata and disconnect individual repositories.
      - Restrict workflow writes to configured repository access.

    - **Interactions with other features:**

      - **GitHub and Gitea connections** supplies the credentials and provider access needed to connect each repository.
      - **Manual and issue-label starts** matches trigger selections against connected repository records and uses their default branch when no override exists.
      - The **Built-in development workflow** performs branch and PR operations in the selected repository, while **Shell scripts** uses it for repository checkout.

  - **Webhooks and provider feedback**
    - **Requirements:**

      - Validate GitHub/Gitea signatures.
      - Handle supported issue-label, issue/comment, PR/comment/review events.
      - Wake orchestration promptly.
      - Avoid duplicate automation work and retain trigger delivery audit.
      - Retain periodic polling for workflow progression.

    - **Interactions with other features:**

      - **GitHub and Gitea connections** supplies signing secrets so unverified deliveries cannot start or wake workflows.
      - **Manual and issue-label starts** matches validated label events to enabled triggers and records delivery-to-execution audit links.
      - The **Built-in development workflow** uses newer comments to revise plans or address PR feedback and uses merge/closure state to complete or cancel runs.
      - **Durable orchestration and runtimes** wakes after supported deliveries and retains periodic polling to advance workflows between events.

  - **Identity and invitations**
    - **Requirements:**

      - Support GitHub external login, persistent Identity users and logout.
      - **Approved revision identity-provider-restart-notice:** Show the GitHub integration restart warning only while its saved identity-provider settings have not been applied to the running login configuration. Ordinary application restarts and deployment rollouts must retire stale restart state when the configuration is applied; enabling an already-enabled provider without changing its settings must not create a new restart requirement. Preserve the administrator restart action for unapplied changes. Cover stale persisted flags and repeated activation with regression tests. The current GitHub challenge and callback read saved integration settings on each request, so integration responses must report no restart requirement, including for legacy persisted flags.
      - Activate an identity provider only after successful login and grant the activating user administration.
      - Gate anonymous management access when a provider is enabled and restrict users without permission to invite redemption.
      - Allow admins to create and list expiring hashed invite codes, showing raw codes only once.
      - Redeem invites after login and grant administration under the current invite contract.

    - **Interactions with other features:**

      - **GitHub and Gitea connections** supplies GitHub identity-provider configuration, and successful provider activation grants the signed-in user administrative access.
      - **Approved revision identity-provider-restart-notice:** Integration details and the restart warning reflect the running authentication configuration, including after deployment or application restart. Request-time configuration makes legacy saved restart flags obsolete for GitHub login. Preserve existing activation authorization, invitations and management roles.
      - Invite redemption grants the administration role enforced by **Roles and administration**, while users without permissions remain on the invite-only screen.
      - **Roles and administration** then determines which configuration pages, execution views and workflow commands the authenticated user can access.

  - **Roles and administration**
    - **Requirements:**

      - List users/roles and assign `WorkflowViewer`, `WorkflowOperator` and `ManagementAdmin`.
      - Higher roles include lower capabilities.
      - Enforce read, workflow-command and configuration permissions when management authorization is enabled.
      - Retain explicit trusted local-development bypass configuration.

    - **Interactions with other features:**

      - **Identity and invitations** establishes the signed-in user, whose assigned roles determine management capabilities when authorization is enabled.
      - Viewer access permits **Execution investigation and history** and **Live and retained logs**, while operator access permits **Pause, resume, cancel and retry**.
      - Administrator access permits changes to **Definitions and visual editor**, **AI configuration and authentication**, integration settings and reusable catalogs.
      - **Definitions and visual editor** and execution controls hide or disable actions that the current user’s roles do not permit.

- **5. Operations and platform**
  - **Execution investigation and history**
    - **Requirements:**

      - Search/filter by text, status, definition, repository and date.
      - Provide Running/Failed presets and locally saved views.
      - Show the pinned read-only execution graph, task state/timing, skipped nodes, loop iterations, attempts, worker identity, errors, decisions and typed evidence.
      - Support links to specific tasks/attempts and bounded evidence exports without provider credentials.
      - **Implemented in 0.27.0 — revision workflow-management-layout:** Keep control/data output ports and their labels separate from node titles, descriptions, execution status and duration, including event nodes with multiple outputs. Retain graph connections and node selection behavior. Fit wide execution graphs in the compact pane, account for port rows when arranging nodes, and place newly added palette nodes in unoccupied space as card heights change.
      - **Implemented in 0.27.0 — revision workflow-management-layout:** Replace the large execution list above the detail view with a compact, bounded execution browser beside the selected execution on wide screens; stack the browser and detail on narrow screens. Preserve search, filters, presets, saved views, pagination, refresh and execution/task deep links. Remove the standalone Manual Start form from Workflow Management so execution investigation is its primary content.

    - **Interactions with other features:**

      - **Definitions and visual editor** supplies the pinned graph, which is overlaid with task states and attempts from **Durable orchestration and runtimes**.
      - **Typed task data** supplies resolved inputs, outputs and producer provenance, while **Decisions** and **Loops and explicit parallel groups** supply route and iteration evidence.
      - Selecting a task or attempt filters **Live and retained logs** to that execution context.
      - **Implemented in 0.27.0 — revision workflow-management-layout:** **Definitions and visual editor** hosts manual execution for the selected definition/version, queues it through **Durable orchestration and runtimes**, and opens **Execution investigation and history** for the new run. The compact execution browser continues selecting the pinned graph and investigation context.
      - **Pause, resume, cancel and retry** adds replacement attempts and control outcomes without removing evidence from previous attempts.

  - **Live and retained logs**
    - **Requirements:**

      - Retain stdout/stderr/errors per task/attempt and filter by source/severity/text.
      - Page older records and download bounded logs.
      - Follow authenticated SSE with cursor replay and allow users to pause following.
      - Deduplicate worker callbacks and reject stale attempts.
      - Mark overflow/truncation and capture runtime-log fallback before cleanup.

    - **Interactions with other features:**

      - **Durable orchestration and runtimes** ingests worker callbacks and captures fallback runtime logs before worker cleanup.
      - **Execution investigation and history** selects task and attempt identities to filter, stream or download the corresponding records.
      - **Roles and administration** enforces viewer access to log history and streams, while **Secret references** supplies known values for masking.
      - **Pause, resume, cancel and retry** preserves prior-attempt logs so retries and worker termination do not erase diagnostic evidence.

  - **Pause, resume, cancel and retry**
    - **Requirements:**

      - Pause only new scheduling while running workers finish.
      - Resume subsequent work.
      - Persist cancellation intent and reconcile runtime termination, retrying cleanup after failures/restart.
      - Support failed workflow/task retry with immutable prior attempts and successful-step reuse.
      - Prevent retry while cleanup is pending.

    - **Interactions with other features:**

      - **Durable orchestration and runtimes** applies controls under scheduler locks and reconciles worker termination before allowing a replacement attempt.
      - **Ordinary task graphs** and **Loops and explicit parallel groups** stop scheduling new work when paused while their running workers continue.
      - **Typed task data** preserves prepared consumer inputs during retries, while successful sibling task results remain reusable.
      - **Execution investigation and history** and **Live and retained logs** retain prior attempts and partial evidence under the [operations semantics](../../docs/workflow-execution-operations.md).

  - **Durable orchestration and runtimes**
    - **Requirements:**

      - Schedule ephemeral Kubernetes Jobs or local Docker/Podman workers asynchronously.
      - Poll/watch completion and enforce deadlines.
      - Persist workflow/task state in PostgreSQL, apply migrations on startup, coordinate API schedulers with a distributed lock and reattach existing jobs after restart.
      - Use fake adapters/in-memory storage for deterministic local development.

    - **Interactions with other features:**

      - **Definitions and visual editor** supplies the pinned graph, and **Ordinary task graphs** identifies tasks whose predecessors have completed successfully.
      - **AI configuration and authentication**, **Reusable profiles and inheritance** and **Secret references** provide the selected worker configuration before runtime launch.
      - Worker completion persists task results for **Typed task data** and **Execution investigation and history**, while telemetry feeds **Live and retained logs**.
      - **Pause, resume, cancel and retry** changes scheduling or cleanup intent that the runtime reconciles across API restarts.
      - **AI configuration and authentication** also schedules login and model-discovery jobs through the same runtime infrastructure.

  - **Deployment, diagnostics and future operations**
    - **Requirements:**

      - Provide API/UI and worker images, Kubernetes/kustomize/Helm assets, configuration/Secrets/RBAC, health checks, version reporting and matching release versions.
      - Supply the local API/UI harness, browser smoke tests and Kubernetes E2E tests.
      - **Approved revision kubernetes-e2e-image-import:** Import the actual API and worker images into the selected disposable kind cluster with a bounded, configurable deadline appropriate for large worker images. Diagnose the failing native import before selecting the loading strategy; use supported kind/container-runtime interfaces, retain command output and elapsed-time/phase evidence on failure, and verify imported images are usable before deployment. Preserve Docker/Podman support, temporary kubeconfig isolation and owned-cluster cleanup. Validate the repair by running the real Kubernetes E2E suite rather than substituting direct Docker checks.
      - Supply CI image/chart publishing, Helm deployment and rollout diagnostics.
      - **Planned:** Add automatic retry/backoff, retention pruning, notifications and artifact storage after an approved baseline update.
      - **Planned:** Add subworkflows, compensation, parallel shared-branch writes and task graphs combined with explicit control nodes after an approved baseline update.

    - **Interactions with other features:**

      - Deployment provisions the API, database and worker access required by **Durable orchestration and runtimes** to persist and execute workflows.
      - Deployment configuration supplies provider credentials for **GitHub and Gitea connections** and operator-managed keys used by **Secret references**.
      - The local harness and browser smoke suite exercise **Definitions and visual editor** and management pages with fake adapters.
      - Kubernetes E2E checks verify **Durable orchestration and runtimes**, **Pause, resume, cancel and retry** and environment provisioning against the [deployment](../../docs/kubernetes-deployment.md) and [runtime configuration](../../docs/job-runtimes.md) contracts.
      - **Approved revision kubernetes-e2e-image-import:** Image loading prepares the existing **Images and tool provisioning** worker for runtime tests and supplies setup diagnostics without changing application task behavior, production runtime settings, or default kubectl context.

## Managed agent images — authorized integration in 0.23.0

Requirements: prepare inline/file-loaded Dockerfiles or connected-repository contexts in Manage → Images; retain immutable source revisions and asynchronous build attempts, bounded logs, cancellation/timeouts and older successful artifacts. Build with isolated rootless Kubernetes BuildKit Jobs and publish to a bundled scoped-token private registry or an external registry. Only pushed, digest-verified, worker-probed linux/amd64 builds are Ready. Administrator access controls mutations. Production builds remain opt-in and need node/pod-reachable TLS, scoped Secrets and compatible build nodes.

Interactions: Agent tasks and environment profiles select an exact Ready build. Save Version freezes the server-resolved digest and pull settings; rebuilds and retries retain that snapshot. Node overrides change only image settings, preserving environment tools, MCP and timeouts. Workflow event nodes remain worker-free and cannot select execution images. Archive retains artifacts referenced by saved versions/history. Kubernetes is the initial private-image runtime; automatic GC, context archives, multi-architecture and secret build arguments are deferred. See [operator documentation](../../docs/managed-agent-images.md) and the [verified feature spec](../specs/2026-10-08-1707-managed-agent-images/plan.md).

Deployment validation for the user-authorized managed-image integration uses increasing semantic release versions for the matching API and worker image tags, applies new chart defaults while retaining installation overrides, and verifies the deployed image plus HTTP health. Authorization references: “Merge main, push, and validate deployment” and “It should increase with version”; no feature scope is added.

Release validation also preserves the existing startup-log polling contract when Kubernetes reports that a worker container “is not available” before startup: return the existing starting message and continue bounded polling. This corrects the cancellation E2E startup race found during the authorized deployment validation; authorization reference: “Merge main, push, and validate deployment”.
