# Integration event nodes

Status: baseline revision `integration-event-nodes` approved by the product owner on 2026-10-08. Approval reference: conversation message “Approved” following the revised baseline and implementation plan.

## Scope and decisions

Replace the universal Start type selector with a shared event-node contract implemented by built-in and integration-owned definitions. Manual Start is a built-in event. Webhook is a distinct built-in event. GitHub registers Issue created and Label added; Gitea retains Label added through its own event definition. Each concrete event owns its settings and matcher. This establishes an extension point for future provider events without claiming support for every GitHub event in this revision.

Retain optional single manual entry, independent entry scheduling, pinned versions, delivery authentication, duplicate suppression and selected-entry history. Adapt legacy trigger/start documents without rewriting pinned history. Update the built-in workflow template and upgrade editable existing workflows through new versions, preserving their task sequence.

Use existing .NET dependency injection and provider webhook handlers for registration and dispatch. Check framework-native facilities and existing installed packages before adding abstractions or dependencies. Use stable event type keys and provider-owned editor metadata/settings rather than an all-provider enum and selector. Preserve legacy wire/database names through explicit compatibility boundaries where necessary.

## Context

User direction: rename trigger to event; base event node with integration-specific implementations; built-in Start; GitHub events such as issue created and label added. Existing-workflow update remains part of the requested work. No visuals supplied. Product alignment: customizable workflows, GitHub-first integration, durable execution. Azure DevOps remains deferred.
