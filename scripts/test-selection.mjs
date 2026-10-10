#!/usr/bin/env node
import { readFileSync, readdirSync, existsSync, mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';

export const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const client = 'src/hhnl.Formicae.Api/ClientApp';
const backend = 'tests/hhnl.Formicae.Tests/hhnl.Formicae.Tests.csproj';
const kubernetes = 'tests/hhnl.Formicae.KubernetesE2ETests/hhnl.Formicae.KubernetesE2ETests.csproj';
export const manifest = JSON.parse(readFileSync(path.join(root, 'scripts/test-selection.json'), 'utf8'));

export function glob(pattern, value) {
  const regex = pattern.split('**').map(part => part.split('*').map(s => s.replace(/[.+?^${}()|[\]\\]/g, '\\$&')).join('[^/]*')).join('.*');
  return new RegExp(`^${regex}$`).test(value);
}

function command(program, args, { cwd = root, capture = false } = {}) {
  const result = spawnSync(program, args, { cwd, encoding: 'utf8', stdio: capture ? 'pipe' : 'inherit', env: { ...process.env, DOTNET_NOLOGO: '1', VSLANG: '1033' } });
  if (result.error || result.status !== 0) throw new Error(`${program} ${args.join(' ')} failed: ${result.error?.message ?? result.stderr ?? result.status}`);
  return result.stdout ?? '';
}

export function changedFiles(base, cwd = root) {
  const git = args => command('git', args, { cwd, capture: true });
  // --no-renames reports both old and new names, including deleted inputs.
  const committed = git(['diff', '--name-only', '-z', '--no-renames', `${base}...HEAD`]);
  const staged = git(['diff', '--cached', '--name-only', '-z', '--no-renames']);
  const working = git(['diff', '--name-only', '-z', '--no-renames']);
  const untracked = git(['ls-files', '--others', '--exclude-standard', '-z']);
  return [...new Set(`${committed}${staged}${working}${untracked}`.split('\0').filter(Boolean))].sort();
}

export function select(files, families = [], data = manifest) {
  const selected = new Set(families);
  const reasons = families.map(f => ({ file: '(explicit)', families: [f], reason: 'Requested family; focused validation only unless combined with --base' }));
  for (const file of files) {
    const matches = data.rules.filter(rule => rule.paths.some(p => glob(p, file)));
    const primary = matches.filter(rule => !rule.fallback);
    const applicable = primary.length ? primary : matches;
    if (!applicable.length) {
      for (const f of data.full) selected.add(f);
      reasons.push({ file, families: data.full, reason: 'Unmapped input: conservative full validation' });
    } else {
      for (const rule of applicable) {
        for (const f of rule.families) selected.add(f);
        reasons.push({ file, families: rule.families, reason: rule.reason });
      }
    }
  }
  if (!files.length && !families.length) {
    for (const f of data.full) selected.add(f);
    reasons.push({ file: '(no changes)', families: data.full, reason: 'No selection inputs: full validation; use --family for focused work' });
  }
  for (const family of selected) if (!data.families[family]) throw new Error(`Unknown family: ${family}`);
  return { families: [...selected].sort(), reasons };
}

export function resolve(selection, data = manifest, availableSpecs = readdirSync(path.join(root, client, 'tests/e2e'))) {
  const dotnet = new Set(), browser = new Set(), checks = new Set(['docs']);
  for (const name of selection.families) {
    const family = data.families[name];
    for (const token of family.dotnet ?? []) dotnet.add(token);
    for (const spec of family.browser ?? []) browser.add(spec);
    for (const check of family.checks ?? []) checks.add(check);
  }
  if (dotnet.has('*')) { dotnet.clear(); dotnet.add('*'); }
  if (browser.has('*')) { browser.clear(); browser.add('*'); }
  else if (browser.size && !browser.has('@smoke')) browser.add('@smoke');
  const specs = [...browser].filter(s => s !== '*' && s !== '@smoke');
  for (const spec of specs) if (!availableSpecs.includes(`${spec}.spec.ts`)) throw new Error(`Selected browser spec is missing: ${spec}`);
  return { dotnet: [...dotnet].sort(), browser: [...browser].sort(), checks: [...checks].sort() };
}

export function dotnetFilter(tokens) {
  return tokens.includes('*') ? [] : ['--filter', tokens.map(t => `FullyQualifiedName~${t}`).join('|')];
}

export function parseDotnetDiscovery(output) {
  const marker = 'The following Tests are available:';
  if (!output.includes(marker)) throw new Error('Unrecognized .NET discovery output; refusing to run');
  const tests = output.slice(output.indexOf(marker) + marker.length).split(/\r?\n/).filter(line => /^\s{4}\S/.test(line)).map(line => line.trim());
  if (!tests.length) throw new Error('No .NET tests discovered for the selected family');
  return tests;
}

export function validateDotnetFamilies(selection, tests, data = manifest) {
  for (const name of selection.families) {
    const tokens = data.families[name].dotnet;
    for (const token of tokens ?? []) if (!tests.some(test => token === '*' || test.toLowerCase().includes(token.toLowerCase()))) throw new Error(`No tests discovered for selected family ${name}: ${token}`);
  }
}

export function parseBrowserDiscovery(output) {
  const report = JSON.parse(output);
  if (report.errors?.length) throw new Error(`Browser discovery failed: ${JSON.stringify(report.errors)}`);
  const tests = [];
  const visit = (suites, parents = [], depth = 0) => {
    for (const suite of suites ?? []) {
      const titles = depth === 0 ? parents : [...parents, suite.title];
      for (const spec of suite.specs ?? []) tests.push({ file: spec.file ?? suite.file, title: spec.title, titles: [...titles, spec.title], tags: (spec.tags ?? []).map(tag => tag.startsWith('@') ? tag : `@${tag}`) });
      visit(suite.suites, titles, depth + 1);
    }
  };
  visit(report.suites);
  if (!tests.length) throw new Error('No browser tests discovered for the selected family');
  return tests;
}

export function validateBrowserFamilies(selection, tests, data = manifest, plan = resolve(selection, data)) {
  for (const spec of plan.browser) if (!tests.some(t => spec === '*' || (spec === '@smoke' ? t.tags.includes('@smoke') : t.file === `${spec}.spec.ts`))) throw new Error(`No browser tests discovered for resolved selection: ${spec}`);
  for (const name of selection.families) {
    const specs = data.families[name].browser;
    if (!specs?.length) continue;
    for (const spec of specs) if (!tests.some(t => spec === '*' || (spec === '@smoke' ? t.tags.includes('@smoke') : t.file === `${spec}.spec.ts`))) throw new Error(`No browser tests discovered for ${name}: ${spec}`);
  }
}

export function browserTestList(tests) {
  return tests.map(t => {
    if (t.titles.some(title => /[\r\n›>]/.test(title))) throw new Error('Unsupported browser title in exact test-list');
    return `${t.file} › ${t.titles.join(' › ')}`;
  }).join('\n') + '\n';
}

export function parseArgs(args) {
  const options = { mode: 'plan', families: [], files: [], base: undefined };
  let modeSet = false;
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (['--plan', '--list', '--run'].includes(arg)) {
      if (modeSet) throw new Error('Choose only one of --plan, --list or --run');
      options.mode = arg.slice(2); modeSet = true;
    } else if (['--base', '--family', '--file'].includes(arg)) {
      const value = args[++i];
      if (!value || value.startsWith('--')) throw new Error(`${arg} needs a value`);
      if (arg === '--base') options.base = value;
      else options[arg === '--family' ? 'families' : 'files'].push(value);
    } else if (arg === '--help') options.help = true;
    else throw new Error(`Unknown option: ${arg}`);
  }
  return options;
}

export function main(args) {
  const options = parseArgs(args);
  if (options.help) {
    console.log('Usage: node scripts/test-selection.mjs [--base origin/main] [--family NAME ...] [--file PATH ...] [--plan|--list|--run]\nDefault: explain branch + working-tree changes against origin/main. --family alone is focused iteration.\nFamilies: ' + Object.keys(manifest.families).join(', '));
    return;
  }
  const files = options.files.length ? options.files : (options.base || !options.families.length ? changedFiles(options.base ?? 'origin/main') : []);
  const selection = select(files, options.families);
  const plan = resolve(selection);
  console.log(`Selected families: ${selection.families.join(', ')}`);
  for (const reason of selection.reasons) console.log(`${reason.file}: ${reason.families.join(', ')} — ${reason.reason}`);
  const filter = dotnetFilter(plan.dotnet);
  if (plan.dotnet.length) console.log(`Backend: dotnet test ${backend} --no-build --no-restore --configuration Release ${filter.join(' ')}`);
  if (plan.browser.length) console.log(`Browser: ${plan.browser.join(', ')} (plus tagged smoke tests)`);
  console.log(`Checks: ${plan.checks.join(', ')}`);
  console.log(`Fast verification: ${plan.dotnet.length ? 'Release backend build; ' : ''}${plan.browser.length ? 'frontend build; ' : ''}git diff --check`);
  if (options.mode === 'plan') return;
  const started = Date.now();
  const timed = (label, fn) => { const begin = Date.now(); const result = fn(); console.log(`${label}: ${((Date.now() - begin) / 1000).toFixed(1)}s`); return result; };
  timed('Whitespace check', () => command('git', ['diff', '--check', 'HEAD']));
  if (plan.checks.includes('selector')) timed('Selector tests', () => command('node', ['--test', 'scripts/tests/test-selection.test.mjs']));
  if (plan.dotnet.length) {
    timed('Backend build', () => command('dotnet', ['build', backend, '--configuration', 'Release']));
    const tests = timed('Backend discovery', () => parseDotnetDiscovery(command('dotnet', ['test', backend, '--no-build', '--no-restore', '--configuration', 'Release', '--list-tests', ...filter], { capture: true })));
    validateDotnetFamilies(selection, tests);
    console.log(`Resolved backend tests (${tests.length}):\n${tests.join('\n')}`);
    if (options.mode === 'run') timed('Backend tests', () => command('dotnet', ['test', backend, '--no-build', '--no-restore', '--configuration', 'Release', ...filter]));
  }
  if (plan.browser.length) {
    const cwd = path.join(root, client);
    if (!existsSync(path.join(cwd, 'node_modules/.bin/playwright'))) throw new Error(`Frontend dependencies missing; run npm ci in ${client}`);
    timed('Frontend build', () => command('npm', ['run', 'build'], { cwd }));
    const tests = timed('Browser discovery', () => parseBrowserDiscovery(command('npm', ['exec', '--', 'playwright', 'test', '--list', '--reporter=json'], { cwd, capture: true })));
    validateBrowserFamilies(selection, tests, manifest, plan);
    const chosen = tests.filter(t => plan.browser.some(s => s === '*' || (s === '@smoke' ? t.tags.includes('@smoke') : t.file === `${s}.spec.ts`)));
    if (!chosen.length) throw new Error('No selected browser tests');
    console.log(`Resolved browser tests (${chosen.length}):\n${chosen.map(t => `${t.file}: ${t.title}`).join('\n')}`);
    const temp = mkdtempSync(path.join(tmpdir(), 'formicae-browser-selection-'));
    try {
      const list = path.join(temp, 'tests.txt');
      writeFileSync(list, browserTestList(chosen));
      const resolved = parseBrowserDiscovery(command('npm', ['exec', '--', 'playwright', 'test', '--list', '--reporter=json', '--test-list', list], { cwd, capture: true }));
      const identity = t => `${t.file} › ${t.titles.join(' › ')}`;
      if (JSON.stringify(resolved.map(identity).sort()) !== JSON.stringify(chosen.map(identity).sort())) throw new Error('Browser test-list did not resolve exactly to the selected tests');
      if (options.mode === 'run') timed('Browser tests', () => command('npm', ['exec', '--', 'playwright', 'test', '--test-list', list], { cwd }));
    } finally { rmSync(temp, { recursive: true, force: true }); }

  }
  if (plan.checks.includes('helm')) {
    if (options.mode === 'run') timed('Helm lint', () => command('helm', ['lint', 'deploy/helm/formicae']));
    else console.log('Deployment: helm lint deploy/helm/formicae');
  }
  if (plan.checks.includes('kubernetes')) {
    timed('Kubernetes build', () => command('dotnet', ['build', kubernetes, '--configuration', 'Release']));
    const tests = parseDotnetDiscovery(command('dotnet', ['test', kubernetes, '--no-build', '--no-restore', '--configuration', 'Release', '--list-tests'], { capture: true }));
    console.log(`Resolved Kubernetes tests (${tests.length}):\n${tests.join('\n')}`);
    if (options.mode === 'run') timed('Kubernetes E2E', () => command('bash', ['scripts/run-k8s-e2e.sh']));
  }
  console.log(`${options.mode === 'run' ? 'Validation' : 'Discovery'} completed in ${((Date.now() - started) / 1000).toFixed(1)}s`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { main(process.argv.slice(2)); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
