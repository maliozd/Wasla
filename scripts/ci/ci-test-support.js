// Shared helpers for the tests of the CI PowerShell scripts (test-shards.test.js, ci-job-plan.test.js).
// They run the scripts in PowerShell 7 (pwsh) or, on Windows without it, in Windows PowerShell 5.1.
const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

// pwsh keeps a .NET startup JIT profile (StartupProfileData-NonInteractive) in its cache folder and replays it at every
// start. On Linux and macOS that folder is $XDG_CACHE_HOME/powershell, or ~/.cache/powershell for every pwsh of the user
// when the variable is unset. node --test runs the test files in parallel, so pwsh processes of two test files can exit
// at the same moment. The .NET runtime then rewrites the shared profile from both at once, without a lock on Linux, and
// every later pwsh on the machine replays the mixed file and crashes at startup with "The given assembly name was
// invalid" (WAS-257). So every pwsh started here uses a cache folder that belongs to this test process alone; the pwsh
// runs of one test process never overlap (spawnSync). On Windows, pwsh ignores XDG_CACHE_HOME: its profile is under
// LOCALAPPDATA, and Windows file sharing lets only one process write it at a time.
const powershellCacheHome = fs.mkdtempSync(path.join(os.tmpdir(), 'wasla-ci-pwsh-cache-'));
process.on('exit', () => fs.rmSync(powershellCacheHome, { recursive: true, force: true }));

function findPowerShell() {
  for (const candidate of process.platform === 'win32' ? ['pwsh', 'powershell'] : ['pwsh']) {
    const probe = spawnSync(candidate, ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', '$PSVersionTable.PSVersion.Major'], { encoding: 'utf8', env: cleanEnv() });
    if (probe.status === 0) return candidate;
  }
  throw new Error('PowerShell (pwsh, or powershell on Windows) is required for the CI script tests.');
}

const powershell = findPowerShell();

// The tests run inside GitHub Actions too: the scripts must not write to this job's outputs or summary, nor read
// this job's event. The pwsh cache folder is always this test process's own, whatever the caller passes.
function cleanEnv(extra = {}) {
  const env = { ...process.env };
  for (const key of Object.keys(env)) {
    if (key.startsWith('GITHUB_')) delete env[key];
  }
  return { ...env, ...extra, XDG_CACHE_HOME: powershellCacheHome };
}

function runPowerShell(command, env) {
  return spawnSync(powershell, ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', command], {
    encoding: 'utf8',
    env: cleanEnv(env),
    maxBuffer: 16 * 1024 * 1024,
  });
}

module.exports = { cleanEnv, powershellCacheHome, runPowerShell };
