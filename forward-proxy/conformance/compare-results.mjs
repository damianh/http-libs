/* Compares a forward-proxy conformance results file against expected-results.json.
 *
 * The baseline stores only the outcome ("pass", "fail" or "error") per fixture id;
 * observations contain per-run values such as traceparent and ports.
 *
 * Usage:
 *   node compare-results.mjs <results.json> <expected-results.json>          # gate
 *   node compare-results.mjs <results.json> --update <expected-results.json> # (re)write baseline
 *
 * Exit codes: 0 = no regressions, 1 = regressions or missing fixtures, 2 = usage/IO error.
 */
import { readFileSync, writeFileSync } from 'node:fs'

function outcomeOf (value) {
  return typeof value?.outcome === 'string' ? value.outcome : `unknown:${JSON.stringify(value)}`
}

function compare (results, baseline) {
  const actual = Object.fromEntries(Object.entries(results).map(([id, value]) => [id, outcomeOf(value)]))
  const regressions = []
  const newPasses = []
  const changed = []
  const missing = []
  for (const [id, expected] of Object.entries(baseline)) {
    const got = actual[id]
    if (got === undefined) missing.push(id)
    else if (got === expected) continue
    else if (expected === 'pass') regressions.push({ id, got, reasons: results[id].reasons ?? [] })
    else if (got === 'pass') newPasses.push({ id, expected })
    else changed.push({ id, expected, got })
  }
  const unknown = Object.keys(actual).filter(id => !Object.hasOwn(baseline, id))
  return { actual, regressions, newPasses, changed, missing, unknown }
}

function main (args) {
  if (args.length < 2) {
    console.error('Usage: node compare-results.mjs <results.json> [--update] <expected-results.json>')
    return 2
  }
  const resultsPath = args[0]
  const update = args.includes('--update')
  const baselinePath = args[args.length - 1]
  const results = JSON.parse(readFileSync(resultsPath, 'utf8'))

  if (update) {
    const sorted = Object.fromEntries(Object.entries(results).map(([id, v]) => [id, outcomeOf(v)]).sort(([a], [b]) => a.localeCompare(b)))
    writeFileSync(baselinePath, JSON.stringify(sorted, null, 2) + '\n')
    const passes = Object.values(sorted).filter(v => v === 'pass').length
    console.log(`Baseline written to ${baselinePath}: ${Object.keys(sorted).length} fixtures, ${passes} passing.`)
    return 0
  }

  const baseline = JSON.parse(readFileSync(baselinePath, 'utf8'))
  const { actual, regressions, newPasses, changed, missing, unknown } = compare(results, baseline)
  const passCount = Object.values(actual).filter(v => v === 'pass').length
  console.log(`Results: ${Object.keys(actual).length} fixtures, ${passCount} passing.`)

  const known = Object.entries(baseline).filter(([id, v]) => v !== 'pass' && actual[id] === v).map(([id]) => id)
  if (known.length > 0) {
    console.log(`\nKnown baseline failures (${known.length}), not gating:`)
    for (const id of known) console.log(`  ${id}: ${(results[id].reasons ?? []).join('; ')}`)
  }
  if (newPasses.length > 0) {
    console.log(`\n🎉 New passes (${newPasses.length}) — consider updating the baseline:`)
    for (const { id, expected } of newPasses) console.log(`  ${id} (was ${expected})`)
  }
  if (changed.length > 0) {
    console.log(`\nℹ️ Failure type changed (${changed.length}), not gating:`)
    for (const { id, expected, got } of changed) console.log(`  ${id}: ${expected} -> ${got}`)
  }
  if (unknown.length > 0) {
    console.log(`\nℹ️ Fixtures not in baseline (${unknown.length}), not gating:`)
    for (const id of unknown) console.log(`  ${id}: ${actual[id]}`)
  }
  let code = 0
  if (missing.length > 0) {
    console.log(`\n⚠️ Baseline fixtures missing from results (${missing.length}):`)
    for (const id of missing) console.log(`  ${id}`)
    console.error('\nIncomplete suite: missing baseline fixtures are gating failures.')
    code = 1
  }
  if (regressions.length > 0) {
    console.log(`\n⛔ Regressions (${regressions.length}):`)
    for (const { id, got, reasons } of regressions) console.log(`  ${id}: pass -> ${got}: ${reasons.join('; ')}`)
    code = 1
  }
  if (code === 0) console.log('\n✅ No regressions against baseline.')
  return code
}

try {
  process.exitCode = main(process.argv.slice(2))
} catch (error) {
  console.error(error)
  process.exitCode = 2
}
