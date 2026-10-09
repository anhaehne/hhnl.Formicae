using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.OpenHands;

namespace hhnl.Formicae.KubernetesE2ETests;

public sealed partial class KubernetesWorkflowE2ETests
{
    [Theory]
    [InlineData(OpenHandsAuthMethods.CodexSubscription, false)]
    [InlineData(OpenHandsAuthMethods.ApiKey, false)]
    [InlineData(OpenHandsAuthMethods.CodexSubscription, true)]
    [InlineData(OpenHandsAuthMethods.ApiKey, true)]
    public Task Native_worker_corrects_output_in_same_conversation_and_enforces_limit(string auth, bool exhaust)
        => WithExtensionWorkerAsync(async context =>
        {
            const string probe = """
                set -eu
                cat > /workspace/output-probe.py <<'PROBE'
                import json, os, pathlib, sys
                codex = sys.argv[1] == 'codex'
                args = sys.argv[2:]
                identity = '11111111111111111111111111111111'
                state = pathlib.Path('/workspace/output-probe-turn')
                turn = int(state.read_text()) if state.exists() else 0
                state.write_text(str(turn + 1))
                assert json.loads(os.environ['FORMICAE_OUTPUT_SCHEMA']) == [{'name':'summary','valueType':'string','required':True}]
                assert 'Output schema:' in args[-1], 'Output contract missing from prompt'
                assert '65536 UTF-8 bytes' in args[-1], 'Output bounds missing'
                if turn:
                    assert ('resume' if codex else '--resume') in args, 'Correction must resume'
                    assert identity in args, 'Wrong conversation'
                    assert "Required output 'summary' is missing" in args[-1], 'Validation error missing'
                    assert 'Do not repeat the original task' in args[-1], 'Correction repeats original work'
                response = '{}' if not turn or 'Exhaust' in os.environ['FORMICAE_TASK_PROMPT'] else '{"summary":"corrected"}'
                if codex:
                    print(json.dumps({'type':'thread.started','thread_id':identity}))
                    print(json.dumps({'type':'turn.started'}))
                    print(json.dumps({'type':'item.completed','item':{'type':'agent_message','text':response}}))
                    print(json.dumps({'type':'turn.completed'}))
                else:
                    print(json.dumps({'kind':'ActionEvent','source':'agent','action':{'kind':'FinishAction','message':response}}))
                    print('Conversation ID: ' + identity)
                PROBE
                printf '#!/bin/sh\nexec python3 /workspace/output-probe.py codex "$@"\n' > /usr/local/bin/npx
                printf '#!/bin/sh\nexec python3 /workspace/output-probe.py openhands "$@"\n' > /root/.local/bin/openhands
                chmod 755 /usr/local/bin/npx /root/.local/bin/openhands
                """;
            var configuration = context.Configuration with { Tools = [new("output-probe", probe)] };
            var task = new AgentTask(Guid.NewGuid(), TaskRunKind.Custom, exhaust ? "Exhaust correction" : "Return summary", "https://example.invalid/repo", "main", null,
                ExecutionAttemptId: Guid.NewGuid(), TimeoutSeconds: 60, EnvironmentSnapshot: context.Snapshot(configuration), Capabilities: [],
                OutputSchema: [new("summary", "string", true)]);
            var started = await context.StartAsync(task);
            var result = await context.WaitAsync(started.ExternalId);
            var logs = await context.Runtime.ReadJobLogsAsync(started.ExternalId, context.Token);
            Assert.Equal(!exhaust, result.Succeeded);
            Assert.Equal(!exhaust, result.OutputIsFinalResponse);
            Assert.Contains("Correction turn 1/2", logs);
            Assert.Contains(task.ExecutionAttemptId!.Value.ToString(), logs);
            if (exhaust)
            {
                Assert.Contains("Correction turn 2/2", logs);
                Assert.Contains("Output validation failed after 2 correction turns", result.FailureReason);
                Assert.Empty(result.Output);
                await context.AssertWorkerExitAsync(started.ExternalId, 1);
            }
            else
            {
                Assert.Equal("{\"summary\":\"corrected\"}", result.Output);
                Assert.DoesNotContain("Correction turn 2/2", logs);
                await context.AssertWorkerExitAsync(started.ExternalId, 0);
            }
        }, auth);
}
