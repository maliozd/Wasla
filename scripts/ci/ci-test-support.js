// Shared helpers for the tests of the CI PowerShell scripts (test-shards.test.js, ci-job-plan.test.js).
// They run the scripts in PowerShell 7 (pwsh) or, on Windows without it, in Windows PowerShell 5.1.
const { spawnSync } = require('node:child_process');

function findPowerShell() {
  for (const candidate of process.platform === 'win32' ? ['pwsh', 'powershell'] : ['pwsh']) {
    const probe = spawnSync(candidate, ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', '$PSVersionTable.PSVersion.Major'], { encoding: 'utf8' });
    if (probe.status === 0) return candidate;
  }
  throw new Error('PowerShell (pwsh, or powershell on Windows) is required for the CI script tests.');
}

const powershell = findPowerShell();

// The tests run inside GitHub Actions too: the scripts must not write to this job's outputs or summary, nor read
// this job's event.
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

module.exports = { cleanEnv, runPowerShell };
