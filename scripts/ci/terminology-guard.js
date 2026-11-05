#!/usr/bin/env node
'use strict';

// Repository-wide guard for the clean-break terminology policy (docs/product/terminology.md).
//
// It fails when a retired name appears in any tracked file's content or path, outside the exact historical migration
// files listed below. The retired-term patterns are not in the repository: they are read from the WASLA_RETIRED_TERMS
// environment variable, which CI fills from the GitHub Actions repository variable of the same name. Usage and the
// variable's format: docs/operations/testing.md.
//
// Every match is classified on its own, by its text only: a file's name never hides its content, and other words on
// the same line never hide a match.

const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');

const VARIABLE = 'WASLA_RETIRED_TERMS';
const CONFIG_VERSION = 1;
const CONFIG_KEYS = ['version', 'rules', 'approvedHistoricalMigrations'];
const RULE_KEYS = ['id', 'pattern', 'flags'];
const ALLOWED_FLAGS = new Set(['i', 'u']);
const THIS_FILE = 'scripts/ci/terminology-guard.js';

// The historical migration exception from docs/product/terminology.md: the only files whose content is not scanned.
// Exact paths only. The list may only shrink; a path is added only after the repository variable approves it.
const HISTORICAL_MIGRATIONS = Object.freeze([
  'src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20260614190906_InitialTenantSchema.Designer.cs',
  'src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20260623215553_AddPrintJobInstallationOwnership.Designer.cs',
  'src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20260623220646_AddTenantPasswordResetTokens.Designer.cs',
  'src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20260925124839_AddOrderCustomerNote.Designer.cs',
  'src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20260926100229_EnforceOnePlatformConnectionPerPlatform.Designer.cs',
  'src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20260928060853_AddGuidedDemoSessionSingleOpenIndex.Designer.cs',
  'src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20260928113614_AddUserGuidedSetupStates.Designer.cs',
  'src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20261001233557_AddTenantOperationalMode.Designer.cs',
  'src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20261006092124_AddAppUserLastLoginAt.Designer.cs',
  'src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20261008093450_AddAppUserSecurityStamp.Designer.cs',
  'src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20261013072101_AddPrintJobStatusVersion.Designer.cs',
  'src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20261013112832_AddOrderChangeVersion.Designer.cs',
  'src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20261014155220_AddOrderItemSequence.Designer.cs',
  'src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20261017060726_AddOrderTimeSemantics.Designer.cs',
  'src/Wasla.Infrastructure/Persistence/Tenant/Migrations/20261018073348_AddRestaurantLogo.Designer.cs',
]);

const EXPECTED_SHAPE =
  '{"version":1,"rules":[{"id":"<name>","pattern":"<JavaScript regular expression>","flags":"<\'\', \'i\', \'u\' or \'iu\'>"}],' +
  '"approvedHistoricalMigrations":["<exact path>"]}';

class ConfigError extends Error {}

function configError(message) {
  return new ConfigError(
    `${VARIABLE} is invalid: ${message}\nExpected JSON of the form ${EXPECTED_SHAPE}. ` +
      'The current value is recorded in WAS-144; see docs/operations/testing.md.',
  );
}

function isPlainObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function rejectUnknownKeys(object, allowed, where) {
  const unknown = Object.keys(object).filter((key) => !allowed.includes(key));
  if (unknown.length > 0) {
    throw configError(`${where} has unknown key(s) ${unknown.map((key) => JSON.stringify(key)).join(', ')}.`);
  }
}

// Parses and validates the variable's value. Throws ConfigError with an actionable message; never returns a
// configuration that scans nothing.
function parseConfig(raw) {
  if (typeof raw !== 'string' || raw.trim() === '') {
    throw new ConfigError(
      `${VARIABLE} is not set or is empty, so the guard cannot check anything.\n` +
        `In CI it comes from the GitHub Actions repository variable ${VARIABLE} ` +
        '(Settings > Secrets and variables > Actions > Variables); its value is recorded in WAS-144. ' +
        `Locally, load it first, for example in PowerShell: $env:${VARIABLE} = gh variable get ${VARIABLE}`,
    );
  }

  let parsed;
  try {
    parsed = JSON.parse(raw);
  } catch (error) {
    throw configError(`it is not valid JSON (${error.message}).`);
  }

  if (!isPlainObject(parsed)) throw configError('the value must be a JSON object.');
  rejectUnknownKeys(parsed, CONFIG_KEYS, 'The object');
  if (parsed.version !== CONFIG_VERSION) {
    throw configError(`"version" must be ${CONFIG_VERSION}, found ${JSON.stringify(parsed.version)}.`);
  }
  if (!Array.isArray(parsed.rules) || parsed.rules.length === 0) {
    throw configError('"rules" must be a non-empty array.');
  }

  const ids = new Set();
  const rules = parsed.rules.map((rule, index) => {
    const where = `rules[${index}]`;
    if (!isPlainObject(rule)) throw configError(`${where} must be an object.`);
    rejectUnknownKeys(rule, RULE_KEYS, where);
    if (typeof rule.id !== 'string' || !/^[a-z0-9]+(-[a-z0-9]+)*$/.test(rule.id)) {
      throw configError(`${where}.id must be a lower-case kebab-case name.`);
    }
    if (ids.has(rule.id)) throw configError(`rule id "${rule.id}" is used twice.`);
    ids.add(rule.id);
    if (typeof rule.pattern !== 'string' || rule.pattern === '') {
      throw configError(`rule "${rule.id}" needs a non-empty "pattern".`);
    }
    const flags = rule.flags === undefined ? '' : rule.flags;
    if (typeof flags !== 'string' || [...flags].some((flag) => !ALLOWED_FLAGS.has(flag)) || new Set(flags).size !== flags.length) {
      throw configError(`rule "${rule.id}" has flags ${JSON.stringify(rule.flags)}; only "i" and "u" are allowed, each once.`);
    }
    let regex;
    try {
      regex = new RegExp(rule.pattern, `${flags}g`);
    } catch (error) {
      throw configError(`rule "${rule.id}" is not a valid regular expression (${error.message}).`);
    }
    if (regex.test('')) {
      throw configError(`rule "${rule.id}" matches the empty string, so it would match every line.`);
    }
    regex.lastIndex = 0;
    return { id: rule.id, regex };
  });

  const approved = parsed.approvedHistoricalMigrations;
  if (!Array.isArray(approved) || approved.some((entry) => typeof entry !== 'string' || entry === '')) {
    throw configError('"approvedHistoricalMigrations" must be an array of exact paths (it may be empty).');
  }
  if (new Set(approved).size !== approved.length) {
    throw configError('"approvedHistoricalMigrations" lists a path twice.');
  }
  const inexact = approved.find((entry) => !isExactDesignerPath(entry));
  if (inexact !== undefined) {
    throw configError(`"approvedHistoricalMigrations" entry ${JSON.stringify(inexact)} is not an exact migration designer path.`);
  }

  return { rules, approved: new Set(approved) };
}

// Text views of a file to scan. Text is decoded as UTF-8 or, with a byte-order mark, UTF-16. A file with NUL bytes is
// treated as binary and scanned in each single-byte and UTF-16 reading, so an encoding cannot hide a match.
function decode(buffer) {
  if (buffer.length >= 2 && buffer[0] === 0xff && buffer[1] === 0xfe) {
    return { binary: false, texts: [buffer.subarray(2).toString('utf16le')] };
  }
  if (buffer.length >= 2 && buffer[0] === 0xfe && buffer[1] === 0xff) {
    const body = Buffer.from(buffer.subarray(2, 2 + Math.floor((buffer.length - 2) / 2) * 2));
    return { binary: false, texts: [body.swap16().toString('utf16le')] };
  }
  if (buffer.length >= 3 && buffer[0] === 0xef && buffer[1] === 0xbb && buffer[2] === 0xbf) {
    return { binary: false, texts: [buffer.subarray(3).toString('utf8')] };
  }
  if (!buffer.includes(0)) {
    return { binary: false, texts: [buffer.toString('utf8')] };
  }
  const even = buffer.length % 2 === 0 ? buffer : buffer.subarray(0, buffer.length - 1);
  const odd = buffer.subarray(1, 1 + Math.floor((buffer.length - 1) / 2) * 2);
  return { binary: true, texts: [buffer.toString('latin1'), even.toString('utf16le'), odd.toString('utf16le')] };
}

function* matchesIn(text, rules) {
  for (const rule of rules) {
    rule.regex.lastIndex = 0;
    for (const match of text.matchAll(rule.regex)) {
      yield { rule: rule.id, index: match.index, text: match[0] };
    }
  }
}

function contentFindings(file, buffer, rules) {
  const { binary, texts } = decode(buffer);
  const findings = [];
  if (binary) {
    const seen = new Set();
    for (const text of texts) {
      for (const match of matchesIn(text, rules)) {
        const key = `${match.rule}\u0000${match.text.toLowerCase()}`;
        if (seen.has(key)) continue;
        seen.add(key);
        findings.push({ kind: 'content', path: file, line: 0, column: 0, rule: match.rule, text: match.text });
      }
    }
    return findings;
  }
  const lines = texts[0].split(/\r\n|\n|\r/);
  lines.forEach((line, index) => {
    for (const match of matchesIn(line, rules)) {
      findings.push({ kind: 'content', path: file, line: index + 1, column: match.index + 1, rule: match.rule, text: match.text });
    }
  });
  return findings;
}

function pathFindings(file, rules) {
  return [...matchesIn(file, rules)].map((match) => ({ kind: 'path', path: file, rule: match.rule, text: match.text }));
}

function isExactDesignerPath(entry) {
  return (
    !/[*?[\]{}!\\]/.test(entry) &&
    !entry.startsWith('/') &&
    !entry.startsWith(':') &&
    !entry.split('/').some((part) => part === '' || part === '.' || part === '..') &&
    /\/Migrations\/\d{14}_[A-Za-z0-9]+\.Designer\.cs$/.test(entry)
  );
}

// Checks the historical exception list against the approval in the variable, the tracked files and their content.
// Returns the findings and the files actually exempt: only an exact, approved, tracked path skips the content scan.
function checkHistoricalList(historical, approved, tracked, readFile, rules) {
  const findings = [];
  const exempt = new Set();
  const listed = new Set();
  const remedy = `remove it from HISTORICAL_MIGRATIONS in ${THIS_FILE} and from docs/product/terminology.md (the list may only shrink)`;
  for (const entry of historical) {
    if (listed.has(entry)) {
      findings.push({ kind: 'exception', path: entry, problem: 'is listed twice as a historical exception' });
      continue;
    }
    listed.add(entry);
    if (!isExactDesignerPath(entry)) {
      findings.push({
        kind: 'exception',
        path: entry,
        problem: 'is not an exact migration designer path; a historical exception names one file, never a directory or pattern',
      });
      continue;
    }
    if (!approved.has(entry)) {
      findings.push({
        kind: 'exception',
        path: entry,
        problem: `is not an approved historical exception; adding one needs a recorded WAS-134 decision and the path in ${VARIABLE}.approvedHistoricalMigrations`,
      });
      continue;
    }
    if (!tracked.has(entry)) {
      findings.push({ kind: 'exception', path: entry, problem: `is no longer a tracked file; ${remedy}` });
      continue;
    }
    exempt.add(entry);
    if (contentFindings(entry, readFile(entry), rules).length === 0) {
      findings.push({ kind: 'exception', path: entry, problem: `no longer contains a retired name, so it needs no exception; ${remedy}` });
    }
  }
  return { findings, exempt };
}

function gitTrackedFiles(root) {
  const output = execFileSync('git', ['-c', 'core.quotePath=false', 'ls-files', '-z', '--cached', '--full-name'], {
    cwd: root,
    maxBuffer: 256 * 1024 * 1024,
  });
  return output.toString('utf8').split('\0').filter(Boolean);
}

function gitTopLevel(directory) {
  return execFileSync('git', ['rev-parse', '--show-toplevel'], { cwd: directory }).toString('utf8').trim();
}

function scan({ root, rules, approved, historical = HISTORICAL_MIGRATIONS }) {
  const files = gitTrackedFiles(root);
  const tracked = new Set(files);
  const readFile = (file) => fs.readFileSync(path.join(root, ...file.split('/')));
  const { findings, exempt } = checkHistoricalList(historical, approved, tracked, readFile, rules);
  let scanned = 0;

  for (const file of files) {
    findings.push(...pathFindings(file, rules));
    if (exempt.has(file)) continue;
    let buffer;
    try {
      buffer = readFile(file);
    } catch (error) {
      // A tracked file deleted from the working tree has no content to scan; Git still lists it until the deletion is
      // staged. A submodule is listed as a directory; its files belong to its own repository. Neither happens in CI.
      if (error.code === 'ENOENT' || error.code === 'EISDIR') continue;
      throw error;
    }
    scanned += 1;
    findings.push(...contentFindings(file, buffer, rules));
  }

  return { files: files.length, scanned, exempt: exempt.size, findings };
}

function escapeData(value) {
  return String(value).replace(/%/g, '%25').replace(/\r/g, '%0D').replace(/\n/g, '%0A');
}

function escapeProperty(value) {
  return escapeData(value).replace(/:/g, '%3A').replace(/,/g, '%2C');
}

function describe(finding) {
  switch (finding.kind) {
    case 'content':
      return finding.line > 0
        ? `${finding.path}:${finding.line}:${finding.column}: [${finding.rule}] ${JSON.stringify(finding.text)}`
        : `${finding.path} (binary content): [${finding.rule}] ${JSON.stringify(finding.text)}`;
    case 'path':
      return `${finding.path} (file path): [${finding.rule}] ${JSON.stringify(finding.text)}`;
    default:
      return `${finding.path} (historical exception list): ${finding.problem}`;
  }
}

function annotation(finding) {
  const properties = [`file=${escapeProperty(finding.path)}`];
  if (finding.kind === 'content' && finding.line > 0) {
    properties.push(`line=${finding.line}`, `col=${finding.column}`);
  }
  const title = finding.kind === 'exception' ? 'Historical migration exception' : `Retired name (${finding.rule})`;
  properties.push(`title=${escapeProperty(title)}`);
  const message =
    finding.kind === 'exception'
      ? finding.problem
      : `${JSON.stringify(finding.text)} in the ${finding.kind === 'path' ? 'file path' : 'file content'}`;
  return `::error ${properties.join(',')}::${escapeData(message)}`;
}

// Runs the guard and returns the process exit code: 0 clean, 1 findings, 2 configuration or Git error.
function run({ cwd = process.cwd(), env = process.env, historical = HISTORICAL_MIGRATIONS, out = console.log, err = console.error } = {}) {
  let config;
  try {
    config = parseConfig(env[VARIABLE]);
  } catch (error) {
    if (!(error instanceof ConfigError)) throw error;
    if (env.GITHUB_ACTIONS === 'true') err(`::error title=${escapeProperty(`${VARIABLE} configuration`)}::${escapeData(error.message)}`);
    err(error.message);
    return 2;
  }

  let result;
  try {
    result = scan({ root: gitTopLevel(cwd), rules: config.rules, approved: config.approved, historical });
  } catch (error) {
    err(`The guard could not list or read the tracked files: ${error.message}`);
    return 2;
  }

  out(
    `Scanned ${result.scanned} of ${result.files} tracked files with ${config.rules.length} retired-name rules; ` +
      `${result.exempt} historical migration file(s) are exempt from the content scan.`,
  );
  if (result.findings.length === 0) {
    out('No retired names found.');
    return 0;
  }

  // Ordinal path order, so the output is the same on every machine and locale.
  const ordinal = (a, b) => (a < b ? -1 : a > b ? 1 : 0);
  const ordered = [...result.findings].sort(
    (a, b) => ordinal(a.path, b.path) || (a.line || 0) - (b.line || 0) || (a.column || 0) - (b.column || 0),
  );
  for (const finding of ordered) err(describe(finding));
  if (env.GITHUB_ACTIONS === 'true') {
    for (const finding of ordered) err(annotation(finding));
  }
  err(
    `${ordered.length} finding(s). Use the canonical names in docs/product/terminology.md; ` +
      'add no alias, compatibility code or exception. Historical-exception problems name the change to make.',
  );
  return 1;
}

module.exports = {
  VARIABLE,
  HISTORICAL_MIGRATIONS,
  ConfigError,
  parseConfig,
  decode,
  contentFindings,
  pathFindings,
  checkHistoricalList,
  isExactDesignerPath,
  scan,
  run,
};

if (require.main === module) {
  process.exitCode = run();
}
