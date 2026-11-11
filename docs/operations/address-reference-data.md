# Address reference data

CentralDb holds Türkiye's address reference data: the country, the 81 cities with their districts, and (WAS-214) the neighbourhoods and streets the signup page suggests. The page suggests only; a visitor can always type a neighbourhood or street that is not in the list, and the server validates the save as before (see [../architecture/signup-registration.md](../architecture/signup-registration.md)).

## Command

```bash
wasla-cli -- seed-address-reference-data                                         # country, cities, districts
wasla-cli -- seed-address-reference-data --source <data/jsonl> --dry-run --report dry-run.json
wasla-cli -- seed-address-reference-data --source <data/jsonl> --report import.json
```

- Without `--source` the command seeds the country, cities and districts from the embedded `turkey-reference-data.json`, as it always did, and says that no neighbourhood or street was imported. Before WAS-214 it looked for two embedded neighbourhood and street files that never existed and reported success while importing nothing.
- `--source` adds the neighbourhood and street catalog. Every file is first checked against the embedded manifest `src/Wasla.Infrastructure/ReferenceData/turkey-address-catalog-source.json` (byte size and SHA-256 of 245 files). Any missing or different file stops the command before anything is read or written.
- `--dry-run` validates and plans against CentralDb as it is and writes nothing at all, not even the cities and districts. On an empty CentralDb seed the cities and districts first, or the dry run reports every district as missing.
- `--report` writes the full JSON report: source, CentralDb collation, counts per level and per district, and every collision, rejected record, conflict and parent that was not imported (up to 5,000 entries per list; the counts are always exact).

## Source and provenance

| | |
| --- | --- |
| Dataset | `data/jsonl` of [onurusluca/turkey-geo-api](https://github.com/onurusluca/turkey-geo-api) |
| Pinned commit | `e44ca84316a8b3d6ff3d9b898b806b1c3d853449` (2026-10-08) |
| Built | `meta.json` `built_at_utc` 2026-10-08T08:31:53.340Z |
| Upstream named in `meta.json` | [melihozkara/il-ilce-mahalle-sokak-veritabani](https://github.com/melihozkara/il-ilce-mahalle-sokak-veritabani) `data/`, 06.10.2026 (that repository's commit `4870a6f62d3a`, "Veri güncelleme (Ekim 2026)", has the same totals) |
| Declared totals | 81 provinces, 973 districts, 73,398 neighbourhoods, 1,276,922 streets (also 390 towns and 18,171 villages, which are not imported) |
| Files read | `meta.json`, `provinces.jsonl`, and `province-{plate}/districts.jsonl`, `neighborhoods.jsonl`, `streets.jsonl` |

The importer reads no other file and runs nothing from either repository.

### Licence evidence and open question

- turkey-geo-api has an MIT `LICENSE` (copyright 2025 Onur Usluca) for "the Software and associated documentation files". Its README names its sources as NVİ's public address inquiry (adres.nvi.gov.tr), TÜİK and Harita Genel Müdürlüğü.
- The upstream repository has no licence file, and GitHub detects none. Its README says the data was collected from NVİ's address inquiry page, using a reCAPTCHA solving service and proxies. It invites readers to use the data or the code in their own projects, asking only for a star.
- **Not established:** the rights to the underlying NVİ address data, and whether NVİ's terms allow this collection and reuse. The MIT licence of the API repository does not settle the rights to data it republishes. This is for the owner to decide; nobody was contacted and no terms were accepted.
- The identifiers are those of the dataset. The upstream README says they are NVİ's `kimlikNo` values. That has not been checked independently, and Wasla does not present them as official codes.

## Acquisition

Fetch exactly the pinned commit. `core.autocrlf=false` keeps the bytes the checksums were taken from.

```bash
git init turkey-geo-api
git -C turkey-geo-api remote add origin https://github.com/onurusluca/turkey-geo-api.git
git -C turkey-geo-api fetch --depth 1 origin e44ca84316a8b3d6ff3d9b898b806b1c3d853449
git -C turkey-geo-api -c core.autocrlf=false checkout FETCH_HEAD
# --source turkey-geo-api/data/jsonl
```

Do not regenerate the data with the upstream collection scripts, scrape NVİ, solve CAPTCHAs or use proxies. A newer snapshot needs a new pinned commit and a new manifest, reviewed like code.

## What is imported, and how

The plan for each province is made in memory against that city's existing rows: one query each for the city, its districts, neighbourhoods and streets. It is written in batches of 1,000 rows, each batch one `SaveChanges` and one transaction, and the change tracker is cleared after every batch. The largest province (Istanbul, about 91,000 streets) bounds the memory, plus one set of all source ids (about 1.35 million numbers) used to find duplicates.

**Matching to CentralDb.**
- **City:** by plate code, and the name must agree under Turkish case rules.
- **District:** by name under Turkish case rules (`Kadıköy` = `KADIKÖY`). All 81 cities and 973 districts of the pinned source match the embedded list.
- **ExternalCode:** the source id is stored as `ExternalCode`. It never replaces a Wasla id.

**Names.**
- The source spelling and case (upper case) are kept.
- Whitespace runs, tabs and line breaks become one space, and the ends are trimmed. SQL Server ignores trailing spaces when it compares names, and 2,765 source names have stray spaces.
- Text is stored in Unicode NFC.

**Neighbourhood names.** A neighbourhood is stored under its source name, except in two cases where it takes its source `full_official_name`:
- The source has no name: these are the village records (`neighborhood_type_code` 0), for example `AKDURAK KÖYÜ`.
- Another neighbourhood of the same district has a name a collation could find equal. Then every member of that group takes its official name, which names its town or village, for example `KESMETEPE BELDESİ, FATİH MAHALLESİ` and `ŞAMBAYAT BELDESİ, FATİH MAHALLESİ`.

Names still shared after that are not imported (see below).

**Street types.** `type_code` maps to the source's own labels, stored as `StreetType` as written: 0 `Köy Sokağı`, 1 `Meydan`, 2 `Bulvar`, 3 `Cadde`, 4 `Sokak`, 5 `Küme Evler`. Every street's official name must end with its label in parentheses, so each record's code is checked against its own name. An unknown code or a disagreement rejects the street. The signup page shows a street as its name followed by `StreetType` (for example `ATATÜRK Cadde`); how the type should read is a display question for later, and needs no data change.

**Collisions.** Two records of one parent are a collision when their names (and, for streets, types) are equal after Turkish upper case, with İ, I, ı and i as one letter and without accents. That is the coarsest equality any case- and accent-insensitive SQL Server collation could apply, so no CentralDb collation can refuse a row the plan accepted. Records in a collision group are **all quarantined**: none is imported, and the report lists each group with its source ids and names.
- No record is chosen over another.
- No source id is merged into another record or overwritten.
- No `ExternalCode` holds two ids.

A quarantined neighbourhood's streets are not imported either.

**Rejected records.** A record is rejected when:
- it is not valid JSON, or has no id;
- its id appears more than once in the source;
- its parent is not in the source, or its district or province differs from its parent's;
- its name is empty, has no letter or digit, contains `?` (text damaged at the source, for example `?S?MS?Z`), a control or invisible character, or a letter outside the Latin script, or is longer than the column (150 for neighbourhoods, 200 for streets);
- its street type is unknown or disagrees with its official name.

**Existing rows** are never deleted, deactivated, renamed or re-keyed, and their `SortOrder` and `IsActive` stay as they are.
- A row that already carries the source id is unchanged.
- A row with the same name under Turkish case rules and no `ExternalCode` gets the source id (`Fill`).
- A row with the same name and another `ExternalCode`, a name that differs only in accents or I letters, or a source id on a row of another parent is a **conflict**. That row is left alone, the source record is not imported, and neither are its streets.
- Streets without a `StreetType`, as entered by hand, are never matched.

Stored registration references keep pointing at the same rows.

**New rows** are active and get `SortOrder` from their position in Turkish alphabetical order within their parent.

**Repeating and resuming.**
- A repeated import finds every row by its `ExternalCode` and writes nothing.
- A stopped import leaves only whole batches. The next run finds them and continues with the rest; there is no separate resume state.
- Two imports at the same time cannot create duplicates: the unique name indexes refuse the second writer's batch, and that run fails and can be repeated. Run one at a time.

## Expected coverage of the pinned source

An independent offline script (Python, run on the pinned files during WAS-214) applied the rules above and gave these counts. A dry run on a CentralDb that has only the seeded cities and districts should report the same.

| Level | Source | Imported | Quarantined (groups) | Rejected | Parent not imported |
| --- | --- | --- | --- | --- | --- |
| Neighbourhoods | 73,398 | 73,390 | 8 (4) | 0 | 0 |
| Streets | 1,276,922 | 1,275,850 | 977 (473) | 81 | 14 |

- Of the imported neighbourhoods, 52,746 keep their source name. 12,293 village records take their official name (the source has none), and 8,351 take it because another neighbourhood of their district has the same name.
- The 4 neighbourhood groups that stay quarantined have identical official names, for example two `OSMANİYE KÖYÜ, YAHYALI MEVKİİ` records in Merkez.
- Sakarya has 6 street collision groups: the 4 listed in WAS-214 plus `FEVZIYE/30`/`FEVZİYE/30` in Pamukova and `BARIŞ`/`BARİŞ` in Söğütlü.
- The 81 rejected streets are 67 names with no letter or digit (`-`, `.`, `--`), 12 empty names, and 2 names with `?`.
- About 860 imported street names look like placeholders (`İSİMSİZ …`, `ADSIZ`, `TANIMSIZ`, `SAHADA BULUNAMAYANLAR !!!`). They are kept as the source has them.
- Istanbul/Kadıköy (21 neighbourhoods, 1,377 streets) and Sakarya/Akyazı (76 neighbourhoods, 2,981 streets) import completely.

These are structural checks of the dataset, not proof that it matches NVİ today.

## Verification

- **Unit tests** in `tests/Wasla.UnitTests/ReferenceData`, on invented records:
  - name rules (`AddressCatalogTextTests`), the manifest and reader (`AddressCatalogSourceTests`), and every planning outcome (`AddressCatalogPlannerTests`);
  - the import on SQLite (`AddressCatalogImporterTests`): repeat, dry run, a stop between batches followed by a resume, existing rows, and a missing or changed source through the command's entry point leaving all five reference tables unchanged;
  - the same on SQL Server (`AddressCatalogImporterSqlServerTests`): Turkish text, the collation, and a registration's foreign keys to existing rows.
- **Real source:** `.github/workflows/address-reference-import.yml` runs on a disposable LocalDB on the CI runner for pull requests that change the importer or the address tables' EF configurations, and can be started by hand. It fetches the pinned source, migrates a new CentralDb, seeds the cities and districts, and adds one hand-made neighbourhood (`Caferağa` in Kadıköy). Then it runs the dry run, kills an import part-way, resumes it, and repeats it. `scripts/ci/check-address-catalog-import.ps1` checks:
  - the dry run wrote nothing;
  - the reports agree with each other and the repeat changed nothing;
  - CentralDb holds no duplicate source ids;
  - the hand-made row kept its id and name;
  - every Kadıköy and Akyazı record matches the source files.

  The reports are uploaded as the `address-catalog-reports` artifact.

## Import into a target CentralDb

Run this only when the owner has decided the licence question above and approved the target. It never runs from a deployment script.

1. Back up CentralDb first. On the VDS: `sudo wasla-backup <label>` ([linux-vds-runbook.md](linux-vds-runbook.md)). A backup is the way back; the importer deletes nothing and offers no undo.
2. Fetch the pinned source on the server ([Acquisition](#acquisition)), outside the release folders.
3. Dry run with a report:

   ```bash
   sudo wasla-cli -- seed-address-reference-data --source <path>/data/jsonl --dry-run --report /tmp/address-dry-run.json
   ```

   Compare the counts with [Expected coverage](#expected-coverage-of-the-pinned-source). Any `Conflict` or `Fill` comes from rows that existed before. Read them in the report before going on.
4. Import with a report:

   ```bash
   sudo wasla-cli -- seed-address-reference-data --source <path>/data/jsonl --report /tmp/address-import.json
   ```

   If it stops (connection, timeout, a killed process), run the same command again.
5. Run it once more. The report must show no insert and no fill.
6. Open the signup page and check the suggestions for a district such as Istanbul/Kadıköy.
