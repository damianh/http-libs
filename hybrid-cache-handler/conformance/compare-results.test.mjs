import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, writeFileSync, rmSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

function compare (t, results, baseline, framework = 'net10.0') {
  const folder = mkdtempSync(join(tmpdir(), 'conformance-compare-'))
  t.after(() => rmSync(folder, { recursive: true, force: true }))
  const resultsPath = join(folder, 'results.json')
  const baselinePath = join(folder, 'baseline.json')
  writeFileSync(resultsPath, JSON.stringify(results))
  writeFileSync(baselinePath, JSON.stringify(baseline))
  return spawnSync(process.execPath, [fileURLToPath(new URL('./compare-results.mjs', import.meta.url)), resultsPath, baselinePath, '--framework', framework], { encoding: 'utf8' })
}

test('complete unchanged baseline succeeds, including known failures', t => {
  assert.equal(compare(t, { a: true, b: ['Assertion', 'random'] }, { a: 'pass', b: 'Assertion' }).status, 0)
})
test('a missing passing fixture fails', t => {
  const result = compare(t, { a: true }, { a: 'pass', b: 'pass' })
  assert.equal(result.status, 1)
  assert.match(result.stdout, /missing from results/)
  assert.doesNotMatch(result.stdout, /No regressions/)
})
test('a missing known failure also fails', t => {
  assert.equal(compare(t, { a: true }, { a: 'pass', b: 'Setup' }).status, 1)
})
test('empty results cannot pass a nonempty baseline', t => {
  assert.equal(compare(t, {}, { a: 'pass' }).status, 1)
})
test('a previously passing fixture fails', t => {
  assert.equal(compare(t, { a: ['Assertion', 'changed'] }, { a: 'pass' }).status, 1)
})
test('new passes remain allowed without changing the baseline', t => {
  assert.equal(compare(t, { a: true }, { a: 'Assertion' }).status, 0)
})

const legacy = JSON.parse(readFileSync(new URL('./legacy-expectations.json', import.meta.url), 'utf8'))
const [legacyId, legacyResult] = Object.entries(legacy.cases)[0]
test('reviewed exact formatting differences apply only to legacy targets', t => {
  for (const framework of legacy.frameworks)
    assert.equal(compare(t, { [legacyId]: legacyResult }, { [legacyId]: 'pass' }, framework).status, 0)
  assert.equal(compare(t, { [legacyId]: legacyResult }, { [legacyId]: 'pass' }).status, 1)
})
test('another failure on an exception fixture is still a regression', t => {
  const changed = ['Setup', legacyResult[1].replace('max-age=10000', 'max-age=9999')]
  assert.equal(compare(t, { [legacyId]: changed }, { [legacyId]: 'pass' }, 'net472').status, 1)
  assert.equal(compare(t, { [legacyId]: ['Assertion', 'Cache reused incorrectly'] }, { [legacyId]: 'pass' }, 'netstandard2.0').status, 1)
})
test('reviewed formatting fixtures are still required in a full result set', t => {
  assert.equal(compare(t, {}, { [legacyId]: 'pass' }, 'netstandard2.0').status, 1)
})
test('Framework-only content formatting assertions cannot weaken Standard or modern checks', t => {
  const id = '304-etag-update-response-Content-Type'
  const results = { [id]: legacy.frameworkCases.net472[id] }
  assert.equal(compare(t, results, { [id]: 'pass' }, 'net472').status, 0)
  assert.equal(compare(t, results, { [id]: 'pass' }, 'netstandard2.0').status, 1)
  assert.equal(compare(t, results, { [id]: 'pass' }).status, 1)
})
