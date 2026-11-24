// Tests for the CI job selection (select-test-scope.ps1 with ci-job-rules.json) and the final job's check of it
// (assert-ci-jobs.ps1), plus guards that fail when a dependency a focused rule relies on changes in the repository.
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

function assertSelection(testPlan, { mode, jobs, projects, rule, forced }) {
  const selection = testPlan.selection;
  assert.equal(selection.mode, mode, JSON.stringify(selection));
  assert.deepEqual(windowsJobs(selection), jobs, JSON.stringify(selection));
  assert.equal(selection.jobs['terminology-guard'].run, true);
  assert.equal(selection.jobs['javascript-tests'].run, true);
  assert.deepEqual(testPlan.projects.map(p => p.project), projects ?? jobs.map(job => Object.keys(projectJob).find(p => projectJob[p] === job)));
  if (rule) assert.deepEqual(selection.rules.map(r => r.id), rule);
  if (forced) assert.match(selection.forcedBy.join('\n'), forced);
}

// ---- Rules, one area or several ----------------------------------------------------------------------------------

const focused = [
  ['documentation only', ['M\tdocs/README.md', 'M\tdocs/product/terminology.md', 'A\tdocs/brand/new-sketch.svg'], [], ['documentation']],
  ['root and agent instructions', ['M\tREADME.md', 'M\tAGENTS.md', 'M\tCLAUDE.md', 'M\t.claude/rules/web-ui.md'], [], ['documentation']],
  ['the testing guide, which no test reads', ['M\tdocs/operations/testing.md'], [], ['documentation']],
  ['a document Wasla.UnitTests reads', ['M\tdocs/operations/migrations.md'], [UT], ['docs-read-by-unit-tests']],
  ['a document Wasla.PrintBridge.Tests reads', ['M\tdocs/integrations/print-bridge-service.md'], [PB], ['docs-read-by-print-bridge-tests']],
  ['Wasla.UnitTests sources and Node tests', ['M\ttests/Wasla.UnitTests/Auth/LoginTests.cs', 'A\ttests/Wasla.UnitTests/Signup/signup-steps.test.js'], [UT], ['unit-tests-project']],
  ['Wasla.PrintBridge.Tests sources', ['M\ttests/Wasla.PrintBridge.Tests/WebShell/ShellTests.cs'], [PB], ['print-bridge-tests-project']],
  ['the Print Bridge installer sources', ['M\tinstaller/print-bridge/Package/Package.wxs'], [PB], ['print-bridge-installer']],
  ['the server hosts', ['M\tsrc/Wasla.Api/Program.cs', 'M\tsrc/Wasla.Cli/Program.cs', 'M\tsrc/Wasla.Worker/Jobs/OrderSyncWorker.cs', 'M\tsrc/Wasla.Contracts/Orders/OrderDto.cs'], [UT], ['server-hosts']],
  ['documentation and Wasla.UnitTests: the union', ['M\tdocs/README.md', 'M\ttests/Wasla.UnitTests/Auth/LoginTests.cs'], [UT], ['documentation', 'unit-tests-project']],
  ['both test projects: the union', ['M\ttests/Wasla.UnitTests/A.cs', 'M\ttests/Wasla.PrintBridge.Tests/B.cs'], [UT, PB], ['unit-tests-project', 'print-bridge-tests-project']],
  ['a server host and the installer: the union', ['M\tsrc/Wasla.Worker/Program.cs', 'M\tinstaller/print-bridge/Bundle/Bundle.wxs', 'M\tdocs/README.md'], [UT, PB], ['server-hosts', 'print-bridge-installer', 'documentation']],
];

for (const [name, changes, jobs, rule] of focused) {
  test(`a pull request into dev that changes ${name} runs ${jobs.length ? jobs.join(' and ') : 'no .NET job'}`, () => {
    assertSelection(planFor({ changes }), { mode: 'focused', jobs, rule });
  });
}

const everything = [
  ['Wasla.Web Razor views (tenant UI)', ['M\tsrc/Wasla.Web/Areas/Tenant/Views/Orders/_OrdersTable.cshtml'], /rule web needs every job/],
  ['Wasla.Web JavaScript and static files', ['M\tsrc/Wasla.Web/wwwroot/js/live-screen.js', 'M\tsrc/Wasla.Web/wwwroot/css/site.css'], /rule web needs every job/],
  ['Wasla.Web authentication and authorization', ['M\tsrc/Wasla.Web/Program.cs'], /rule web needs every job/],
  ['Domain, Application and Infrastructure', ['M\tsrc/Wasla.Domain/Orders/Order.cs'], /rule shared-libraries needs every job/],
  ['an EF migration', ['A\tsrc/Wasla.Infrastructure/Persistence/Migrations/Tenant/20261124_Add.cs'], /rule shared-libraries needs every job/],
  ['tenant isolation in Infrastructure', ['M\tsrc/Wasla.Infrastructure/Tenancy/TenantDbContextFactory.cs'], /rule shared-libraries needs every job/],
  ['Print Bridge product code', ['M\tsrc/Wasla.PrintBridge.Core/Services/WaslaPrintBridgeClient.cs'], /rule print-bridge-product needs every job/],
  ['Print Bridge resources', ['M\tsrc/Wasla.PrintBridge/Resources/PrintBridgeResources.ru-RU.resx'], /rule print-bridge-product needs every job/],
  ['the Print Bridge version', ['M\tsrc/Wasla.PrintBridge.Version.props'], /rule ci-and-build needs every job/],
  ['a project file inside a focused folder', ['M\ttests/Wasla.UnitTests/Wasla.UnitTests.csproj'], /rule ci-and-build needs every job/],
  ['an MSBuild file inside the installer folder', ['M\tinstaller/print-bridge/Directory.Build.props'], /rule ci-and-build needs every job/],
  ['the solution', ['M\tWasla.sln'], /rule ci-and-build needs every job/],
  ['the CI workflow', ['M\t.github/workflows/ci.yml'], /rule ci-and-build needs every job/],
  ['the planner and its rules', ['M\tscripts/ci/select-test-scope.ps1', 'M\tscripts/ci/ci-job-rules.json'], /rule ci-and-build needs every job/],
  ['the release scripts', ['M\tscripts/release/package-print-bridge.ps1'], /rule ci-and-build needs every job/],
  ['Git attributes', ['M\t.gitattributes'], /rule ci-and-build needs every job/],
  ['the deployment scripts', ['M\tdeploy/linux/publish-release.ps1'], /rule deployment needs every job/],
  ['the report export smoke test', ['M\ttests/Wasla.ReportExport.LinuxSmoke/Program.cs'], /rule report-export-smoke needs every job/],
  ['a path no rule knows', ['A\ttools/new-tool.ps1'], /no rule matches tools\/new-tool\.ps1/],
  ['a new top-level file', ['A\tLICENSE'], /no rule matches LICENSE/],
  ['a path that differs only in case from a focused folder', ['M\tDocs/README.md'], /no rule matches Docs\/README\.md/],
  ['documentation together with Wasla.Web', ['M\tdocs/README.md', 'M\tsrc/Wasla.Web/wwwroot/js/a.js'], /rule web needs every job/],
  ['documentation together with a path no rule knows', ['M\tdocs/README.md', 'A\tnotes/todo.txt'], /no rule matches notes\/todo\.txt/],
  ['a deleted document', ['D\tdocs/README.md'], /other than an addition or modification: deleted docs\/README\.md/],
  ['a renamed document', ['R100\tdocs/a.md\tdocs/b.md'], /renamed docs\/a\.md to docs\/b\.md/],
  ['a copied test file', ['C090\ttests/Wasla.UnitTests/A.cs\ttests/Wasla.UnitTests/B.cs'], /copied tests\/Wasla\.UnitTests\/A\.cs to tests\/Wasla\.UnitTests\/B\.cs/],
  ['a file whose type changed', ['T\tdocs/README.md'], /changed the type of docs\/README\.md/],
  ['nothing (an empty diff)', [], /the diff is empty/],
];

for (const [name, changes, forced] of everything) {
  test(`a pull request into dev that changes ${name} runs every job`, () => {
    assertSelection(planFor({ changes }), { mode: 'all', jobs: [UT, PB], forced });
  });
}

test('every job that runs and every job that is skipped says why', () => {
  const selection = planFor({ changes: ['M\tdocs/README.md', 'M\ttests/Wasla.UnitTests/A.cs'] }).selection;
  assert.equal(selection.jobs[UT].why, 'needed by rule unit-tests-project');
  assert.equal(selection.jobs[PB].why, 'no changed file needs it (rules: documentation, unit-tests-project)');
  assert.deepEqual(selection.rules.map(r => [r.id, r.jobs, r.files]), [['documentation', [], 1], ['unit-tests-project', [UT], 1]]);
});

// ---- Events ------------------------------------------------------------------------------------------------------

test('a manual run runs every job and the full scope, whatever changed: it is the way to force everything', () => {
  const testPlan = planFor({ event: 'workflow_dispatch', base: '', changes: ['M\tdocs/README.md'] });
  assert.equal(testPlan.scope, 'full');
  assertSelection(testPlan, { mode: 'all', jobs: [UT, PB] });
  assert.match(testPlan.selection.basis, /every job runs for workflow_dispatch/);
  assert.equal(testPlan.observation, null);
});

for (const [event, base, ref] of [['pull_request', 'master', ''], ['push', '', 'master'], ['merge_group', '', ''], ['', '', '']]) {
  test(`${event || 'an unknown event'}${base ? ` into ${base}` : ''}${ref ? ` to ${ref}` : ''} runs every job, even for a documentation change`, () => {
    const testPlan = planFor({ event, base, ref, changes: ['M\tdocs/README.md'] });
    assert.equal(testPlan.scope, 'full');
    assertSelection(testPlan, { mode: 'all', jobs: [UT, PB] });
    assert.equal(testPlan.observation, null);
  });
}

test('a push to dev runs every job and records what the rules would have selected (observation phase)', () => {
  const testPlan = planFor({ event: 'push', base: '', ref: 'dev', changes: ['M\tdocs/README.md', 'M\ttests/Wasla.PrintBridge.Tests/A.cs'] });
  assert.equal(testPlan.scope, 'full');
  assertSelection(testPlan, { mode: 'all', jobs: [UT, PB] });
  assert.match(testPlan.selection.basis, /observation phase/);
  assert.equal(testPlan.observation.mode, 'focused');
  assert.deepEqual(windowsJobs(testPlan.observation), [PB]);
});

test('a pull request into dev without an event payload runs every job', () => {
  assertSelection(planFor({}), { mode: 'all', jobs: [UT, PB], forced: /the event payload is not available/ });
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
    assertSelection(testPlan, { mode: 'focused', jobs: [], rule: ['documentation'] });
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
  });
});

test('a pull request with renamed and deleted files runs every job', () => {
  withRepo((repo, root) => {
    const head = commit(repo, { 'docs/a.md': null, 'docs/b.md': 'a\n', 'tests/Wasla.UnitTests/A.cs': null }, 'rename and delete');
    const testPlan = planFor({ repo, payload: { pull_request: { base: { sha: root }, head: { sha: head } } } });
    assertSelection(testPlan, { mode: 'all', jobs: [UT, PB], forced: /renamed docs\/a\.md to docs\/b\.md/ });
    assert.match(testPlan.selection.forcedBy.join('\n'), /deleted tests\/Wasla\.UnitTests\/A\.cs/);
  });
});

test('a pull request whose head is already in its base has an empty diff and runs every job', () => {
  withRepo((repo, root) => {
    const tip = commit(repo, { 'docs/README.md': 'docs, edited\n' }, 'already merged');
    assertSelection(planFor({ repo, payload: { pull_request: { base: { sha: tip }, head: { sha: root } } } }), { mode: 'all', jobs: [UT, PB], forced: /the diff is empty/ });
  });
});

test('a commit missing from the fetched history (a shallow clone or a force push) runs every job', () => {
  withRepo((repo, root) => {
    const missing = 'f'.repeat(40);
    let testPlan = planFor({ repo, payload: { pull_request: { base: { sha: missing }, head: { sha: root } } } });
    assertSelection(testPlan, { mode: 'all', jobs: [UT, PB], forced: /the previous commit f{40} is not in the fetched history/ });

    const shallow = tempDir('wasla-ci-shallow-');
    try {
      const head = commit(repo, { 'docs/README.md': 'docs, edited\n' }, 'pull request');
      // A file:// URL: a plain local path ignores --depth.
      const url = `file://${repo.startsWith('/') ? '' : '/'}${repo.replace(/\\/g, '/')}`;
      const cloned = spawnSync('git', ['clone', '-q', '--depth', '1', url, shallow], { encoding: 'utf8' });
      assert.equal(cloned.status, 0, cloned.stderr);
      testPlan = planFor({ repo: shallow, payload: { pull_request: { base: { sha: root }, head: { sha: head } } } });
      assertSelection(testPlan, { mode: 'all', jobs: [UT, PB], forced: /not in the fetched history/ });
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
    assert.match(testPlan.observation.forcedBy.join('\n'), /the changes could not be computed: the previous commit is unknown/);
  });
});

test('a diff git cannot compute (not a repository) runs every job', () => {
  const notARepo = tempDir('wasla-ci-norepo-');
  try {
    const testPlan = planFor({ repo: notARepo, payload: { pull_request: { base: { sha: 'a'.repeat(40) }, head: { sha: 'b'.repeat(40) } } } });
    assertSelection(testPlan, { mode: 'all', jobs: [UT, PB], forced: /the changes could not be computed/ });
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

test('guard: every rule is well formed and only names jobs the workflow can skip', () => {
  assert.deepEqual(rules.skippableJobs, [UT, PB]);
  const ids = new Set();
  for (const rule of rules.rules) {
    assert.ok(rule.id && !ids.has(rule.id), `rule id ${rule.id}`);
    ids.add(rule.id);
    assert.ok(rule.why && rule.paths.length > 0, rule.id);
    assert.ok(rule.jobs === 'all' || (Array.isArray(rule.jobs) && rule.jobs.every(job => rules.skippableJobs.includes(job))), rule.id);
  }
});
