'use strict';

// Tests for scripts/ci/terminology-guard.js. They run the guard against throwaway Git repositories with made-up terms:
// "Quokkahub" stands for a retired product name and "PatronId" for a retired tenant identifier, while "PatronName"
// plays a genuine order field. No retired name of this repository appears here (docs/product/terminology.md).

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { test } = require('node:test');

const { HISTORICAL_MIGRATIONS, VARIABLE, isExactDesignerPath, run } = require('./terminology-guard.js');

const RULES = [
  { id: 'former-product-name', pattern: 'quokkahub', flags: 'i' },
  { id: 'tenant-id', pattern: 'patron[-_]?id\\b', flags: 'i' },
];
const MIGRATIONS = 'src/App/Persistence/Migrations';
const HISTORICAL = `${MIGRATIONS}/20200101000000_Initial.Designer.cs`;

const BASE_FILES = {
  'src/App/Order.cs': 'public sealed class Order\n{\n    public string PatronName { get; set; } = "";\n}\n',
  'docs/orders.md': 'The order shows the PatronName and the patron-name column.\n',
  [HISTORICAL]: 'modelBuilder.Entity("Quokkahub.Entities.Order", b => { });\n',
  [`${MIGRATIONS}/20200101000000_Initial.cs`]: 'public partial class Initial : Migration { }\n',
};

function config({ rules = RULES, approved = [HISTORICAL] } = {}) {
  return JSON.stringify({ version: 1, rules, approvedHistoricalMigrations: approved });
}

function repository(t, files = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'terminology-guard-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const git = (...args) => execFileSync('git', ['-c', 'core.autocrlf=false', '-c', 'core.safecrlf=false', ...args], { cwd: root, stdio: 'pipe' });
  git('init', '-q');
  for (const [file, content] of Object.entries({ ...BASE_FILES, ...files })) {
    if (content === null) continue;
    const target = path.join(root, ...file.split('/'));
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, content);
  }
  git('add', '-A');
  return root;
}

// options.value is the variable's value; passing undefined leaves the variable unset.
function guard(root, options = {}) {
  const { historical = [HISTORICAL], actions = false } = options;
  const value = Object.hasOwn(options, 'value') ? options.value : config();
  const out = [];
  const err = [];
  const env = {};
  if (value !== undefined) env[VARIABLE] = value;
  if (actions) env.GITHUB_ACTIONS = 'true';
  const code = run({ cwd: root, env, historical, out: (line) => out.push(line), err: (line) => err.push(line) });
  return { code, out: out.join('\n'), err: err.join('\n'), lines: err };
}

test('a clean repository passes: genuine order fields and the exempt historical file are not findings', (t) => {
  const result = guard(repository(t));

  assert.equal(result.code, 0, result.err);
  assert.match(result.out, /Scanned 3 of 4 tracked files with 2 retired-name rules; 1 historical migration file\(s\) are exempt/);
  assert.match(result.out, /No retired names found\./);
  assert.equal(result.err, '');
});

test('a retired name in source, in a test and in a new migration each fails with its file, line and column', (t) => {
  const root = repository(t, {
    'src/App/Service.cs': 'public sealed class Service\n{\n    public int PatronId => 1;\n}\n',
    'tests/App.Tests/ClientTests.cs': '[Fact]\npublic void Name() => Assert.Equal("quokkahub", Client.Name);\n',
    [`${MIGRATIONS}/20990101000000_AddThing.Designer.cs`]: 'modelBuilder.Entity("Quokkahub.Entities.Thing", b => { });\n',
    [`${MIGRATIONS}/20990101000000_AddThing.cs`]: 'migrationBuilder.AddColumn<int>(name: "patron_id", table: "Things");\n',
  });

  const result = guard(root);

  assert.equal(result.code, 1);
  assert.deepEqual(result.lines.slice(0, 4), [
    `${MIGRATIONS}/20990101000000_AddThing.cs:1:40: [tenant-id] "patron_id"`,
    `${MIGRATIONS}/20990101000000_AddThing.Designer.cs:1:22: [former-product-name] "Quokkahub"`,
    'src/App/Service.cs:3:16: [tenant-id] "PatronId"',
    'tests/App.Tests/ClientTests.cs:2:37: [former-product-name] "quokkahub"',
  ]);
  assert.match(result.lines[4], /^4 finding\(s\)\. Use the canonical names in docs\/product\/terminology\.md/);
  assert.equal(result.lines.length, 5);
});

test('every match is classified by its own text: a genuine name on the line or in the file name hides nothing', (t) => {
  const root = repository(t, {
    'tests/App.Tests/OrderPatronNameTests.cs':
      'Assert.Equal(order.PatronName, auth.PatronId); // PatronName PatronName\nAssert.Equal("PatronName", view.PatronName);\n',
  });

  const result = guard(root);

  assert.equal(result.code, 1);
  assert.deepEqual(result.lines.slice(0, -1), ['tests/App.Tests/OrderPatronNameTests.cs:1:37: [tenant-id] "PatronId"']);
});

test('a retired name in a file path is its own finding, and the file content is still scanned', (t) => {
  const root = repository(t, {
    'src/App/QuokkahubClient.cs': 'public sealed class Client\n{\n    public int PatronId { get; }\n}\n',
    'docs/quokkahub/setup.md': 'Set up the client.\n',
  });

  const result = guard(root);

  assert.equal(result.code, 1);
  assert.deepEqual(result.lines.slice(0, -1), [
    'docs/quokkahub/setup.md (file path): [former-product-name] "quokkahub"',
    'src/App/QuokkahubClient.cs (file path): [former-product-name] "Quokkahub"',
    'src/App/QuokkahubClient.cs:3:16: [tenant-id] "PatronId"',
  ]);
});

test('every match on a line is reported, from every rule', (t) => {
  const result = guard(repository(t, { 'scripts/setup.sh': 'echo quokkahub patron_id QUOKKAHUB\n' }));

  assert.equal(result.code, 1);
  assert.deepEqual(result.lines.slice(0, -1), [
    'scripts/setup.sh:1:6: [former-product-name] "quokkahub"',
    'scripts/setup.sh:1:16: [tenant-id] "patron_id"',
    'scripts/setup.sh:1:26: [former-product-name] "QUOKKAHUB"',
  ]);
});

test('UTF-16 text, UTF-8 with a byte-order mark and binary files are scanned too', (t) => {
  const utf16 = Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from('first\r\nint PatronId;\r\n', 'utf16le')]);
  const utf16BigEndian = Buffer.from(Buffer.concat([Buffer.from([0xfe, 0xff]), Buffer.from('x PatronId', 'utf16le').swap16()]));
  const utf16WithoutMark = Buffer.from('see Quokkahub', 'utf16le');
  const bom = Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from('<root>Quokkahub</root>\n', 'utf8')]);
  const binary = Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x00, 0x00]), Buffer.from('tEXtquokkahub'), Buffer.from([0x00])]);
  const root = repository(t, {
    'src/App/Utf16.cs': utf16,
    'src/App/Utf16BigEndian.cs': utf16BigEndian,
    'src/App/Utf16WithoutMark.txt': utf16WithoutMark,
    'src/App/Settings.xml': bom,
    'src/App/logo.png': binary,
  });

  const result = guard(root);

  assert.equal(result.code, 1);
  assert.deepEqual(result.lines.slice(0, -1), [
    'src/App/logo.png (binary content): [former-product-name] "quokkahub"',
    'src/App/Settings.xml:1:7: [former-product-name] "Quokkahub"',
    'src/App/Utf16.cs:2:5: [tenant-id] "PatronId"',
    'src/App/Utf16BigEndian.cs:1:3: [tenant-id] "PatronId"',
    'src/App/Utf16WithoutMark.txt (binary content): [former-product-name] "Quokkahub"',
  ]);
});

test('only tracked files are scanned, including staged new files', (t) => {
  const root = repository(t, { 'src/App/New.cs': 'int PatronId;\n' });
  fs.writeFileSync(path.join(root, 'notes.txt'), 'quokkahub\n');

  const result = guard(root);

  assert.equal(result.code, 1);
  assert.deepEqual(result.lines.slice(0, -1), ['src/App/New.cs:1:5: [tenant-id] "PatronId"']);
});

test('the snapshot and every unlisted file in the migrations folder are scanned', (t) => {
  const root = repository(t, {
    [`${MIGRATIONS}/AppDbContextModelSnapshot.cs`]: 'modelBuilder.Entity("Quokkahub.Entities.Order", b => { });\n',
    [`${MIGRATIONS}/20200101000000_Second.Designer.cs`]: 'modelBuilder.Entity("Quokkahub.Entities.Order", b => { });\n',
  });

  const result = guard(root);

  assert.equal(result.code, 1);
  assert.deepEqual(result.lines.slice(0, -1), [
    `${MIGRATIONS}/20200101000000_Second.Designer.cs:1:22: [former-product-name] "Quokkahub"`,
    `${MIGRATIONS}/AppDbContextModelSnapshot.cs:1:22: [former-product-name] "Quokkahub"`,
  ]);
});

test('a historical file that disappears without the list being updated fails', (t) => {
  const gone = `${MIGRATIONS}/20200202000000_Gone.Designer.cs`;
  const result = guard(repository(t), { value: config({ approved: [HISTORICAL, gone] }), historical: [HISTORICAL, gone] });

  assert.equal(result.code, 1);
  assert.deepEqual(result.lines.slice(0, -1), [
    `${gone} (historical exception list): is no longer a tracked file; remove it from HISTORICAL_MIGRATIONS in ` +
      'scripts/ci/terminology-guard.js and from docs/product/terminology.md (the list may only shrink)',
  ]);
});

test('an exception the variable does not approve fails, and that file is scanned like any other', (t) => {
  const added = `${MIGRATIONS}/20990101000000_AddThing.Designer.cs`;
  const root = repository(t, { [added]: 'modelBuilder.Entity("Quokkahub.Entities.Thing", b => { });\n' });

  const result = guard(root, { historical: [HISTORICAL, added] });

  assert.equal(result.code, 1);
  assert.deepEqual(result.lines.slice(0, -1), [
    `${added} (historical exception list): is not an approved historical exception; adding one needs a recorded ` +
      `WAS-134 decision and the path in ${VARIABLE}.approvedHistoricalMigrations`,
    `${added}:1:22: [former-product-name] "Quokkahub"`,
  ]);
});

test('a directory, a pattern or a non-migration file is never an exception, even when approved', (t) => {
  const root = repository(t, { 'src/App/Service.cs': 'int PatronId;\n' });
  const entries = [`${MIGRATIONS}/`, `${MIGRATIONS}/*.Designer.cs`, 'src/App/Service.cs', `./${HISTORICAL}`];

  const result = guard(root, { historical: [HISTORICAL, ...entries] });

  assert.equal(result.code, 1);
  const problem = 'is not an exact migration designer path; a historical exception names one file, never a directory or pattern';
  assert.deepEqual(result.lines.slice(0, -1).sort(), [
    `./${HISTORICAL} (historical exception list): ${problem}`,
    `${MIGRATIONS}/ (historical exception list): ${problem}`,
    `${MIGRATIONS}/*.Designer.cs (historical exception list): ${problem}`,
    `src/App/Service.cs (historical exception list): ${problem}`,
    'src/App/Service.cs:1:5: [tenant-id] "PatronId"',
  ].sort());
});

test('a historical file that no longer contains a retired name must leave the list', (t) => {
  const root = repository(t, { [HISTORICAL]: 'modelBuilder.Entity("App.Entities.Order", b => { });\n' });

  const result = guard(root);

  assert.equal(result.code, 1);
  assert.match(result.lines[0], /20200101000000_Initial\.Designer\.cs \(historical exception list\): no longer contains a retired name/);
});

test('a historical file listed twice fails', (t) => {
  const result = guard(repository(t), { historical: [HISTORICAL, HISTORICAL] });

  assert.equal(result.code, 1);
  assert.match(result.lines[0], /20200101000000_Initial\.Designer\.cs \(historical exception list\): is listed twice/);
});

test('a missing, empty or invalid configuration fails before scanning, saying what to fix', async (t) => {
  const root = repository(t);
  const rule = (extra) => config({ rules: [{ id: 'former-product-name', pattern: 'quokkahub', ...extra }] });
  const cases = [
    ['unset', undefined, /is not set or is empty.*gh variable get WASLA_RETIRED_TERMS/s],
    ['empty', '', /is not set or is empty/],
    ['blank', '  \n', /is not set or is empty/],
    ['not JSON', '{"version":1,', /is not valid JSON/],
    ['an array', '[]', /must be a JSON object/],
    ['another version', JSON.stringify({ version: 2, rules: RULES, approvedHistoricalMigrations: [] }), /"version" must be 1, found 2/],
    ['an unknown key', JSON.stringify({ version: 1, rules: RULES, approvedHistoricalMigrations: [], allow: [] }), /unknown key\(s\) "allow"/],
    ['no rules', config({ rules: [] }), /"rules" must be a non-empty array/],
    ['a rule that is not an object', config({ rules: ['quokkahub'] }), /rules\[0\] must be an object/],
    ['a rule with an unknown key', rule({ exclude: 'x' }), /rules\[0\] has unknown key\(s\) "exclude"/],
    ['a rule without an id', config({ rules: [{ pattern: 'quokkahub' }] }), /rules\[0\]\.id must be a lower-case kebab-case name/],
    ['a duplicated rule id', config({ rules: [RULES[0], RULES[0]] }), /rule id "former-product-name" is used twice/],
    ['an empty pattern', rule({ pattern: '' }), /needs a non-empty "pattern"/],
    ['an unsupported flag', rule({ flags: 'g' }), /only "i" and "u" are allowed/],
    ['a repeated flag', rule({ flags: 'ii' }), /only "i" and "u" are allowed/],
    ['an invalid regular expression', rule({ pattern: 'quokka(' }), /is not a valid regular expression/],
    ['a pattern that matches anything', rule({ pattern: 'q*' }), /matches the empty string/],
    ['no approval list', JSON.stringify({ version: 1, rules: RULES }), /"approvedHistoricalMigrations" must be an array/],
    ['a duplicated approval', config({ approved: [HISTORICAL, HISTORICAL] }), /lists a path twice/],
    ['an approved folder', config({ approved: [`${MIGRATIONS}/`] }), /is not an exact migration designer path/],
  ];

  for (const [name, value, message] of cases) {
    await t.test(name, () => {
      const result = guard(root, { value });

      assert.equal(result.code, 2);
      assert.equal(result.out, '', 'nothing is scanned or reported as clean');
      assert.match(result.err, new RegExp(VARIABLE));
      assert.match(result.err, message);
    });
  }
});

test('in GitHub Actions each finding is also an error annotation with escaped values', (t) => {
  const root = repository(t, { 'src/App/a,b.cs': 'int PatronId;\n' });

  const result = guard(root, { actions: true });

  assert.equal(result.code, 1);
  assert.ok(
    result.lines.includes('::error file=src/App/a%2Cb.cs,line=1,col=5,title=Retired name (tenant-id)::"PatronId" in the file content'),
    result.err,
  );
});

test('in GitHub Actions a configuration error is an error annotation', (t) => {
  const result = guard(repository(t), { value: '', actions: true });

  assert.equal(result.code, 2);
  assert.match(result.lines[0], /^::error title=WASLA_RETIRED_TERMS configuration::WASLA_RETIRED_TERMS is not set or is empty/);
});

test('the guard exempts exactly the historical migration files that docs/product/terminology.md lists', () => {
  const repositoryRoot = path.resolve(__dirname, '..', '..');
  const terminology = fs.readFileSync(path.join(repositoryRoot, 'docs', 'product', 'terminology.md'), 'utf8');
  const section = terminology.split('### Historical migration exception')[1].split('\n## ')[0];
  const documented = [...section.matchAll(/^\d+\. `([^`]+)`\s*$/gm)].map((match) => match[1]);

  assert.deepEqual([...HISTORICAL_MIGRATIONS], documented);
  assert.equal(new Set(HISTORICAL_MIGRATIONS).size, 15);
  assert.ok(HISTORICAL_MIGRATIONS.every(isExactDesignerPath));
});
