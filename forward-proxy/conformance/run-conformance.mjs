// Cross-platform launcher: pinned HTTP Garden checkout, Garden echo origin, conformance proxy,
// fixture driver, then the exact baseline gate. The PowerShell and Bash entrypoints delegate here.
import { spawn } from 'node:child_process'
import { createWriteStream, existsSync } from 'node:fs'
import { rm } from 'node:fs/promises'
import { connect, createServer } from 'node:net'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { setTimeout as delay } from 'node:timers/promises'

const root = dirname(fileURLToPath(import.meta.url))
const garden = join(root, '.http-garden')
const pin = 'b417e806c1b15e8ea0b9312f81a91fbdcbc7a83e'
const options = { update: false, testId: '', originPort: 0, proxyPort: 0, serve: false, python: process.env.PYTHON ?? (process.platform === 'win32' ? 'python' : 'python3') }
const args = process.argv.slice(2)
for (let i = 0; i < args.length; i++) {
  switch (args[i]) {
    case '--update': options.update = true; break
    case '--test-id': options.testId = args[++i]; break
    case '--origin-port': options.originPort = Number(args[++i]); break
    case '--proxy-port': options.proxyPort = Number(args[++i]); break
    case '--serve': options.serve = true; break
    case '--python': options.python = args[++i]; break
    default: throw new Error(`Unknown option: ${args[i]}`)
  }
}
for (const port of [options.originPort, options.proxyPort]) {
  if (!Number.isInteger(port) || port < 0 || port > 65535) throw new Error('Ports must be 0 (automatic) or 1..65535.')
}
if (typeof options.testId !== 'string') throw new Error('--test-id requires a fixture ID.')
if (options.update && options.testId) throw new Error('Only full runs may update the baseline.')

const key = options.testId ? `diagnostic-${options.testId.replace(/[^a-zA-Z0-9._-]/g, '_')}` : 'full'
const results = join(root, `results-${key}.json`)
const baseline = join(root, 'expected-results.json')
const running = new Set()
const logs = []
let interrupted = false

function launch (command, argv, { cwd = root, log, echo = false } = {}) {
  const output = log ? createWriteStream(join(root, `${key}-${log}.log`)) : null
  if (output) logs.push(output)
  const child = spawn(command, argv, { cwd, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] })
  let error
  child.on('error', ex => { error = ex })
  child.stdout.on('data', chunk => { if (output) output.write(chunk); if (echo) process.stdout.write(chunk) })
  child.stderr.on('data', chunk => { if (output) output.write(chunk); if (echo) process.stderr.write(chunk) })
  child.done = new Promise(resolve => child.on('close', (code, signal) => {
    running.delete(child)
    resolve({ code, signal, error })
  }))
  running.add(child)
  return child
}

async function run (command, argv, config) {
  const child = launch(command, argv, config)
  const chunks = []
  if (config?.capture) child.stdout.on('data', chunk => chunks.push(chunk))
  const status = await child.done
  if (status.error || status.code !== 0) throw new Error(`${command} ${argv.join(' ')} failed: ${status.error ?? status.code ?? status.signal}`)
  return Buffer.concat(chunks).toString('utf8').trim()
}

async function stop (child) {
  if (child.exitCode !== null || child.signalCode !== null) return
  if (process.platform === 'win32') {
    const killer = spawn('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' })
    await new Promise((resolve, reject) => { killer.on('error', reject); killer.on('close', resolve) })
  } else {
    child.kill('SIGTERM')
    await Promise.race([child.done, delay(5000)])
    if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL')
  }
  await child.done
}

async function cleanup () {
  await Promise.all([...running].reverse().map(stop))
  await Promise.all(logs.map(log => new Promise((resolve, reject) => { log.on('error', reject); log.end(resolve) })))
}
for (const signal of ['SIGINT', 'SIGTERM']) {
  process.once(signal, () => {
    interrupted = true
    cleanup().then(() => process.exit(options.serve ? 0 : 130), error => { console.error(error); process.exit(1) })
  })
}

async function freePort (requested) {
  const server = createServer()
  await new Promise((resolve, reject) => {
    server.once('error', reject)
    server.listen(requested, '127.0.0.1', resolve)
  })
  const port = server.address().port
  await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()))
  return port
}

function alive (child) {
  if (interrupted || child.exitCode !== null || child.signalCode !== null || !child.pid)
    throw new Error(`Owned process ${child.pid} exited (code ${child.exitCode}, signal ${child.signalCode}) before conformance completed; inspect ${key}-*.log.`)
}

async function waitFor (probe, child, what) {
  const deadline = Date.now() + 45_000
  let lastError
  while (Date.now() < deadline) {
    alive(child)
    try {
      return await probe()
    } catch (error) {
      lastError = error
      await delay(250)
    }
  }
  throw new Error(`Timed out waiting for ${what}: ${lastError}`)
}

const tcpProbe = port => () => new Promise((resolve, reject) => {
  const socket = connect(port, '127.0.0.1', () => { socket.end(); resolve() })
  socket.once('error', reject)
})

async function proxyProbe (url, pid) {
  const response = await fetch(url, { signal: AbortSignal.timeout(2000) })
  const report = await response.json()
  if (report.ready !== true || report.processId !== pid) throw new Error(`Unexpected proxy health: ${JSON.stringify(report)}`)
}

try {
  await rm(results, { force: true })
  if (!existsSync(join(garden, '.git'))) await run('git', ['clone', '--quiet', 'https://github.com/narfindustries/http-garden.git', garden], { echo: true })
  if (await run('git', ['-C', garden, 'rev-parse', 'HEAD'], { capture: true }) !== pin) {
    await run('git', ['-C', garden, 'fetch', '--quiet', 'origin', pin], { echo: true })
    await run('git', ['-C', garden, 'checkout', '--quiet', pin], { echo: true })
  }
  await run('git', ['-C', garden, 'diff', '--exit-code', pin, '--', 'tools'], { echo: true })

  const proxyProject = join(root, 'ConformanceProxy', 'ConformanceProxy.csproj')
  await run('dotnet', ['build', proxyProject, '-c', 'Release', '-v', 'q', '--nologo'], { log: 'proxy-build', echo: true })

  const originPort = await freePort(options.originPort)
  let proxyPort = await freePort(options.proxyPort)
  if (proxyPort === originPort) {
    if (options.proxyPort) throw new Error('Origin and proxy ports must differ.')
    do { proxyPort = await freePort(0) } while (proxyPort === originPort)
  }

  // -u: the echo server prints each received preamble; unbuffered output keeps the log useful.
  const origin = launch(options.python, ['-u', join(garden, 'tools', 'echo_server.py'), '127.0.0.1', String(originPort)], { log: 'origin' })
  await waitFor(tcpProbe(originPort), origin, `HTTP Garden echo origin on :${originPort}`)

  const proxy = launch('dotnet', [join(root, 'ConformanceProxy', 'bin', 'Release', 'net10.0', 'ConformanceProxy.dll'), '--port', String(proxyPort)], { log: 'proxy' })
  const healthUrl = `http://127.0.0.1:${proxyPort}/proxy-health`
  await waitFor(() => proxyProbe(healthUrl, proxy.pid), proxy, healthUrl)
  console.log(`HTTP Garden ${pin}: echo origin PID ${origin.pid} :${originPort}, forward proxy PID ${proxy.pid} :${proxyPort}`)

  if (options.serve) {
    console.log(`Serving until Ctrl+C. Register the proxy as an HTTP Garden external transducer:\n\ndotnet_forward_proxy:\n  x-props:\n    role: transducer\n    address: 127.0.0.1\n    port: ${proxyPort}\n\nPayloads must use absolute-form, e.g. GET http://127.0.0.1:${originPort}/ HTTP/1.1`)
    await Promise.race([origin.done, proxy.done])
    throw new Error('An owned process exited while serving.')
  }

  await run(options.python, [
    join(root, 'garden_driver.py'), '--garden', garden, '--fixtures', join(root, 'fixtures.json'), '--output', results,
    '--proxy', `127.0.0.1:${proxyPort}`, '--origin', `127.0.0.1:${originPort}`, ...(options.testId ? ['--test-id', options.testId] : [])
  ], { log: 'driver', echo: true })
  alive(origin)
  alive(proxy)
  await proxyProbe(healthUrl, proxy.pid)
  if (!options.testId) {
    await run(process.execPath, [join(root, 'compare-results.mjs'), results, ...(options.update ? ['--update'] : []), baseline], { log: 'comparison', echo: true })
  } else {
    console.log(`Diagnostic result written to ${results}; single-fixture runs do not gate.`)
  }
} catch (error) {
  if (!interrupted) {
    console.error(error)
    process.exitCode = 1
  }
} finally {
  await cleanup()
}
