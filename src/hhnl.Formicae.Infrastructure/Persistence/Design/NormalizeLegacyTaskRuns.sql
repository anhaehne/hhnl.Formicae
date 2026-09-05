-- Pre-loop retries updated the same (WorkflowId, Kind) row. Preserve that row and all history.
-- Resolve only against the pinned immutable version, never the current/default definition.
CREATE TEMP TABLE legacy_task_step_map ON COMMIT DROP AS
WITH required_keys AS (
    SELECT "WorkflowId", "Kind" FROM task_runs
    UNION
    SELECT "Id", "CurrentStep" FROM workflows WHERE "CurrentStep" NOT IN ('None', 'Done')
), builtins(kind, uses, canonical_id) AS (
    VALUES ('Plan', 'builtins.plan', 'plan'),
           ('Implement', 'builtins.implement', 'implement'),
           ('CreatePullRequest', 'builtins.create-pull-request', 'createPullRequest'),
           ('AddressComments', 'builtins.address-comments', 'addressComments')
)
SELECT keys."WorkflowId", keys."Kind",
       CASE WHEN w."WorkflowDefinitionVersionId" IS NULL THEN b.canonical_id
            ELSE matched.step_id END AS step_id,
       CASE WHEN w."Id" IS NULL OR b.kind IS NULL THEN 0
            WHEN w."WorkflowDefinitionVersionId" IS NULL THEN 1
            ELSE matched.matches END AS matches
FROM required_keys keys
LEFT JOIN workflows w ON w."Id" = keys."WorkflowId"
LEFT JOIN builtins b ON b.kind = keys."Kind"
LEFT JOIN workflow_definition_versions v ON v."Id" = w."WorkflowDefinitionVersionId"
LEFT JOIN LATERAL (
    SELECT count(*) AS matches, min(step->>'id') AS step_id
    FROM jsonb_array_elements(v."DefinitionJson"::jsonb->'steps') step
    WHERE step->>'uses' = b.uses
) matched ON true;

DO $$
DECLARE invalid record;
BEGIN
    SELECT * INTO invalid FROM legacy_task_step_map
    WHERE matches <> 1 OR step_id IS NULL OR btrim(step_id) = '' LIMIT 1;
    IF FOUND THEN
        RAISE EXCEPTION 'Cannot normalize legacy workflow %, kind %: expected one nonempty definition step, found %',
            invalid."WorkflowId", invalid."Kind", invalid.matches;
    END IF;
END $$;

UPDATE task_runs r SET "DefinitionStepId" = m.step_id
FROM legacy_task_step_map m
WHERE r."WorkflowId" = m."WorkflowId" AND r."Kind" = m."Kind";

UPDATE workflows w SET "CurrentDefinitionStepId" = m.step_id
FROM legacy_task_step_map m
WHERE w."Id" = m."WorkflowId" AND w."CurrentStep" = m."Kind"
  AND w."CurrentDefinitionStepId" IS NULL;

-- LoopIteration intentionally stays NULL: all existing runs were non-loop executions.
DO $$
DECLARE duplicate record;
BEGIN
    SELECT "WorkflowId", "DefinitionStepId", "LoopIteration" INTO duplicate
    FROM task_runs GROUP BY "WorkflowId", "DefinitionStepId", "LoopIteration"
    HAVING count(*) > 1 LIMIT 1;
    IF FOUND THEN
        RAISE EXCEPTION 'Cannot normalize legacy workflow %: duplicate task-run key for step % (iteration %)',
            duplicate."WorkflowId", duplicate."DefinitionStepId", duplicate."LoopIteration";
    END IF;
END $$;
