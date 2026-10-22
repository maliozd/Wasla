const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const script = path.join(__dirname, 'migrate-before-start.sh');

function runGate(failStep) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'wasla-migration-gate-'));
  try {
    const cli = path.join(dir, 'Wasla.Cli.dll');
    const log = path.join(dir, 'calls');
    fs.writeFileSync(cli, 'fake published binary');
    fs.writeFileSync(path.join(dir, 'dotnet'), '#!/bin/sh\nprintf "cli:%s:%s\\n" "$2" "$3" >> "$CALLS"\n[ "$2" != "$FAIL_STEP" ]\n', { mode: 0o755 });
    fs.writeFileSync(path.join(dir, 'start'), '#!/bin/sh\nprintf "start:%s\\n" "$1" >> "$CALLS"\n', { mode: 0o755 });

    const result = spawnSync(script, [cli, '--', path.join(dir, 'start'), 'new-version'], {
      encoding: 'utf8',
      env: { ...process.env, PATH: `${dir}${path.delimiter}${process.env.PATH}`, CALLS: log, FAIL_STEP: failStep ?? '' },
    });
    return { result, calls: fs.readFileSync(log, 'utf8').trim().split('\n') };
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

test('successful migrations and strict status check start the new version last', () => {
  const { result, calls } = runGate();
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(calls, ['cli:migrate-central:', 'cli:migrate-all-tenants:', 'cli:migration-status:--require-current', 'start:new-version']);
});

for (const step of ['migrate-central', 'migrate-all-tenants', 'migration-status']) {
  test(`${step} failure prevents the new version from starting`, () => {
    const { result, calls } = runGate(step);
    assert.equal(result.status, 1);
    assert.equal(calls.at(-1), `cli:${step}:${step === 'migration-status' ? '--require-current' : ''}`);
    assert.ok(!calls.some(call => call.startsWith('start:')));
  });
}

test('a missing CLI or start command fails closed', () => {
  const missingCli = spawnSync(script, ['/does/not/exist', '--', 'true'], { encoding: 'utf8' });
  assert.equal(missingCli.status, 2);
  const missingStart = spawnSync(script, ['/does/not/exist', '--'], { encoding: 'utf8' });
  assert.equal(missingStart.status, 2);
});
