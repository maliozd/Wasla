const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const script = path.join(__dirname, 'wasla-preflight');
const sha = '0123456789abcdef0123456789abcdef01234567';
const fakeSecret = 'FAKE-TEST-SECRET-never-print-me-0123456789';
const fakeKey = Buffer.alloc(32, 7).toString('base64');

function withRelease(run) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'wasla-preflight-'));
  try {
    const release = path.join(root, 'releases', sha);
    for (const p of ['web', 'api', 'worker']) {
      fs.mkdirSync(path.join(release, p), { recursive: true });
      fs.writeFileSync(path.join(release, p, 'appsettings.Production.json'), '{"Platform":{"TrendyolGo":{"BaseUrl":"https://api.tgoapis.com"}}}');
    }
    fs.mkdirSync(path.join(release, 'web', 'wwwroot'));
    fs.writeFileSync(path.join(release, 'RELEASE'), `commit=${sha}\n`);
    fs.symlinkSync(path.join('releases', sha), path.join(root, 'current'));
    const keys = path.join(root, 'keys');
    fs.mkdirSync(keys, { mode: 0o700 });
    fs.chmodSync(keys, 0o700);
    return run(root, keys);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

function productionEnv(keys) {
  return {
    PATH: process.env.PATH,
    ASPNETCORE_ENVIRONMENT: 'Production',
    DOTNET_ENVIRONMENT: 'Production',
    Platforms__ProviderMode: 'Real',
    Platform__TrendyolGo__BaseUrl: 'https://api.tgoapis.com',
    Wasla__TenantOnboarding__PublicSignupEnabled: 'false',
    Wasla__TenantOnboarding__MarketingBaseDomain: 'wasla.tr',
    Wasla__TenantOnboarding__ApplicationBaseDomain: 'app.wasla.tr',
    Wasla__ApiBaseUrl: '',
    ENCRYPTION_MASTER_KEY: fakeKey,
    ConnectionStrings__CentralDb: `Server=127.0.0.1,1433;Database=WaslaProd_Central;User Id=waslaprod_app;Password=${fakeSecret};Encrypt=True;TrustServerCertificate=True`,
    Realtime__Internal__Port: '5290',
    Realtime__Internal__Secret: fakeSecret,
    TrustedProxies__KnownProxies__0: '127.0.0.1',
    ASPNETCORE_URLS: 'http://127.0.0.1:5200',
    DataProtection__KeyPath: keys,
  };
}

function preflight(processName, change) {
  return withRelease((root, keys) => {
    const env = { ...productionEnv(keys), WASLA_ROOT: root };
    change?.(env, root);
    for (const [k, v] of Object.entries(env)) if (v === undefined) delete env[k];
    return spawnSync('bash', [script, processName], { encoding: 'utf8', env });
  });
}

function assertRefused(result, fragment) {
  assert.equal(result.status, 1, result.stdout + result.stderr);
  assert.match(result.stderr, fragment);
  assert.ok(!(result.stdout + result.stderr).includes(fakeSecret), 'a secret value was printed');
  assert.ok(!(result.stdout + result.stderr).includes(fakeKey), 'the master key was printed');
}

test('a complete production environment passes for every process', () => {
  for (const name of ['web', 'api', 'worker']) {
    const result = preflight(name);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, new RegExp(`release ${sha}`));
    assert.ok(!(result.stdout + result.stderr).includes(fakeSecret));
  }
});

test('a missing CentralDb is refused instead of falling back to the committed LocalDB', () => {
  assertRefused(preflight('worker', env => { delete env.ConnectionStrings__CentralDb; }), /ConnectionStrings__CentralDb is not set/);
  assertRefused(preflight('web', env => { env.ConnectionStrings__CentralDb = 'Server=(localdb)\\MSSQLLocalDB;Database=Wasla_Central;Trusted_Connection=True'; }), /names LocalDB/);
});

test('Mock mode, a stage endpoint and open signup are refused', () => {
  assertRefused(preflight('worker', env => { env.Platforms__ProviderMode = 'Mock'; }), /Platforms__ProviderMode must be Real/);
  assertRefused(preflight('worker', env => { env.Platform__TrendyolGo__BaseUrl = 'https://stageapi.tgoapis.com'; }), /Platform__TrendyolGo__BaseUrl must be/);
  assertRefused(preflight('web', env => { env.Wasla__TenantOnboarding__PublicSignupEnabled = 'true'; }), /PublicSignupEnabled must be false/);
});

test('Development hosts, localhost URLs and PFX certificates are refused', () => {
  assertRefused(preflight('web', env => { env.Wasla__TenantWebBaseUrl = 'https://sushim.app.wasla.local'; }), /Wasla__TenantWebBaseUrl names a wasla.local host/);
  assertRefused(preflight('api', env => { env.Wasla__ApiBaseUrl = 'http://localhost:59451'; }), /Wasla__ApiBaseUrl names localhost/);
  assertRefused(preflight('web', env => { env.Kestrel__Certificates__Default__Path = 'dev.pfx'; }), /configures a Kestrel certificate/);
  assertRefused(preflight('web', env => { env.ASPNETCORE_URLS = 'http://0.0.0.0:5200'; }), /ASPNETCORE_URLS must be one http:\/\/127\.0\.0\.1/);
});

test('Web and Api need a persistent private Data Protection key folder; Worker does not', () => {
  assertRefused(preflight('web', env => { delete env.DataProtection__KeyPath; }), /DataProtection__KeyPath is not set/);
  assertRefused(preflight('api', env => { env.DataProtection__KeyPath = '/does/not/exist'; }), /not an existing directory/);
  const worker = preflight('worker', env => { delete env.DataProtection__KeyPath; delete env.ASPNETCORE_URLS; delete env.TrustedProxies__KnownProxies__0; });
  assert.equal(worker.status, 0, worker.stderr);
});

test('missing secrets and a missing release are refused without printing values', () => {
  assertRefused(preflight('worker', env => { env.ENCRYPTION_MASTER_KEY = 'short'; }), /ENCRYPTION_MASTER_KEY is not Base64 of 32 bytes/);
  assertRefused(preflight('worker', env => { env.Realtime__Internal__Secret = 'FAKE-short'; }), /Realtime__Internal__Secret/);
  assertRefused(preflight('web', (env, root) => { fs.rmSync(path.join(root, 'current')); }), /does not point at an installed release/);
});

test('an unknown process name is a usage error', () => {
  const result = spawnSync('bash', [script, 'cli'], { encoding: 'utf8' });
  assert.equal(result.status, 2);
});
