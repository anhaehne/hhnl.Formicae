-- A failed review with historical runs and retry events as stored by 0.7.4.
INSERT INTO workflows ("Id", "IssueUrl", "RepositoryUrl", "BaseBranch", "Status", "CurrentStep",
    "BranchName", "PlanArtifact", "PullRequestUrl", "FailureReason", "CreatedAt", "UpdatedAt")
VALUES ('74000000-0000-0000-0000-000000000001', 'https://github.com/example/repo/issues/74',
    'https://github.com/example/repo', 'main', 'Failed', 'AddressComments',
    'formicae/legacy', 'approved legacy plan', 'https://github.com/example/repo/pull/74',
    'retryable review failure', '2026-07-01T01:00:00Z', '2026-07-01T02:00:00Z');

INSERT INTO task_runs ("Id", "WorkflowId", "Kind", "Status", "ExternalId", "Output", "FailureReason",
    "CreatedAt", "UpdatedAt", "StartedAt", "CompletedAt")
SELECT id::uuid, '74000000-0000-0000-0000-000000000001', kind, status, 'job-' || kind,
    'preserved output: ' || kind, failure, '2026-07-01T01:00:00Z', '2026-07-01T02:00:00Z',
    '2026-07-01T01:01:00Z', '2026-07-01T01:59:00Z'
FROM (VALUES
    ('74000000-0000-0000-0000-000000000010', 'Plan', 'Succeeded', NULL),
    ('74000000-0000-0000-0000-000000000011', 'Implement', 'Succeeded', NULL),
    ('74000000-0000-0000-0000-000000000012', 'CreatePullRequest', 'Succeeded', NULL),
    ('74000000-0000-0000-0000-000000000013', 'AddressComments', 'Failed', 'retryable review failure')
) AS runs(id, kind, status, failure);

INSERT INTO workflow_logs ("Id", "WorkflowId", "TaskRunId", "Level", "Message", "CreatedAt")
VALUES ('74000000-0000-0000-0000-000000000020', '74000000-0000-0000-0000-000000000001',
    '74000000-0000-0000-0000-000000000013', 'Warning', 'preserved retry log', '2026-07-01T01:59:00Z');
INSERT INTO workflow_events ("Id", "WorkflowId", "TaskRunId", "Type", "Level", "Message", "DetailsJson", "CreatedAt")
VALUES ('74000000-0000-0000-0000-000000000030', '74000000-0000-0000-0000-000000000001',
    '74000000-0000-0000-0000-000000000013', 'workflow.retry_requested', 'Information',
    'preserved retry event', json_build_object('attempt', 2)::text, '2026-07-01T01:58:00Z');
