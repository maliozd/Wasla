// Tests for the CI job selection and the scope chosen after it (select-test-scope.ps1 with ci-job-rules.json), the
// final job's check of them (assert-ci-jobs.ps1, assert-test-shards.ps1), plus guards that fail when a dependency a
// focused rule relies on changes in the repository.
const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { runPowerShell } = require('./ci-test-support');

const repoRoot = path.resolve(__dirname, '..', '..');
const selectScript = path.join(__dirname, 'select-test-scope.ps1');
const jobsScript = path.join(__dirname, 'assert-ci-jobs.ps1');
const accountingScript = path.join(__dirname, 'assert-test-shards.ps1');
const rules = JSON.parse(fs.readFileSync(path.join(__dirname, 'ci-job-rules.json'), 'utf8'));

const projectJob = { 'Wasla.UnitTests': 'unit-tests', 'Wasla.PrintBridge.Tests': 'print-bridge' };
const UT = 'unit-tests';
const PB = 'print-bridge';

function tempDir(prefix) {
  return fs.mkdtempSync(path.join(os.tmpdir(), prefix));
}

// The plan for an event. changes: name-status lines ("M\tpath"), or undefined for the event's own diff (payload and
// repository, or neither).
function planFor({ event = 'pull_request', base = 'dev', ref = '', changes, payload, repo } = {}) {
  const env = {};
  let args = `-EventName '${event}' -BaseRef '${base}' -RefName '${ref}' -PlanOnly`;
  if (changes) {
    env.CHANGES = changes.join('\n');
    args += ' -ChangeList ([string[]]@($env:CHANGES -split [char]10 | Where-Object { $_ }))';
  }
  let dir;
  if (payload) {
    dir = tempDir('wasla-ci-event-');
    const file = path.join(dir, 'event.json');
    fs.writeFileSync(file, JSON.stringify(payload));
    args += ` -EventPath '${file}'`;
  }
  if (repo) args += ` -RepositoryRoot '${repo}'`;
  try {
    const result = runPowerShell(`& '${selectScript}' ${args}`, env);
    assert.equal(result.status, 0, result.stderr + result.stdout);
    return JSON.parse(result.stdout.trim());
  } finally {
    if (dir) fs.rmSync(dir, { recursive: true, force: true });
  }
}

// The Windows jobs a selection runs, and the .NET projects its plan checks.
function windowsJobs(selection) {
  return Object.entries(selection.jobs).filter(([job, entry]) => [UT, PB].includes(job) && entry.run).map(([job]) => job);
}

const browserShards = ['browser-1', 'browser-2', 'browser-3'];

// The scope, checked on its own: the plan's scope and the selection's agree, and every planned project runs exactly
// the scope's tests (the browser shards and the WebView2 runtime tests only in the full scope).
function assertScope(testPlan, scope, fullScopeBy) {
  assert.equal(testPlan.scope, scope, JSON.stringify(testPlan.selection));
  assert.equal(testPlan.selection.scope, scope);
  if (scope === 'fast') assert.equal(testPlan.selection.fullScopeForced, 0);
  for (const project of testPlan.projects) {
    const shards = project.shards.map(s => s.id);
    if (project.project === 'Wasla.UnitTests') {
      assert.deepEqual(shards, ['nonbrowser-1', 'nonbrowser-2', ...(scope === 'full' ? browserShards : [])]);
      assert.deepEqual(project.scopeExcludesTraits, scope === 'full' ? [] : ['Category=Browser']);
    } else {
      assert.deepEqual(project.scopeExcludesTraits, scope === 'full' ? [] : ['Category=WebView2Runtime']);
      assert.equal(project.shards[0].filter, scope === 'full' ? '' : 'Category!=WebView2Runtime');
    }
  }
  if (fullScopeBy) assert.match(testPlan.selection.fullScopeBy.join('\n'), fullScopeBy);
}

function assertSelection(testPlan, { mode, jobs, projects, rule, forced, scope, fullScopeBy }) {
  const selection = testPlan.selection;
  assert.equal(selection.mode, mode, JSON.stringify(selection));
  assert.deepEqual(windowsJobs(selection), jobs, JSON.stringify(selection));
  assert.equal(selection.jobs['terminology-guard'].run, true);
  assert.equal(selection.jobs['javascript-tests'].run, true);
  assert.deepEqual(testPlan.projects.map(p => p.project), projects ?? jobs.map(job => Object.keys(projectJob).find(p => projectJob[p] === job)));
  if (rule) assert.deepEqual(selection.rules.map(r => r.id), rule);
  if (forced) assert.match(selection.forcedBy.join('\n'), forced);
  if (scope) assertScope(testPlan, scope, fullScopeBy);
}

// ---- Rules, one area or several: the jobs, then the scope ---------------------------------------------------------

const focused = [
  ['documentation only', ['M\tdocs/README.md', 'M\tdocs/product/terminology.md', 'A\tdocs/brand/new-sketch.svg'], [], ['documentation'], 'fast'],
  ['root and agent instructions', ['M\tREADME.md', 'M\tAGENTS.md', 'M\tCLAUDE.md', 'M\t.claude/rules/web-ui.md'], [], ['documentation'], 'fast'],
  ['the testing guide, which no test reads', ['M\tdocs/operations/testing.md'], [], ['documentation'], 'fast'],
  ['a document Wasla.UnitTests reads', ['M\tdocs/operations/migrations.md'], [UT], ['docs-read-by-unit-tests'], 'fast'],
  ['a document Wasla.PrintBridge.Tests reads', ['M\tdocs/integrations/print-bridge-service.md'], [PB], ['docs-read-by-print-bridge-tests'], 'fast'],
  ['Wasla.UnitTests sources and Node tests', ['M\ttests/Wasla.UnitTests/Auth/LoginTests.cs', 'A\ttests/Wasla.UnitTests/Signup/signup-steps.test.js'], [UT], ['unit-tests-project'], 'fast'],
  ['Wasla.PrintBridge.Tests sources outside the web shell', ['M\ttests/Wasla.PrintBridge.Tests/Printing/PrintJobPollerTests.cs'], [PB], ['print-bridge-tests-project'], 'fast'],
  ['the Print Bridge installer sources', ['M\tinstaller/print-bridge/Package/Package.wxs'], [PB], ['print-bridge-installer'], 'fast'],
  ['the server hosts, which neither deferred test group starts', ['M\tsrc/Wasla.Api/Program.cs', 'M\tsrc/Wasla.Cli/Program.cs', 'M\tsrc/Wasla.Worker/Jobs/OrderSyncWorker.cs'], [UT], ['server-hosts'], 'fast'],
  ['the contracts, which Wasla.Web builds on', ['M\tsrc/Wasla.Contracts/Orders/OrderDto.cs'], [UT], ['contracts'], 'full'],
  ['a server host and the contracts', ['M\tsrc/Wasla.Api/Program.cs', 'M\tsrc/Wasla.Contracts/Orders/OrderDto.cs'], [UT], ['server-hosts', 'contracts'], 'full'],
  ['documentation and Wasla.UnitTests: the union', ['M\tdocs/README.md', 'M\ttests/Wasla.UnitTests/Auth/LoginTests.cs'], [UT], ['documentation', 'unit-tests-project'], 'fast'],
  ['both test projects: the union', ['M\ttests/Wasla.UnitTests/A.cs', 'M\ttests/Wasla.PrintBridge.Tests/B.cs'], [UT, PB], ['unit-tests-project', 'print-bridge-tests-project'], 'fast'],
  ['a server host and the installer: the union', ['M\tsrc/Wasla.Worker/Program.cs', 'M\tinstaller/print-bridge/Bundle/Bundle.wxs', 'M\tdocs/README.md'], [UT, PB], ['server-hosts', 'print-bridge-installer', 'documentation'], 'fast'],
  ['a headless-browser test', ['M\ttests/Wasla.UnitTests/Web/OrderDetailModalBrowserTests.cs'], [UT], ['browser-tests'], 'full'],
  ['a headless-browser test whose name does not end in BrowserTests', ['M\ttests/Wasla.UnitTests/Admin/HeadlessChromiumNavigationTests.cs'], [UT], ['browser-tests'], 'full'],
  ['the headless-browser driver', ['M\ttests/Wasla.UnitTests/Admin/HeadlessChromium.cs', 'M\ttests/Wasla.UnitTests/Admin/BrowserProcessTree.cs'], [UT], ['browser-tests'], 'full'],
  ['a WebView2 runtime test', ['M\ttests/Wasla.PrintBridge.Tests/WebShell/ShellWebViewRuntimeTests.cs'], [PB], ['webview2-runtime-tests'], 'full'],
  ['the web shell tests\' host', ['M\ttests/Wasla.PrintBridge.Tests/WebShell/TrayTestHost.cs'], [PB], ['webview2-runtime-tests'], 'full'],
  ['a headless-browser test and documentation', ['M\tdocs/README.md', 'A\ttests/Wasla.UnitTests/Web/NewPageBrowserTests.cs'], [UT], ['documentation', 'browser-tests'], 'full'],
  ['a headless-browser test and other unit tests', ['M\ttests/Wasla.UnitTests/Auth/LoginTests.cs', 'M\ttests/Wasla.UnitTests/Signup/SignupFlowBrowserTests.cs'], [UT], ['unit-tests-project', 'browser-tests'], 'full'],
  ['a headless-browser test and a Print Bridge test: the union', ['M\ttests/Wasla.UnitTests/Web/ReportsPageBrowserTests.cs', 'M\ttests/Wasla.PrintBridge.Tests/B.cs'], [UT, PB], ['browser-tests', 'print-bridge-tests-project'], 'full'],
];

for (const [name, changes, jobs, rule, scope] of focused) {
  test(`a pull request into dev that changes ${name} runs ${jobs.length ? jobs.join(' and ') : 'no .NET job'}, in the ${scope} scope`, () => {
    assertSelection(planFor({ changes }), { mode: 'focused', jobs, rule, scope });
  });
}

const everything = [
  ['Wasla.Web Razor views (tenant UI)', ['M\tsrc/Wasla.Web/Areas/Tenant/Views/Orders/_OrdersTable.cshtml'], /rule web needs every job/, 'full', /rule web needs the full scope/],
  ['Wasla.Web JavaScript and static files', ['M\tsrc/Wasla.Web/wwwroot/js/live-screen.js', 'M\tsrc/Wasla.Web/wwwroot/css/site.css'], /rule web needs every job/, 'full'],
  ['Wasla.Web authentication and authorization', ['M\tsrc/Wasla.Web/Program.cs'], /rule web needs every job/, 'full'],
  ['Domain, Application and Infrastructure', ['M\tsrc/Wasla.Domain/Orders/Order.cs'], /rule shared-libraries needs every job/, 'full', /rule shared-libraries needs the full scope/],
  ['an EF migration', ['A\tsrc/Wasla.Infrastructure/Persistence/Migrations/Tenant/20261124_Add.cs'], /rule shared-libraries needs every job/, 'full'],
  ['tenant isolation in Infrastructure', ['M\tsrc/Wasla.Infrastructure/Tenancy/TenantDbContextFactory.cs'], /rule shared-libraries needs every job/, 'full'],
  ['the Print Bridge engine', ['M\tsrc/Wasla.PrintBridge.Core/Services/WaslaPrintBridgeClient.cs'], /rule print-bridge-app needs every job/, 'full', /rule print-bridge-app needs the full scope/],
  ['the Print Bridge tray app and its resources', ['M\tsrc/Wasla.PrintBridge/Resources/PrintBridgeResources.ru-RU.resx'], /rule print-bridge-app needs every job/, 'full'],
  ['the Print Bridge Windows Service, which the WebView2 runtime tests do not start', ['M\tsrc/Wasla.PrintBridge.Service/Program.cs'], /rule print-bridge-service needs every job/, 'fast'],
  ['the Print Bridge Windows Service and documentation', ['M\tsrc/Wasla.PrintBridge.Service/Program.cs', 'M\tdocs/README.md'], /rule print-bridge-service needs every job/, 'fast'],
  ['the Print Bridge version', ['M\tsrc/Wasla.PrintBridge.Version.props'], /rule ci-and-build needs every job/, 'full', /rule ci-and-build needs the full scope \(src\/Wasla\.PrintBridge\.Version\.props\)/],
  ['a project file inside a focused folder', ['M\ttests/Wasla.UnitTests/Wasla.UnitTests.csproj'], /rule ci-and-build needs every job/, 'full'],
  ['an MSBuild file inside the installer folder', ['M\tinstaller/print-bridge/Directory.Build.props'], /rule ci-and-build needs every job/, 'full'],
  ['the solution', ['M\tWasla.sln'], /rule ci-and-build needs every job/, 'full'],
  ['the CI workflow', ['M\t.github/workflows/ci.yml'], /rule ci-and-build needs every job/, 'full', /rule ci-and-build needs the full scope \(\.github\/workflows\/ci\.yml\)/],
  ['the planner and its rules', ['M\tscripts/ci/select-test-scope.ps1', 'M\tscripts/ci/ci-job-rules.json'], /rule ci-and-build needs every job/, 'full', /rule ci-and-build needs the full scope \(scripts\/ci\/select-test-scope\.ps1\)/],
  ['the release scripts', ['M\tscripts/release/package-print-bridge.ps1'], /rule ci-and-build needs every job/, 'full'],
  ['Git attributes', ['M\t.gitattributes'], /rule ci-and-build needs every job/, 'full'],
  ['the deployment scripts', ['M\tdeploy/linux/publish-release.ps1'], /rule deployment needs every job/, 'full', /rule deployment needs the full scope/],
  ['the report export smoke test', ['M\ttests/Wasla.ReportExport.LinuxSmoke/Program.cs'], /rule report-export-smoke needs every job/, 'full'],
  ['a path no rule knows', ['A\ttools/new-tool.ps1'], /no rule matches tools\/new-tool\.ps1/, 'full', /no rule matches tools\/new-tool\.ps1/],
  ['a new top-level file', ['A\tLICENSE'], /no rule matches LICENSE/, 'full'],
  ['a path that differs only in case from a focused folder', ['M\tDocs/README.md'], /no rule matches Docs\/README\.md/, 'full'],
  ['documentation together with Wasla.Web', ['M\tdocs/README.md', 'M\tsrc/Wasla.Web/wwwroot/js/a.js'], /rule web needs every job/, 'full'],
  ['the Print Bridge Windows Service together with Wasla.Web', ['M\tsrc/Wasla.PrintBridge.Service/Program.cs', 'M\tsrc/Wasla.Web/Program.cs'], /rule print-bridge-service needs every job/, 'full', /rule web needs the full scope/],
  ['Wasla.Web together with a headless-browser test', ['M\tsrc/Wasla.Web/wwwroot/js/live-screen.js', 'M\ttests/Wasla.UnitTests/Web/LiveScreenFirstLoadBrowserTests.cs'], /rule web needs every job/, 'full', /rule browser-tests needs the full scope/],
  ['Wasla.Web together with the CI workflow', ['M\tsrc/Wasla.Web/Program.cs', 'M\t.github/workflows/ci.yml'], /rule web needs every job/, 'full', /rule ci-and-build needs the full scope/],
  ['documentation together with a path no rule knows', ['M\tdocs/README.md', 'A\tnotes/todo.txt'], /no rule matches notes\/todo\.txt/, 'full'],
  ['a deleted document', ['D\tdocs/README.md'], /other than an addition or modification: deleted docs\/README\.md/, 'full', /deleted docs\/README\.md/],
  ['a renamed document', ['R100\tdocs/a.md\tdocs/b.md'], /renamed docs\/a\.md to docs\/b\.md/, 'full', /renamed docs\/a\.md to docs\/b\.md/],
  ['a copied test file', ['C090\ttests/Wasla.UnitTests/A.cs\ttests/Wasla.UnitTests/B.cs'], /copied tests\/Wasla\.UnitTests\/A\.cs to tests\/Wasla\.UnitTests\/B\.cs/, 'full'],
  ['a file whose type changed', ['T\tdocs/README.md'], /changed the type of docs\/README\.md/, 'full'],
  ['nothing (an empty diff)', [], /the diff is empty/, 'full', /the diff is empty/],
];

for (const [name, changes, forced, scope, fullScopeBy] of everything) {
  test(`a pull request into dev that changes ${name} runs every job, in the ${scope} scope`, () => {
    assertSelection(planFor({ changes }), { mode: 'all', jobs: [UT, PB], forced, scope, fullScopeBy });
  });
}

// The two decisions are independent: each combination of focused or every job with the fast or full scope occurs.
for (const [changes, mode, scope] of [
  [['M\tdocs/README.md'], 'focused', 'fast'],
  [['M\ttests/Wasla.UnitTests/Web/OrderDetailModalBrowserTests.cs'], 'focused', 'full'],
  [['M\tsrc/Wasla.PrintBridge.Service/Program.cs'], 'all', 'fast'],
  [['M\t.github/workflows/ci.yml'], 'all', 'full'],
]) {
  test(`the scope is chosen after the jobs: ${changes[0].split('\t')[1]} gets ${mode === 'all' ? 'every job' : 'focused jobs'} and the ${scope} scope`, () => {
    const testPlan = planFor({ changes });
    assert.equal(testPlan.selection.mode, mode);
    assertScope(testPlan, scope);
  });
}

test('the scope reason says why a pull request into dev gets the full scope, or that nothing needs it', () => {
  let testPlan = planFor({ changes: ['M\tdocs/README.md', 'M\tscripts/ci/select-test-scope.ps1', 'M\tscripts/ci/ci-job-rules.json'] });
  assert.equal(testPlan.reason, 'pull request into dev whose changes need the full scope: rule ci-and-build needs the full scope (scripts/ci/select-test-scope.ps1) (and 1 more); every test runs');
  assert.deepEqual(testPlan.selection.rules.map(r => [r.id, r.scope]), [['documentation', 'fast'], ['ci-and-build', 'full']]);
  testPlan = planFor({ changes: ['M\tsrc/Wasla.Api/Program.cs'] });
  assert.match(testPlan.reason, /^pull request into dev, and no changed file needs the full scope; the headless-browser and WebView2 runtime tests run after the merge to dev/);
  // An untrusted change comes first, even after a rule's finding.
  testPlan = planFor({ changes: ['M\t.github/workflows/ci.yml', 'D\tdocs/a.md'] });
  assert.match(testPlan.reason, /need the full scope: a change other than an addition or modification: deleted docs\/a\.md \(and 1 more\)/);
});

test('every job that runs and every job that is skipped says why', () => {
  const selection = planFor({ changes: ['M\tdocs/README.md', 'M\ttests/Wasla.UnitTests/A.cs'] }).selection;
  assert.equal(selection.jobs[UT].why, 'needed by rule unit-tests-project');
  assert.equal(selection.jobs[PB].why, 'no changed file needs it (rules: documentation, unit-tests-project)');
  assert.deepEqual(selection.rules.map(r => [r.id, r.jobs, r.files]), [['documentation', [], 1], ['unit-tests-project', [UT], 1]]);
});

// ---- Events ------------------------------------------------------------------------------------------------------

test('a manual run runs every job and the full scope, whatever changed: it is the way to force everything', () => {
  const testPlan = planFor({ event: 'workflow_dispatch', base: '', changes: ['M\tdocs/README.md'] });
  assertSelection(testPlan, { mode: 'all', jobs: [UT, PB], scope: 'full' });
  assert.match(testPlan.selection.basis, /every job runs for workflow_dispatch/);
  assert.equal(testPlan.reason, 'workflow_dispatch; every test runs');
  assert.equal(testPlan.observation, null);
});

for (const [event, base, ref] of [['pull_request', 'master', ''], ['push', '', 'master'], ['merge_group', '', ''], ['', '', '']]) {
  test(`${event || 'an unknown event'}${base ? ` into ${base}` : ''}${ref ? ` to ${ref}` : ''} runs every job and the full scope, even for a documentation change`, () => {
    const testPlan = planFor({ event, base, ref, changes: ['M\tdocs/README.md'] });
    assertSelection(testPlan, { mode: 'all', jobs: [UT, PB], scope: 'full' });
    assert.equal(testPlan.observation, null);
  });
}

test('a push to dev runs every job and the full scope, and records what the rules would have selected (observation phase)', () => {
  let testPlan = planFor({ event: 'push', base: '', ref: 'dev', changes: ['M\tdocs/README.md', 'M\ttests/Wasla.PrintBridge.Tests/A.cs'] });
  assertSelection(testPlan, { mode: 'all', jobs: [UT, PB], scope: 'full' });
  assert.match(testPlan.selection.basis, /observation phase/);
  assert.equal(testPlan.observation.mode, 'focused');
  assert.equal(testPlan.observation.scope, 'fast');
  assert.deepEqual(windowsJobs(testPlan.observation), [PB]);
  assert.deepEqual(testPlan.observation.fastScopeLeavesOut, [
    { project: 'Wasla.UnitTests', job: UT, traits: ['Category=Browser'] },
    { project: 'Wasla.PrintBridge.Tests', job: PB, traits: ['Category=WebView2Runtime'] },
  ]);

  testPlan = planFor({ event: 'push', base: '', ref: 'dev', changes: ['M\tsrc/Wasla.PrintBridge.Service/Program.cs'] });
  assert.deepEqual([testPlan.scope, testPlan.observation.mode, testPlan.observation.scope], ['full', 'all', 'fast']);
  testPlan = planFor({ event: 'push', base: '', ref: 'dev', changes: ['M\tsrc/Wasla.Web/Program.cs'] });
  assert.deepEqual([testPlan.scope, testPlan.observation.mode, testPlan.observation.scope], ['full', 'all', 'full']);
  testPlan = planFor({ event: 'push', base: '', ref: 'dev', changes: ['M\tscripts/ci/ci-job-rules.json'] });
  assert.deepEqual([testPlan.scope, testPlan.observation.mode, testPlan.observation.scope], ['full', 'all', 'full']);
  testPlan = planFor({ event: 'push', base: '', ref: 'dev', changes: ['M\ttests/Wasla.UnitTests/Web/ReportsPageBrowserTests.cs'] });
  assert.deepEqual([testPlan.scope, testPlan.observation.mode, testPlan.observation.scope], ['full', 'focused', 'full']);
});

test('a pull request into dev without an event payload runs every job and the full scope', () => {
  assertSelection(planFor({}), { mode: 'all', jobs: [UT, PB], forced: /the event payload is not available/, scope: 'full', fullScopeBy: /the event payload is not available/ });
});

// ---- Git diffs ---------------------------------------------------------------------------------------------------

function git(repo, ...args) {
  const result = spawnSync('git', args, { cwd: repo, encoding: 'utf8' });
  assert.equal(result.status, 0, `git ${args.join(' ')}: ${result.stderr}`);
  return result.stdout.trim();
}

function commit(repo, files, message) {
  for (const [file, content] of Object.entries(files)) {
    const full = path.join(repo, file);
    if (content === null) {
      fs.rmSync(full);
    } else {
      fs.mkdirSync(path.dirname(full), { recursive: true });
      fs.writeFileSync(full, content);
    }
  }
  git(repo, 'add', '-A');
  git(repo, 'commit', '-q', '-m', message);
  return git(repo, 'rev-parse', 'HEAD');
}

function withRepo(body) {
  const repo = tempDir('wasla-ci-repo-');
  try {
    git(repo, 'init', '-q');
    git(repo, 'config', 'user.email', 'ci-test@example.invalid');
    git(repo, 'config', 'user.name', 'CI test');
    git(repo, 'config', 'commit.gpgsign', 'false');
    const root = commit(repo, {
      'docs/README.md': 'docs\n',
      'docs/a.md': 'a\n',
      'src/Wasla.Web/Program.cs': 'web\n',
      'tests/Wasla.UnitTests/A.cs': 'unit\n',
      'tests/Wasla.PrintBridge.Tests/B.cs': 'bridge\n',
    }, 'root');
    body(repo, root);
  } finally {
    fs.rmSync(repo, { recursive: true, force: true });
  }
}

test('a pull request is diffed from the merge-base, so changes that reached its base after it branched do not count', () => {
  withRepo((repo, root) => {
    const head = commit(repo, { 'docs/README.md': 'docs, edited\n' }, 'pull request: documentation');
    git(repo, 'checkout', '-q', '-b', 'base', root);
    const baseTip = commit(repo, { 'src/Wasla.Web/Program.cs': 'web, edited on dev\n' }, 'dev moved on');
    const testPlan = planFor({ repo, payload: { pull_request: { base: { sha: baseTip }, head: { sha: head } } } });
    assertSelection(testPlan, { mode: 'focused', jobs: [], rule: ['documentation'], scope: 'fast' });
    assert.match(testPlan.selection.basis, new RegExp(`merge-base ${root.slice(0, 12)} to head ${head.slice(0, 12)}`));
  });
});

test('a push of several commits is diffed from before to after: every commit counts', () => {
  withRepo((repo, root) => {
    commit(repo, { 'docs/README.md': 'docs, edited\n' }, 'first');
    const after = commit(repo, { 'tests/Wasla.UnitTests/A.cs': 'unit, edited\n' }, 'second');
    const testPlan = planFor({ event: 'push', base: '', ref: 'dev', repo, payload: { before: root, after } });
    assertSelection(testPlan, { mode: 'all', jobs: [UT, PB] });
    assert.equal(testPlan.observation.mode, 'focused');
    assert.deepEqual(windowsJobs(testPlan.observation), [UT]);
    assert.deepEqual(testPlan.observation.rules.map(r => r.id), ['documentation', 'unit-tests-project']);
    assert.equal(testPlan.observation.files, 2);
    assert.equal(testPlan.observation.scope, 'fast');
  });
});

test('a pull request with renamed and deleted files runs every job', () => {
  withRepo((repo, root) => {
    const head = commit(repo, { 'docs/a.md': null, 'docs/b.md': 'a\n', 'tests/Wasla.UnitTests/A.cs': null }, 'rename and delete');
    const testPlan = planFor({ repo, payload: { pull_request: { base: { sha: root }, head: { sha: head } } } });
    assertSelection(testPlan, { mode: 'all', jobs: [UT, PB], forced: /renamed docs\/a\.md to docs\/b\.md/, scope: 'full', fullScopeBy: /deleted tests\/Wasla\.UnitTests\/A\.cs/ });
    assert.match(testPlan.selection.forcedBy.join('\n'), /deleted tests\/Wasla\.UnitTests\/A\.cs/);
  });
});

test('a pull request whose head is already in its base has an empty diff and runs every job', () => {
  withRepo((repo, root) => {
    const tip = commit(repo, { 'docs/README.md': 'docs, edited\n' }, 'already merged');
    assertSelection(planFor({ repo, payload: { pull_request: { base: { sha: tip }, head: { sha: root } } } }), { mode: 'all', jobs: [UT, PB], forced: /the diff is empty/, scope: 'full' });
  });
});

test('a commit missing from the fetched history (a shallow clone or a force push) runs every job', () => {
  withRepo((repo, root) => {
    const missing = 'f'.repeat(40);
    let testPlan = planFor({ repo, payload: { pull_request: { base: { sha: missing }, head: { sha: root } } } });
    assertSelection(testPlan, { mode: 'all', jobs: [UT, PB], forced: /the previous commit f{40} is not in the fetched history/, scope: 'full' });

    const shallow = tempDir('wasla-ci-shallow-');
    try {
      const head = commit(repo, { 'docs/README.md': 'docs, edited\n' }, 'pull request');
      // A file:// URL: a plain local path ignores --depth.
      const url = `file://${repo.startsWith('/') ? '' : '/'}${repo.replace(/\\/g, '/')}`;
      const cloned = spawnSync('git', ['clone', '-q', '--depth', '1', url, shallow], { encoding: 'utf8' });
      assert.equal(cloned.status, 0, cloned.stderr);
      testPlan = planFor({ repo: shallow, payload: { pull_request: { base: { sha: root }, head: { sha: head } } } });
      assertSelection(testPlan, { mode: 'all', jobs: [UT, PB], forced: /not in the fetched history/, scope: 'full', fullScopeBy: /not in the fetched history/ });
    } finally {
      fs.rmSync(shallow, { recursive: true, force: true });
    }
  });
});

test('a push without a previous commit (a new branch) records that every job would run', () => {
  withRepo((repo, root) => {
    const testPlan = planFor({ event: 'push', base: '', ref: 'dev', repo, payload: { before: '0'.repeat(40), after: root } });
    assertSelection(testPlan, { mode: 'all', jobs: [UT, PB] });
    assert.equal(testPlan.observation.mode, 'all');
    assert.equal(testPlan.observation.scope, 'full');
    assert.match(testPlan.observation.forcedBy.join('\n'), /the changes could not be computed: the previous commit is unknown/);
  });
});

test('a diff git cannot compute (not a repository) runs every job', () => {
  const notARepo = tempDir('wasla-ci-norepo-');
  try {
    const testPlan = planFor({ repo: notARepo, payload: { pull_request: { base: { sha: 'a'.repeat(40) }, head: { sha: 'b'.repeat(40) } } } });
    assertSelection(testPlan, { mode: 'all', jobs: [UT, PB], forced: /the changes could not be computed/, scope: 'full' });
  } finally {
    fs.rmSync(notARepo, { recursive: true, force: true });
  }
});

// ---- The final job's check of the jobs ---------------------------------------------------------------------------

function needsFor(results) {
  return Object.fromEntries(Object.entries(results).map(([job, result]) => [job, { result, outputs: {} }]));
}

function checkJobs(testPlan, results) {
  const result = runPowerShell(`& '${jobsScript}' -Plan $env:TEST_PLAN -Needs $env:JOB_RESULTS; exit $LASTEXITCODE`, {
    TEST_PLAN: JSON.stringify(testPlan),
    JOB_RESULTS: JSON.stringify(needsFor({ plan: 'success', ...results })),
  });
  return { status: result.status, output: result.stdout + result.stderr };
}

const docsOnly = planFor({ changes: ['M\tdocs/README.md'] });
const unitOnly = planFor({ changes: ['M\ttests/Wasla.UnitTests/A.cs'] });
const allJobs = planFor({ changes: ['M\tsrc/Wasla.Web/Program.cs'] });
const passing = { 'terminology-guard': 'success', 'javascript-tests': 'success' };

test('the final job passes when the planned jobs passed and the others were skipped on purpose', () => {
  let result = checkJobs(docsOnly, { ...passing, [UT]: 'skipped', [PB]: 'skipped' });
  assert.equal(result.status, 0, result.output);
  assert.match(result.output, /\| `unit-tests` \| skipped on purpose \| skipped \| no changed file needs it \(rules: documentation\) \|/);
  result = checkJobs(allJobs, { ...passing, [UT]: 'success', [PB]: 'success' });
  assert.equal(result.status, 0, result.output);
});

for (const outcome of ['failure', 'cancelled', 'skipped']) {
  test(`the final job fails when a planned job's result is ${outcome}`, () => {
    const result = checkJobs(unitOnly, { ...passing, [UT]: outcome, [PB]: 'skipped' });
    assert.equal(result.status, 1, result.output);
    assert.match(result.output, new RegExp(`unit-tests was planned to run and pass, but its result is ${outcome}`));
  });
}

test('the final job fails when the terminology guard or the Node tests did not pass', () => {
  const result = checkJobs(docsOnly, { 'terminology-guard': 'failure', 'javascript-tests': 'skipped', [UT]: 'skipped', [PB]: 'skipped' });
  assert.equal(result.status, 1, result.output);
  assert.match(result.output, /terminology-guard was planned to run and pass, but its result is failure/);
  assert.match(result.output, /javascript-tests was planned to run and pass, but its result is skipped/);
});

test('the final job fails when a job the plan left out ran, or a job is missing from its needs', () => {
  let result = checkJobs(unitOnly, { ...passing, [UT]: 'success', [PB]: 'success' });
  assert.equal(result.status, 1, result.output);
  assert.match(result.output, /print-bridge was not planned, but its result is success/);
  result = checkJobs(unitOnly, { ...passing, [UT]: 'success' });
  assert.equal(result.status, 1, result.output);
  assert.match(result.output, /print-bridge was not planned, but its result is missing/);
});

test('on a push to dev the final job reports a failure in a job the rules would have skipped', () => {
  const pushPlan = planFor({ event: 'push', base: '', ref: 'dev', changes: ['M\tdocs/README.md'] });
  let result = checkJobs(pushPlan, { ...passing, [UT]: 'success', [PB]: 'success' });
  assert.equal(result.status, 0, result.output);
  assert.match(result.output, /\| `print-bridge` \| success \| no \|/);
  assert.doesNotMatch(result.output, /would have missed/);

  result = checkJobs(pushPlan, { ...passing, [UT]: 'success', [PB]: 'failure' });
  assert.equal(result.status, 1, result.output);
  assert.match(result.output, /\| `print-bridge` \| failure \| \*\*yes\*\* \|/);
  assert.match(result.output, /The rules would have skipped print-bridge for these changes, and it failed/);
});

test('the test accounting of a plan without .NET projects passes without results and fails on a stray one', () => {
  const root = tempDir('wasla-ci-results-');
  try {
    let result = runPowerShell(`& '${accountingScript}' -Plan $env:TEST_PLAN -ResultsRoot '${path.join(root, 'none')}' -Commit '${'a'.repeat(40)}'; exit $LASTEXITCODE`, { TEST_PLAN: JSON.stringify(docsOnly) });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.match(result.stdout, /the plan has no \.NET test job/);

    const folder = path.join(root, 'dotnet-test-results-print-bridge');
    fs.mkdirSync(folder, { recursive: true });
    fs.writeFileSync(path.join(folder, 'test-shard.json'), JSON.stringify({ project: 'Wasla.PrintBridge.Tests', shard: 'print-bridge', scope: 'fast', filter: '', commit: 'a'.repeat(40) }));
    result = runPowerShell(`& '${accountingScript}' -Plan $env:TEST_PLAN -ResultsRoot '${root}' -Commit '${'a'.repeat(40)}'; exit $LASTEXITCODE`, { TEST_PLAN: JSON.stringify(docsOnly) });
    assert.equal(result.status, 1, result.stdout + result.stderr);
    assert.match(result.stdout, /belongs to Wasla\.PrintBridge\.Tests\/print-bridge, which this run's plan does not have/);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('the final job shows the planned scope apart from the planned jobs, and on a push to dev the scope the rules would choose', () => {
  let result = checkJobs(planFor({ changes: ['M\t.github/workflows/ci.yml'] }), { ...passing, [UT]: 'success', [PB]: 'success' });
  assert.equal(result.status, 0, result.output);
  assert.match(result.output, /\*\*Planned scope: full\*\* \(pull request into dev whose changes need the full scope: rule ci-and-build needs the full scope \(\.github\/workflows\/ci\.yml\); every test runs\)/);
  assert.match(result.output, /\*\*Planned jobs: every job\*\*/);

  result = checkJobs(planFor({ changes: ['M\ttests/Wasla.UnitTests/Web/ReportsPageBrowserTests.cs'] }), { ...passing, [UT]: 'success', [PB]: 'skipped' });
  assert.equal(result.status, 0, result.output);
  assert.match(result.output, /\*\*Planned scope: full\*\*/);
  assert.match(result.output, /\*\*Planned jobs: focused\*\*/);

  result = checkJobs(docsOnly, { ...passing, [UT]: 'skipped', [PB]: 'skipped' });
  assert.match(result.output, /\*\*Planned scope: fast\*\* \(pull request into dev, and no changed file needs the full scope/);

  const pushPlan = planFor({ event: 'push', base: '', ref: 'dev', changes: ['M\tsrc/Wasla.PrintBridge.Service/Program.cs'] });
  result = checkJobs(pushPlan, { ...passing, [UT]: 'success', [PB]: 'success' });
  assert.equal(result.status, 0, result.output);
  assert.match(result.output, /\*\*Planned scope: full\*\* \(push; every test runs\)/);
  assert.match(result.output, /the rules would select every job and the fast scope\*\*/);
  assert.match(result.output, /The fast scope would have left out the headless-browser and WebView2 runtime tests; the test accounting reports a failure among them\./);
  assert.match(result.output, /The rules would have run every job for these changes; nothing to compare\./);
});

// ---- Guards: the repository still has the dependencies the focused rules rely on ---------------------------------
//
// Every dependency of a test project that can be found in the repository (a project it compiles, a file a project
// links from another folder, a file its tests read by a literal path) must be covered: the rule for that path must
// run the test project's job, or every job. These guards run with the Node tests on every change, so a new
// dependency fails CI until the rules cover it.

// The repository's tracked files (what CI checks out), relative and with forward slashes.
const tracked = (() => {
  const result = spawnSync('git', ['ls-files', '-z'], { cwd: repoRoot, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  assert.equal(result.status, 0, `git ls-files: ${result.stderr}`);
  return result.stdout.split('\0').filter(Boolean);
})();

// Tracked files under a folder (relative to the repository) that match a test.
function trackedUnder(folder, predicate = () => true) {
  return tracked.filter(file => file.startsWith(`${folder}/`) && predicate(file)).map(file => path.join(repoRoot, file));
}

const relative = full => path.relative(repoRoot, full).split(path.sep).join('/');

// The jobs each path needs, in one PowerShell process: the planner's own rules and matching.
function jobsForPaths(paths) {
  const unique = [...new Set(paths)];
  const result = runPowerShell(
    `foreach ($p in ($env:PROBES -split [char]10)) { & '${selectScript}' -EventName pull_request -BaseRef dev -ChangeList ("M" + [char]9 + $p) -PlanOnly }`,
    { PROBES: unique.join('\n') });
  assert.equal(result.status, 0, result.stderr + result.stdout);
  const plans = result.stdout.trim().split(/\r?\n/).map(line => JSON.parse(line));
  assert.equal(plans.length, unique.length);
  return new Map(unique.map((p, i) => [p, plans[i].selection]));
}

function assertCovered(dependencies) {
  const selections = jobsForPaths(dependencies.map(d => d.path));
  const uncovered = dependencies.filter(d => !selections.get(d.path).jobs[d.job].run);
  assert.deepEqual(uncovered.map(d => `${d.job} needs ${d.path} (${d.why}), but its rule does not run ${d.job}`), []);
}

const testProjects = {
  'Wasla.UnitTests': path.join(repoRoot, 'tests', 'Wasla.UnitTests'),
  'Wasla.PrintBridge.Tests': path.join(repoRoot, 'tests', 'Wasla.PrintBridge.Tests'),
};
const projectFiles = tracked.filter(file => /\.(cs|wix)proj$|\.props$|\.targets$/.test(file)).map(file => path.join(repoRoot, file));

function projectReferences(projectFile) {
  const text = fs.readFileSync(projectFile, 'utf8');
  return [...text.matchAll(/<ProjectReference\s+Include="([^"]+)"/g)].map(m => path.resolve(path.dirname(projectFile), m[1].replace(/\\/g, '/')));
}

function closure(projectFile, seen = new Set()) {
  for (const reference of projectReferences(projectFile)) {
    if (!seen.has(reference)) {
      seen.add(reference);
      closure(reference, seen);
    }
  }
  return seen;
}

test('guard: every project a test project compiles is in a rule that runs its job', () => {
  const dependencies = [];
  for (const [project, dir] of Object.entries(testProjects)) {
    for (const reference of closure(path.join(dir, `${project}.csproj`))) {
      dependencies.push({ job: projectJob[project], path: `${relative(path.dirname(reference))}/Probe.cs`, why: `${project} compiles ${path.basename(reference)}` });
    }
    dependencies.push({ job: projectJob[project], path: `${relative(dir)}/Probe.cs`, why: `${project} itself` });
  }
  assertCovered(dependencies);
});

test('guard: a file a project links from another folder is in a rule that runs every job', () => {
  const links = [];
  for (const projectFile of projectFiles) {
    const text = fs.readFileSync(projectFile, 'utf8');
    for (const match of text.matchAll(/<(\w+)\s+(?:Include|Update)="([^"]*\.\.[\\/][^"]*)"/g)) {
      if (match[1] === 'ProjectReference') continue;
      const target = relative(path.resolve(path.dirname(projectFile), match[2].replace(/\\/g, '/')));
      links.push({ project: relative(projectFile), target });
    }
  }
  assert.ok(links.length > 0, 'expected the known link of the Wasla logo into Wasla.PrintBridge');
  const selections = jobsForPaths(links.map(l => l.target));
  const narrowed = links.filter(l => selections.get(l.target).mode !== 'all');
  assert.deepEqual(narrowed.map(l => `${l.project} links ${l.target}, whose rule does not run every job`), []);
});

// Paths the tests of a project read, found from string literals: "top", "folder" pairs (Path.Combine arguments),
// "top/folder/..." and "top\folder\..." strings. A top-level folder named without a following literal folder is an
// unbounded read: every rule under it must run the job.
const topLevelFolders = ['src', 'tests', 'installer', 'deploy', 'scripts', '.github', '.claude'];

function literalReads(dir) {
  const reads = [];
  for (const file of trackedUnder(relative(dir), name => name.endsWith('.cs'))) {
    const text = fs.readFileSync(file, 'utf8');
    for (const top of topLevelFolders) {
      const quoted = top.replace('.', '\\.');
      for (const match of text.matchAll(new RegExp(`"${quoted}"(\\s*,\\s*"([^"]+)")?`, 'g'))) {
        reads.push({ file: relative(file), top, folder: match[2] ?? null });
      }
      for (const match of text.matchAll(new RegExp(`"${quoted}[\\\\/]+([^"\\\\/]+)`, 'g'))) {
        reads.push({ file: relative(file), top, folder: match[1] });
      }
    }
  }
  return reads;
}

// One probe path per folder (and file) directly under a top-level folder.
function foldersUnder(top) {
  const probes = new Set();
  for (const file of tracked.filter(f => f.startsWith(`${top}/`))) {
    const parts = file.split('/');
    probes.add(parts.length > 2 ? `${top}/${parts[1]}/Probe.cs` : file);
  }
  return [...probes];
}

test('guard: every source, test, installer and script folder the tests read is in a rule that runs their job', () => {
  const dependencies = [];
  for (const [project, dir] of Object.entries(testProjects)) {
    const job = projectJob[project];
    for (const read of literalReads(dir)) {
      if (read.folder) {
        dependencies.push({ job, path: `${read.top}/${read.folder}/Probe.cs`, why: `${read.file} reads ${read.top}/${read.folder}` });
      } else {
        for (const probe of foldersUnder(read.top)) dependencies.push({ job, path: probe, why: `${read.file} reads all of ${read.top}/` });
      }
    }
  }
  assert.ok(dependencies.some(d => d.job === PB && d.path.startsWith('src/Wasla.Web/')), 'expected the known reads of Wasla.Web by Wasla.PrintBridge.Tests');
  assertCovered(dependencies);
});

// Documents: every ".md" literal in the tests names a document a rule maps to their job. "docs" as a bare literal
// (Path.Combine(root, "docs", ...)) appears only in files whose reads the rules list; any other file must be added
// to the rules first.
const docsReaders = {
  'Wasla.UnitTests': {
    // "docs" is a host label in the routing tests, not a folder.
    docsLiteral: ['Configuration/WaslaConfigurationKeyTests.cs', 'Onboarding/WaslaHostClassifierTests.cs', 'Routing/WaslaHostRoutingTests.cs'],
    // Files the test writes into its own temporary repository.
    temporary: { 'Web/GitWorkingTreeTests.cs': ['notes.md', 'tracked.md', 'missing.md'] },
  },
  'Wasla.PrintBridge.Tests': {
    docsLiteral: ['Installer/PrintBridgeInstallerSourceTests.cs'],
    temporary: {},
  },
};

test('guard: every document the tests read is in a rule that runs their job', () => {
  const problems = [];
  const docsRules = rules.rules.filter(r => Array.isArray(r.jobs) && r.paths.every(p => p.startsWith('docs/') && !p.includes('*')));
  for (const [project, dir] of Object.entries(testProjects)) {
    const job = projectJob[project];
    const known = docsReaders[project];
    for (const file of trackedUnder(relative(dir), f => f.endsWith('.cs'))) {
      const name = path.relative(dir, file).split(path.sep).join('/');
      const text = fs.readFileSync(file, 'utf8');
      if (/"docs"/.test(text) && !known.docsLiteral.includes(name)) problems.push(`${project}/${name} names the docs folder; add what it reads to ci-job-rules.json and to this guard`);
      for (const match of text.matchAll(/"([^"\s]*\.md)"/g)) {
        const literal = match[1];
        if ((known.temporary[name] ?? []).includes(literal)) continue;
        const target = literal.startsWith('docs/') ? literal : `/${literal}`;
        const covered = docsRules.some(rule => rule.jobs.includes(job) && rule.paths.some(p => (target.startsWith('/') ? p.endsWith(target) : p === target)));
        if (!covered) problems.push(`${project}/${name} reads ${literal}, which no rule maps to ${job}`);
      }
    }
  }
  assert.deepEqual(problems, []);
});

test('guard: every document the rules list as read by the tests exists', () => {
  const listed = rules.rules.filter(r => r.id.startsWith('docs-read-by-')).flatMap(r => r.paths);
  assert.ok(listed.length > 0);
  assert.deepEqual(listed.filter(p => !fs.existsSync(path.join(repoRoot, p))), []);
});

// The fast scope leaves out tests by trait. A pull request that changes one of them, or the headless-browser driver,
// must run the full scope, or the changed tests would not run before the merge.
test('guard: every test the fast scope leaves out, and the browser driver, is in a rule that runs its job in the full scope', () => {
  const deferred = { 'Wasla.UnitTests': 'Browser', 'Wasla.PrintBridge.Tests': 'WebView2Runtime' };
  const dependencies = [];
  for (const [project, dir] of Object.entries(testProjects)) {
    const trait = new RegExp(`Trait\\(\\s*"Category"\\s*,\\s*"${deferred[project]}"\\s*\\)`);
    for (const file of trackedUnder(relative(dir), name => name.endsWith('.cs'))) {
      const text = fs.readFileSync(file, 'utf8');
      if (trait.test(text)) dependencies.push({ job: projectJob[project], path: relative(file), why: `has Category=${deferred[project]}` });
      if (/\bclass\s+HeadlessChromium\b/.test(text)) dependencies.push({ job: UT, path: relative(file), why: 'defines the headless-browser driver' });
    }
  }
  assert.ok(dependencies.filter(d => d.job === UT).length >= 30, 'expected the headless-browser test classes');
  assert.ok(dependencies.some(d => d.job === PB), 'expected the WebView2 runtime tests');
  const selections = jobsForPaths(dependencies.map(d => d.path));
  const uncovered = dependencies.filter(d => !selections.get(d.path).jobs[d.job].run || selections.get(d.path).scope !== 'full');
  assert.deepEqual(uncovered.map(d => `${d.path} ${d.why}, but its rule does not run ${d.job} in the full scope`), []);
});

// The headless-browser tests start Wasla.Web and the WebView2 runtime tests start the Wasla.PrintBridge tray app, each
// with the projects it builds on. A change to any of them can change what those tests see, so the full scope.
test('guard: every project the deferred tests start, or that it builds on, is in a rule with the full scope', () => {
  const started = { 'Wasla.Web': 'the headless-browser tests', 'Wasla.PrintBridge': 'the WebView2 runtime tests' };
  const dependencies = [];
  for (const [project, tests] of Object.entries(started)) {
    const projectFile = path.join(repoRoot, 'src', project, `${project}.csproj`);
    for (const reference of [projectFile, ...closure(projectFile)]) {
      dependencies.push({ path: `${relative(path.dirname(reference))}/Probe.cs`, why: `${tests} start ${project}${reference === projectFile ? '' : `, which builds on ${path.basename(reference, '.csproj')}`}` });
    }
  }
  assert.ok(dependencies.length >= 7, 'expected Web with Contracts, Application, Infrastructure and Domain, and Print Bridge with its engine');
  const selections = jobsForPaths(dependencies.map(d => d.path));
  assert.deepEqual(dependencies.filter(d => selections.get(d.path).scope !== 'full').map(d => `${d.path}: ${d.why}, but its rule keeps the fast scope`), []);
});

test('guard: every rule is well formed, only names jobs the workflow can skip and names its scope', () => {
  assert.deepEqual(rules.skippableJobs, [UT, PB]);
  const ids = new Set();
  for (const rule of rules.rules) {
    assert.ok(rule.id && !ids.has(rule.id), `rule id ${rule.id}`);
    ids.add(rule.id);
    assert.ok(rule.why && rule.paths.length > 0, rule.id);
    assert.ok(rule.jobs === 'all' || (Array.isArray(rule.jobs) && rule.jobs.every(job => rules.skippableJobs.includes(job))), rule.id);
    assert.ok(['fast', 'full'].includes(rule.scope), `rule ${rule.id} scope ${rule.scope}`);
  }
});

test('a rule without a valid scope gets the full scope', () => {
  const dir = tempDir('wasla-ci-rules-');
  try {
    const file = path.join(dir, 'rules.json');
    fs.writeFileSync(file, JSON.stringify({ skippableJobs: [UT, PB], rules: [{ id: 'docs', jobs: [], scope: 'Fast', why: 'x', paths: ['docs/**'] }, { id: 'tests', jobs: [UT], why: 'x', paths: ['tests/**'] }] }));
    for (const change of ['M\tdocs/README.md', 'M\ttests/A.cs']) {
      const result = runPowerShell(`& '${selectScript}' -EventName pull_request -BaseRef dev -RulesPath '${file}' -ChangeList $env:CHANGE -PlanOnly`, { CHANGE: change });
      assert.equal(result.status, 0, result.stderr + result.stdout);
      assert.equal(JSON.parse(result.stdout.trim()).scope, 'full', change);
    }
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
