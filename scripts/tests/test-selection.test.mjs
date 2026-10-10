import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, mkdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { select, resolve, glob, changedFiles, parseArgs, parseDotnetDiscovery, parseBrowserDiscovery, validateDotnetFamilies, validateBrowserFamilies, dotnetFilter, browserTestList, manifest } from '../test-selection.mjs';

const includes = (selection, ...names) => names.forEach(name => assert.ok(selection.families.includes(name), name));

test('unknown production input falls back to full validation', () => {
  includes(select(['src/new-component/unknown.cs']), ...manifest.full);
});
test('empty automatic selection runs full validation', () => includes(select([]), ...manifest.full));
test('overlapping domain mappings union feature and consumer families', () => {
  includes(select(['src/hhnl.Formicae.Application/Integrations/IssueWorkflowEventDefinition.cs']), 'integrations', 'workflow');
});
test('persona mapping selects its consumers without unrelated deployment', () => {
  const selected = select(['src/hhnl.Formicae.Application/Workflows/PersonaService.cs']);
  assert.deepEqual(selected.families, ['personas']);
  assert.ok(resolve(selected).dotnet.includes('WorkflowOrchestrator'));
});
test('shared editor changes select all browser and workflow regression', () => {
  includes(select(['src/hhnl.Formicae.Api/ClientApp/src/workflowEditor/Inspector.tsx']), 'workflow', 'browser');
});
test('database and runtime changes retain mandatory Kubernetes verification', () => {
  includes(select(['src/hhnl.Formicae.Infrastructure/Persistence/FormicaeDbContext.cs']), 'backend', 'persistence', 'deployment');
  includes(select(['src/hhnl.Formicae.Worker/Program.cs']), 'worker', 'workflow', 'deployment');
});
test('mixed prose and consumed prompt selects consuming tests', () => {
  includes(select(['agent-os/product/features.md', 'src/hhnl.Formicae.Api/prompts/plan.md']), 'docs', 'worker', 'workflow', 'deployment');
});
test('operational prose selects deployment but ordinary docs only select docs', () => {
  assert.deepEqual(select(['agent-os/product/mission.md']).families, ['docs']);
  includes(select(['docs/kubernetes-deployment.md']), 'deployment', 'backend');
});
test('persistence selection includes classes located in catalog files', () => {
  const selected = select([], ['persistence']);
  validateDotnetFamilies(selected, ['hhnl.Formicae.Tests.PersonaPersistenceTests.RoundTrips', 'hhnl.Formicae.Tests.WorkflowMigrationTests.RoundTrips']);
  assert.match(dotnetFilter(resolve(selected).dotnet)[1], /PersistenceTests/);
});
test('unknown family and missing browser spec fail closed', () => {
  assert.throws(() => select([], ['typo']), /Unknown family/);
  assert.throws(() => resolve(select([], ['personas']), manifest, []), /missing/);
});
test('discovery rejects empty or malformed output and empty selected family', () => {
  assert.throws(() => parseDotnetDiscovery('Build succeeded'), /Unrecognized/);
  assert.throws(() => parseDotnetDiscovery('The following Tests are available:\n'), /No .NET/);
  const tests = parseDotnetDiscovery('The following Tests are available:\n    hhnl.Formicae.Tests.PersonaTests.RoundTrips\n');
  assert.equal(tests.length, 1);
  assert.throws(() => validateDotnetFamilies(select([], ['persistence']), tests), /No tests/);
  assert.throws(() => parseBrowserDiscovery('{"errors":[],"suites":[]}'), /No browser/);
  assert.throws(() => parseBrowserDiscovery('{"errors":[{"message":"bad"}]}'), /failed/);
});
test('browser discovery traverses nested suites and validates smoke tags', () => {
  const tests = parseBrowserDiscovery(JSON.stringify({ suites: [{ suites: [{ specs: [{ file: 'smoke.spec.ts', title: 'health', tags: ['smoke'] }] }] }] }));
  validateBrowserFamilies(select([], ['smoke']), tests);
  assert.throws(() => validateBrowserFamilies(select([], ['personas']), tests), /No browser tests/);
});
test('glob keeps single-star directory boundaries and escapes dots', () => {
  assert.ok(glob('src/**/Persona*', 'src/a/b/PersonaModels.cs'));
  assert.ok(!glob('docs/*.md', 'docs/sub/foo.md'));
  assert.ok(!glob('README.md', 'READMExmd'));
});
test('CLI rejects incomplete, conflicting or unknown options', () => {
  assert.throws(() => parseArgs(['--family']), /needs a value/);
  assert.throws(() => parseArgs(['--run', '--list']), /only one/);
  assert.throws(() => parseArgs(['--invalid']), /Unknown/);
  assert.deepEqual(parseArgs(['--base', 'origin/main', '--family', 'worker', '--run']).families, ['worker']);
});
test('git selection includes branch, staged, unstaged, renamed, deleted and untracked files with spaces', () => {
  const cwd = mkdtempSync(path.join(tmpdir(), 'formicae-selection-'));
  const git = (...args) => execFileSync('git', args, { cwd, encoding: 'utf8' });
  try {
    git('init', '-q'); git('config', 'user.email', 'selector@example.test'); git('config', 'user.name', 'Selector Test');
    for (const name of ['renamed.cs', 'deleted.cs', 'staged.cs', 'unstaged.cs', 'reverted.cs']) writeFileSync(path.join(cwd, name), 'original');
    git('add', '.'); git('commit', '-qm', 'base'); const base = git('rev-parse', 'HEAD').trim();
    writeFileSync(path.join(cwd, 'branch.cs'), 'new'); git('add', '.'); git('commit', '-qm', 'branch');
    git('mv', 'renamed.cs', 'new name.cs'); git('rm', '-q', 'deleted.cs');
    writeFileSync(path.join(cwd, 'staged.cs'), 'changed'); git('add', 'staged.cs');
    writeFileSync(path.join(cwd, 'reverted.cs'), 'staged change'); git('add', 'reverted.cs'); writeFileSync(path.join(cwd, 'reverted.cs'), 'original');
    writeFileSync(path.join(cwd, 'unstaged.cs'), 'changed');
    mkdirSync(path.join(cwd, 'new dir')); writeFileSync(path.join(cwd, 'new dir', 'untracked.cs'), 'new');
    assert.deepEqual(changedFiles(base, cwd), ['branch.cs', 'deleted.cs', 'new dir/untracked.cs', 'new name.cs', 'renamed.cs', 'reverted.cs', 'staged.cs', 'unstaged.cs']);
    assert.throws(() => changedFiles('does-not-exist', cwd), /failed/);
  } finally { rmSync(cwd, { recursive: true, force: true }); }
});

test('implicit smoke is required for a selected browser feature', () => {
  const selected = select([], ['personas']);
  const tests = ['personas', 'editor', 'execution'].map(s => ({file: `${s}.spec.ts`, title: s, tags: []}));
  assert.throws(() => validateBrowserFamilies(selected, tests), /@smoke/);
});
test('AI settings auth refresh and API startup retain all deployment consumers', () => {
  for (const file of ['src/hhnl.Formicae.Application/Workflows/AiSettingsService.cs', 'src/hhnl.Formicae.Application/Workflows/WorkerAgentAuthRefreshService.cs', 'src/hhnl.Formicae.Api/Program.cs']) includes(select([file]), 'backend', 'browser', 'deployment');
});
test('each persistence class group is required, including migrations', () => {
  assert.throws(() => validateDotnetFamilies(select([], ['persistence']), ['hhnl.Formicae.Tests.PersonaPersistenceTests.RoundTrips']), /WorkflowMigrationTests/);
});

 test('exact browser list distinguishes duplicate suffix titles and nested groups', () => {
   const tests = parseBrowserDiscovery(JSON.stringify({suites: [{title: 'editor.spec.ts', suites: [{title: 'one', specs: [{file:'editor.spec.ts', title:'save', tags:[]}]}, {title:'two', specs:[{file:'editor.spec.ts', title:'save', tags:[]}]}]}]}));
   assert.equal(browserTestList(tests), 'editor.spec.ts › one › save\neditor.spec.ts › two › save\n');
   assert.throws(() => browserTestList([{file:'a.spec.ts',titles:['bad\ntitle']}]), /Unsupported/);
 });

test('native Playwright JSON tags normalize without doubling the prefix', () => {
  const tests = parseBrowserDiscovery(JSON.stringify({suites:[{title:'smoke.spec.ts',specs:[{file:'smoke.spec.ts',title:'health',tags:['smoke','@existing']}]}]}));
  assert.deepEqual(tests[0].tags, ['@smoke','@existing']);
  validateBrowserFamilies(select([], ['smoke']), tests);
});

test('native test-list paths stay relative to Playwright rootDir, including subdirectories', () => {
  const tests = parseBrowserDiscovery(JSON.stringify({suites:[{title:'nested/example.spec.ts',specs:[{file:'nested/example.spec.ts',title:'health',tags:['smoke']}]}]}));
  assert.equal(browserTestList(tests),'nested/example.spec.ts › health\n');
});
