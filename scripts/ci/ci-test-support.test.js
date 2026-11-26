// Tests for the pwsh isolation of ci-test-support.js (WAS-257): node --test runs the test files in parallel, so the
// pwsh processes of different test files must never share a cache folder, where pwsh keeps its startup JIT profile.
const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { powershellCacheHome, runPowerShell } = require('./ci-test-support');

const supportModule = path.join(__dirname, 'ci-test-support.js');

// The pwsh cache folder of another test process: a fresh Node process that loads the helpers (and so starts pwsh once).
function cacheHomeOfAnotherTestProcess() {
  const result = spawnSync(process.execPath, ['-e', `process.stdout.write(require(${JSON.stringify(supportModule)}).powershellCacheHome)`], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr + result.stdout);
  return result.stdout;
}

function isInside(folder, parent) {
  const relative = path.relative(fs.realpathSync(parent), fs.realpathSync(folder));
  return relative !== '' && !relative.startsWith('..') && !path.isAbsolute(relative);
}

test('every test process gets a pwsh cache folder of its own, removed when the process ends', () => {
  assert.ok(fs.statSync(powershellCacheHome).isDirectory());
  assert.ok(isInside(powershellCacheHome, os.tmpdir()), `${powershellCacheHome} is not a temporary folder`);

  const first = cacheHomeOfAnotherTestProcess();
  const second = cacheHomeOfAnotherTestProcess();
  assert.notEqual(first, powershellCacheHome);
  assert.notEqual(second, powershellCacheHome);
  assert.notEqual(first, second);
  for (const folder of [first, second]) {
    assert.equal(path.dirname(folder), path.dirname(powershellCacheHome));
    assert.equal(fs.existsSync(folder), false, `${folder} outlived its test process`);
  }
});

test('pwsh started by the CI script tests uses that folder, even when the caller passes another one', (t) => {
  const result = runPowerShell('$env:XDG_CACHE_HOME', { XDG_CACHE_HOME: path.join(os.tmpdir(), 'shared-pwsh-cache') });
  assert.equal(result.status, 0, result.stderr + result.stdout);
  assert.equal(result.stdout.trim(), powershellCacheHome);

  // On Linux and macOS pwsh creates its cache folder, which holds the startup JIT profile, under XDG_CACHE_HOME at
  // startup. On Windows it ignores the variable (see ci-test-support.js).
  if (process.platform !== 'win32') {
    const cache = path.join(powershellCacheHome, 'powershell');
    assert.ok(fs.statSync(cache).isDirectory(), `pwsh did not create its cache folder in ${powershellCacheHome}`);
    t.diagnostic(`pwsh cache folder ${cache}: ${fs.readdirSync(cache).join(', ') || '(empty)'}`);
  }
});
