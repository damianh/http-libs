import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, writeFileSync, rmSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const script = fileURLToPath(new URL('./compare-results.mjs', import.meta.url))
const pass = { outcome: 'pass', reasons: [] }
const fail = { outcome: 'fail', reasons: ['nothing was forwarded'] }

function folder (t) {
  const path = mkdtempSync(join(tmpdir(), 'forward-proxy-compare-'))
  t.after(() => rmSync(path, { recursive: true, force: true }))
  return path
}

function compare (t, results, baseline) {
  const dir = folder(t)
  writeFileSync(join(dir, 'results.json'), JSON.stringify(results))
  writeFileSync(join(dir, 'baseline.json'), JSON.stringify(baseline))
  return spawnSync(process.execPath, [script, join(dir, 'results.json'), join(dir, 'baseline.json')], { encoding: 'utf8' })
}

test('complete unchanged baseline succeeds, including known failures', t => {
  const result = compare(t, { a: pass, b: fail }, { a: 'pass', b: 'fail' })
  assert.equal(result.status, 0)
  assert.match(result.stdout, /Known baseline failures \(1\)/)
})
test('a missing passing fixture fails', t => {
  const result = compare(t, { a: pass }, { a: 'pass', b: 'pass' })
  assert.equal(result.status, 1)
  assert.match(result.stdout, /missing from results/)
  assert.doesNotMatch(result.stdout, /No regressions/)
})
test('a missing known failure also fails', t => {
  assert.equal(compare(t, { a: pass }, { a: 'pass', b: 'fail' }).status, 1)
})
test('empty results cannot pass a nonempty baseline', t => {
  assert.equal(compare(t, {}, { a: 'pass' }).status, 1)
})
test('a previously passing fixture that fails is a regression', t => {
  const result = compare(t, { a: fail }, { a: 'pass' })
  assert.equal(result.status, 1)
  assert.match(result.stdout, /pass -> fail: nothing was forwarded/)
})
test('a driver error on a passing fixture is a regression', t => {
  assert.equal(compare(t, { a: { outcome: 'error', reasons: ['ConnectionRefusedError'] } }, { a: 'pass' }).status, 1)
})
test('new passes remain allowed without changing the baseline', t => {
  const result = compare(t, { a: pass }, { a: 'fail' })
  assert.equal(result.status, 0)
  assert.match(result.stdout, /New passes/)
})
test('fixtures not in the baseline are reported but do not gate', t => {
  const result = compare(t, { a: pass, b: fail }, { a: 'pass' })
  assert.equal(result.status, 0)
  assert.match(result.stdout, /not in baseline \(1\)/)
})
test('--update writes sorted outcomes only', t => {
  const dir = folder(t)
  writeFileSync(join(dir, 'results.json'), JSON.stringify({ b: fail, a: pass }))
  const result = spawnSync(process.execPath, [script, join(dir, 'results.json'), '--update', join(dir, 'baseline.json')], { encoding: 'utf8' })
  assert.equal(result.status, 0)
  assert.equal(readFileSync(join(dir, 'baseline.json'), 'utf8'), '{\n  "a": "pass",\n  "b": "fail"\n}\n')
})
test('the checked-in baseline covers exactly the fixture corpus', () => {
  const fixtures = JSON.parse(readFileSync(new URL('./fixtures.json', import.meta.url), 'utf8')).fixtures
  const baseline = JSON.parse(readFileSync(new URL('./expected-results.json', import.meta.url), 'utf8'))
  const ids = fixtures.map(f => f.id)
  assert.equal(new Set(ids).size, ids.length, 'fixture ids must be unique')
  assert.deepEqual([...ids].sort(), Object.keys(baseline).sort())
  for (const f of fixtures) {
    assert.ok(['forward', 'reject', 'not-forwarded', 'reject-or-forward'].includes(f.expect), f.id)
    assert.ok(f.reference && f.description, f.id)
  }
})
