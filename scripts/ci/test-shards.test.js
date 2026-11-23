// Tests for the CI test plan (select-test-scope.ps1) and its accounting (assert-test-shards.ps1).
// They run the scripts in PowerShell 7 (pwsh) or, on Windows without it, in Windows PowerShell 5.1.
const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const selectScript = path.join(__dirname, 'select-test-scope.ps1');
const accountingScript = path.join(__dirname, 'assert-test-shards.ps1');
const commit = 'a'.repeat(40);

function findPowerShell() {
  for (const candidate of process.platform === 'win32' ? ['pwsh', 'powershell'] : ['pwsh']) {
    const probe = spawnSync(candidate, ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', '$PSVersionTable.PSVersion.Major'], { encoding: 'utf8' });
    if (probe.status === 0) return candidate;
  }
  throw new Error('PowerShell (pwsh, or powershell on Windows) is required for the CI script tests.');
}

const powershell = findPowerShell();

// The tests run inside GitHub Actions too: the scripts must not write to this job's outputs or summary.
function cleanEnv(extra = {}) {
  const env = { ...process.env };
  for (const key of Object.keys(env)) {
    if (key.startsWith('GITHUB_')) delete env[key];
  }
  return { ...env, ...extra };
}

function runPowerShell(command, env) {
  return spawnSync(powershell, ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', command], {
    encoding: 'utf8',
    env: cleanEnv(env),
    maxBuffer: 16 * 1024 * 1024,
  });
}

function plan(eventName, baseRef = '') {
  const result = runPowerShell(`& '${selectScript}' -EventName '${eventName}' -BaseRef '${baseRef}' -PlanOnly`);
  assert.equal(result.status, 0, result.stderr + result.stdout);
  return JSON.parse(result.stdout.trim());
}

const browser = { Category: ['Browser'] };
const webView2 = { Category: ['WebView2Runtime'] };

// A small test assembly per project: every shard rule has tests, and a look-alike namespace
// (Wasla.UnitTests.Authorization) must not count as Wasla.UnitTests.Auth.
const discovered = {
  'Wasla.UnitTests': [
    ['Wasla.UnitTests.Auth.LoginTests', 'SignsIn'],
    ['Wasla.UnitTests.Auth.Sessions.SessionTests', 'Revokes'],
    ['Wasla.UnitTests.Routing.HostTests', 'Classifies'],
    ['Wasla.UnitTests.Printing.ReceiptTests', 'Queues'],
    ['Wasla.UnitTests.Contact.PilotTests', 'Stores'],
    ['Wasla.UnitTests.Orders.OrderTests', 'Approves'],
    ['Wasla.UnitTests.Authorization.PolicyTests', 'Denies'],
    ['Wasla.UnitTests.Web.LayoutTests', 'Renders'],
    ['Wasla.UnitTests.Web.LiveScreenBrowserTests', 'Loads', browser],
    ['Wasla.UnitTests.Web.LiveScreenBrowserTests', 'Refreshes', browser],
    ['Wasla.UnitTests.Signup.SignupFlowBrowserTests', 'Submits', browser],
  ],
  'Wasla.PrintBridge.Tests': [
    ['Wasla.PrintBridge.Tests.EngineTests', 'Polls'],
    ['Wasla.PrintBridge.Tests.WebShell.ShellTests', 'Routes'],
    ['Wasla.PrintBridge.Tests.WebShell.ShellWebViewRuntimeTests', 'Renders', webView2],
  ],
};

const expectedShards = {
  fast: {
    'nonbrowser-1': ['Wasla.UnitTests.Auth.LoginTests.SignsIn', 'Wasla.UnitTests.Auth.Sessions.SessionTests.Revokes', 'Wasla.UnitTests.Routing.HostTests.Classifies', 'Wasla.UnitTests.Printing.ReceiptTests.Queues', 'Wasla.UnitTests.Contact.PilotTests.Stores'],
    'nonbrowser-2': ['Wasla.UnitTests.Orders.OrderTests.Approves', 'Wasla.UnitTests.Authorization.PolicyTests.Denies', 'Wasla.UnitTests.Web.LayoutTests.Renders'],
    'print-bridge': ['Wasla.PrintBridge.Tests.EngineTests.Polls', 'Wasla.PrintBridge.Tests.WebShell.ShellTests.Routes'],
  },
};
expectedShards.full = {
  ...expectedShards.fast,
  'browser-1': ['Wasla.UnitTests.Web.LiveScreenBrowserTests.Loads', 'Wasla.UnitTests.Web.LiveScreenBrowserTests.Refreshes'],
  'browser-2': ['Wasla.UnitTests.Signup.SignupFlowBrowserTests.Submits'],
  'print-bridge': [...expectedShards.fast['print-bridge'], 'Wasla.PrintBridge.Tests.WebShell.ShellWebViewRuntimeTests.Renders'],
};

function discoveryJson(project, tests = discovered[project]) {
  return JSON.stringify(tests.map(([cls, method, traits]) => ({
    Assembly: `${project}.dll`,
    DisplayName: `${cls}.${method}`,
    ID: `${cls}.${method}`,
    Class: cls,
    Method: method,
    ...(traits ? { Traits: traits } : {}),
  })));
}

function escapeXml(text) {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

// One result per method, or two for a "theory"; outcome per method can be overridden.
function trx(methods, outcomes = {}) {
  const definitions = [];
  const results = [];
  methods.forEach((name, index) => {
    const dot = name.lastIndexOf('.');
    const id = `00000000-0000-0000-0000-${String(index).padStart(12, '0')}`;
    definitions.push(`<UnitTest id="${id}" name="${escapeXml(name)}"><TestMethod className="${escapeXml(name.slice(0, dot))}" name="${escapeXml(name.slice(dot + 1))}" /></UnitTest>`);
    const rows = index % 2 === 0 ? 2 : 1;
    for (let row = 0; row < rows; row += 1) {
      results.push(`<UnitTestResult testId="${id}" testName="${escapeXml(name)}(row: ${row})" outcome="${outcomes[name] ?? 'Passed'}" />`);
    }
  });
  return `<?xml version="1.0" encoding="utf-8"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>${results.join('')}</Results><TestDefinitions>${definitions.join('')}</TestDefinitions></TestRun>`;
}

function projectOf(shardId) {
  return shardId === 'print-bridge' ? 'Wasla.PrintBridge.Tests' : 'Wasla.UnitTests';
}

// Writes what each shard job uploads: test-shard.json, the discovery listing and the TRX file.
function writeResults(root, testPlan, change = {}) {
  for (const project of testPlan.projects) {
    for (const shard of project.shards) {
      const folder = path.join(root, `dotnet-test-results-${shard.id}`);
      fs.mkdirSync(folder, { recursive: true });
      const record = {
        project: project.project,
        shard: shard.id,
        scope: testPlan.scope,
        filter: shard.filter,
        commit,
        results: `${project.project}.${shard.id}.trx`,
        discovery: `${project.project}.discovery.json`,
        ...(change.record?.[shard.id] ?? {}),
      };
      fs.writeFileSync(path.join(folder, 'test-shard.json'), JSON.stringify(record));
      const tests = change.discovery?.[shard.id] ?? discovered[project.project];
      fs.writeFileSync(path.join(folder, record.discovery), discoveryJson(project.project, tests));
      // null: the job wrote no TRX file.
      const ran = change.ran && shard.id in change.ran ? change.ran[shard.id] : expectedShards[testPlan.scope][shard.id];
      if (ran !== null) fs.writeFileSync(path.join(folder, record.results), trx(ran, change.outcomes ?? {}));
    }
  }
}

function account(testPlan, change = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'wasla-test-shards-'));
  try {
    writeResults(root, testPlan, change);
    change.after?.(root);
    const planJson = change.planJson ?? JSON.stringify(testPlan);
    const result = runPowerShell(`& '${accountingScript}' -Plan $env:TEST_PLAN -ResultsRoot '${root}' -Commit '${commit}'; exit $LASTEXITCODE`, { TEST_PLAN: planJson });
    return { status: result.status, output: result.stdout + result.stderr };
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

const fastPlan = plan('pull_request', 'dev');
const fullPlan = plan('push');

test('a pull request into dev plans the fast scope: two non-browser unit shards and Print Bridge without the WebView2 runtime tests', () => {
  assert.equal(fastPlan.scope, 'fast');
  const [unit, printBridge] = fastPlan.projects;
  assert.equal(unit.project, 'Wasla.UnitTests');
  assert.deepEqual(unit.scopeExcludesTraits, ['Category=Browser']);
  assert.deepEqual(unit.shards.map(s => [s.id, s.filter]), [
    ['nonbrowser-1', 'Category!=Browser&(FullyQualifiedName~Wasla.UnitTests.Auth.|FullyQualifiedName~Wasla.UnitTests.Routing.|FullyQualifiedName~Wasla.UnitTests.Printing.|FullyQualifiedName~Wasla.UnitTests.Contact.)'],
    ['nonbrowser-2', 'Category!=Browser&FullyQualifiedName!~Wasla.UnitTests.Auth.&FullyQualifiedName!~Wasla.UnitTests.Routing.&FullyQualifiedName!~Wasla.UnitTests.Printing.&FullyQualifiedName!~Wasla.UnitTests.Contact.'],
  ]);
  assert.equal(printBridge.project, 'Wasla.PrintBridge.Tests');
  assert.deepEqual(printBridge.scopeExcludesTraits, ['Category=WebView2Runtime']);
  assert.deepEqual(printBridge.shards.map(s => [s.id, s.filter]), [['print-bridge', 'Category!=WebView2Runtime']]);
});

for (const [eventName, baseRef] of [['push', ''], ['workflow_dispatch', ''], ['pull_request', 'master'], ['merge_group', ''], ['', '']]) {
  test(`${eventName || 'an unknown event'}${baseRef ? ` into ${baseRef}` : ''} plans the full scope with every test`, () => {
    const testPlan = plan(eventName, baseRef);
    assert.equal(testPlan.scope, 'full');
    const [unit, printBridge] = testPlan.projects;
    assert.deepEqual(unit.scopeExcludesTraits, []);
    assert.deepEqual(unit.shards.map(s => [s.id, s.filter]), [
      ['nonbrowser-1', fastPlan.projects[0].shards[0].filter],
      ['nonbrowser-2', fastPlan.projects[0].shards[1].filter],
      ['browser-1', 'Category=Browser&(FullyQualifiedName~Wasla.UnitTests.Web.)'],
      ['browser-2', 'Category=Browser&FullyQualifiedName!~Wasla.UnitTests.Web.'],
    ]);
    assert.deepEqual(printBridge.scopeExcludesTraits, []);
    assert.deepEqual(printBridge.shards.map(s => [s.id, s.filter]), [['print-bridge', '']]);
  });
}

test('on GitHub Actions the plan, the unit-test matrix and the Print Bridge filter are written as step outputs', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'wasla-test-scope-'));
  try {
    const output = path.join(dir, 'output');
    const summary = path.join(dir, 'summary');
    const result = runPowerShell(`& '${selectScript}' -EventName pull_request -BaseRef dev`, { GITHUB_OUTPUT: output, GITHUB_STEP_SUMMARY: summary });
    assert.equal(result.status, 0, result.stderr);
    const bytes = fs.readFileSync(output);
    assert.notDeepEqual([...bytes.subarray(0, 3)], [0xef, 0xbb, 0xbf], 'no byte-order mark before the first output name');
    const outputs = Object.fromEntries(bytes.toString('utf8').trim().split('\n').map(line => {
      const at = line.indexOf('=');
      return [line.slice(0, at), line.slice(at + 1)];
    }));
    assert.deepEqual(Object.keys(outputs), ['scope', 'plan', 'unit-shards', 'print-bridge-filter']);
    assert.equal(outputs.scope, 'fast');
    assert.deepEqual(JSON.parse(outputs.plan), fastPlan);
    assert.deepEqual(JSON.parse(outputs['unit-shards']), fastPlan.projects[0].shards.map(({ id, title, filter }) => ({ id, title, filter })));
    assert.equal(outputs['print-bridge-filter'], 'Category!=WebView2Runtime');
    assert.match(fs.readFileSync(summary, 'utf8'), /\*\*Test scope: fast\*\*/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

for (const scope of ['fast', 'full']) {
  test(`${scope} scope: shards that ran exactly their planned tests pass the accounting`, () => {
    const result = account(scope === 'fast' ? fastPlan : fullPlan);
    assert.equal(result.status, 0, result.output);
    assert.match(result.output, /Test accounting is clean/);
  });
}

test('a planned test that did not run fails the accounting', () => {
  const ran = expectedShards.full['nonbrowser-1'].filter(name => !name.endsWith('Revokes'));
  const result = account(fullPlan, { ran: { 'nonbrowser-1': ran } });
  assert.equal(result.status, 1);
  assert.match(result.output, /nonbrowser-1: planned methods that did not run: 1\s+Wasla\.UnitTests\.Auth\.Sessions\.SessionTests\.Revokes/);
});

test('a test run by two shards fails the accounting', () => {
  const ran = [...expectedShards.full['nonbrowser-2'], 'Wasla.UnitTests.Auth.LoginTests.SignsIn'];
  const result = account(fullPlan, { ran: { 'nonbrowser-2': ran } });
  assert.equal(result.status, 1);
  assert.match(result.output, /nonbrowser-2: methods that ran but were not planned for this shard: 1/);
  assert.match(result.output, /methods that ran in more than one shard: 1\s+Wasla\.UnitTests\.Auth\.LoginTests\.SignsIn \(nonbrowser-1, nonbrowser-2\)/);
});

test('a deferred browser test that runs in the fast scope fails the accounting', () => {
  const ran = [...expectedShards.fast['nonbrowser-2'], 'Wasla.UnitTests.Web.LiveScreenBrowserTests.Loads'];
  const result = account(fastPlan, { ran: { 'nonbrowser-2': ran } });
  assert.equal(result.status, 1);
  assert.match(result.output, /nonbrowser-2: methods that ran but were not planned for this shard: 1\s+Wasla\.UnitTests\.Web\.LiveScreenBrowserTests\.Loads/);
});

test('a shard without results fails the accounting', () => {
  const result = account(fullPlan, { after: root => fs.rmSync(path.join(root, 'dotnet-test-results-browser-2'), { recursive: true }) });
  assert.equal(result.status, 1);
  assert.match(result.output, /Wasla\.UnitTests shard browser-2: no result was found/);
});

test('a shard whose result file is missing fails the accounting', () => {
  const result = account(fullPlan, { ran: { 'print-bridge': null } });
  assert.equal(result.status, 1);
  assert.match(result.output, /Wasla\.PrintBridge\.Tests shard print-bridge: the result file Wasla\.PrintBridge\.Tests\.print-bridge\.trx is missing/);
});

test('a result file with no test results fails the accounting', () => {
  const result = account(fullPlan, { ran: { 'browser-1': [] } });
  assert.equal(result.status, 1);
  assert.match(result.output, /browser-1: Wasla\.UnitTests\.browser-1\.trx contains no test results/);
  assert.match(result.output, /browser-1: planned methods that did not run: 2/);
});

test('a shard that tested another commit fails the accounting', () => {
  const result = account(fastPlan, { record: { 'nonbrowser-2': { commit: 'b'.repeat(40) } } });
  assert.equal(result.status, 1);
  assert.match(result.output, /nonbrowser-2: tested commit b{40}, not a{40}/);
});

test('a shard that ran another filter or scope fails the accounting', () => {
  const result = account(fastPlan, { record: { 'print-bridge': { filter: '', scope: 'full' } } });
  assert.equal(result.status, 1);
  assert.match(result.output, /print-bridge: ran the full scope, not fast/);
  assert.match(result.output, /print-bridge: ran filter '', not the planned 'Category!=WebView2Runtime'/);
});

test('a result for a shard the plan does not have fails the accounting', () => {
  const result = account(fastPlan, {
    after: root => {
      const folder = path.join(root, 'dotnet-test-results-browser-1');
      fs.mkdirSync(folder);
      fs.writeFileSync(path.join(folder, 'test-shard.json'), JSON.stringify({ project: 'Wasla.UnitTests', shard: 'browser-1', scope: 'fast', filter: '', commit }));
    },
  });
  assert.equal(result.status, 1);
  assert.match(result.output, /belongs to Wasla\.UnitTests\/browser-1, which this run's plan does not have/);
});

test('shards that listed different tests fail the accounting', () => {
  const fewer = discovered['Wasla.UnitTests'].slice(1);
  const result = account(fullPlan, { discovery: { 'browser-2': fewer } });
  assert.equal(result.status, 1);
  assert.match(result.output, /Wasla\.UnitTests shards browser-1 and browser-2 listed different tests \(1 only in browser-1, 0 only in browser-2\)/);
});

test('a shard the plan leaves empty fails the accounting', () => {
  const noOtherBrowserTests = discovered['Wasla.UnitTests'].filter(([cls]) => !cls.includes('Signup'));
  const discovery = Object.fromEntries(fullPlan.projects[0].shards.map(s => [s.id, noOtherBrowserTests]));
  const result = account(fullPlan, { discovery, ran: { 'browser-2': [] } });
  assert.equal(result.status, 1);
  assert.match(result.output, /browser-2: the plan selects no test of the scope; an empty shard proves nothing/);
});

test('shard rules that overlap or leave a test of the scope out fail the accounting', () => {
  const overlapping = structuredClone(fullPlan);
  overlapping.projects[0].shards[1].exceptNamespaces = [];
  let result = account(fullPlan, { planJson: JSON.stringify(overlapping) });
  assert.equal(result.status, 1);
  assert.match(result.output, /Wasla\.UnitTests methods that more than one shard selects: 5/);

  const gap = structuredClone(fullPlan);
  gap.projects[0].shards[3].exceptNamespaces = ['Wasla.UnitTests.Web', 'Wasla.UnitTests.Signup'];
  result = account(fullPlan, { planJson: JSON.stringify(gap) });
  assert.equal(result.status, 1);
  assert.match(result.output, /Wasla\.UnitTests methods of the scope that no shard selects: 1\s+Wasla\.UnitTests\.Signup\.SignupFlowBrowserTests\.Submits/);
});

test('a shard rule that selects a test outside the scope fails the accounting', () => {
  const leaky = structuredClone(fastPlan);
  leaky.projects[1].shards[0].excludeTraits = [];
  const result = account(fastPlan, { planJson: JSON.stringify(leaky) });
  assert.equal(result.status, 1);
  assert.match(result.output, /Wasla\.PrintBridge\.Tests methods outside the scope that a shard selects: 1\s+Wasla\.PrintBridge\.Tests\.WebShell\.ShellWebViewRuntimeTests\.Renders \(print-bridge\)/);
});

test('failed and skipped results are counted but left to assert-test-results.ps1', () => {
  const result = account(fastPlan, { outcomes: { 'Wasla.UnitTests.Orders.OrderTests.Approves': 'Failed', 'Wasla.PrintBridge.Tests.EngineTests.Polls': 'NotExecuted' } });
  assert.equal(result.status, 0, result.output);
  assert.match(result.output, /\| Wasla\.UnitTests \| nonbrowser-2 \| 3 \| 3 \| 5 \| 3 \| 2 \| 0 \|/);
  assert.match(result.output, /\| Wasla\.PrintBridge\.Tests \| print-bridge \| 2 \| 2 \| 3 \| 1 \| 0 \| 2 \|/);
});
