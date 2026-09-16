// Shared cross-platform launcher; the PowerShell and Bash entrypoints use identical lifecycle/gating.
import { spawn } from 'node:child_process'
import { createWriteStream, existsSync, readFileSync } from 'node:fs'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { createServer } from 'node:net'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { setTimeout as delay } from 'node:timers/promises'

const root = dirname(fileURLToPath(import.meta.url))
const suite = join(root, '.cache-tests')
const pin = 'b55b8bda3dbb8c927c04e85bd8d496a8caa3e4ba'
const options = { framework: 'net10.0', fileSystem: false, update: false, testId: '', originPort: 0, proxyPort: 0 }
const args = process.argv.slice(2)
for (let i = 0; i < args.length; i++) {
  switch (args[i]) {
    case '--framework': options.framework = args[++i]; break
    case '--file-system': options.fileSystem = true; break
    case '--update': options.update = true; break
    case '--test-id': options.testId = args[++i]; break
    case '--origin-port': options.originPort = Number(args[++i]); break
    case '--proxy-port': options.proxyPort = Number(args[++i]); break
    default: throw new Error(`Unknown option: ${args[i]}`)
  }
}
if (!['net10.0', 'netstandard2.0', 'net472'].includes(options.framework)) throw new Error('Invalid --framework.')
if (options.framework === 'net472' && process.platform !== 'win32') throw new Error('net472 conformance requires Windows and the real .NET Framework CLR.')
if (options.update && (options.framework !== 'net10.0' || options.testId)) throw new Error('Only full net10.0 runs may update the common baseline.')
for (const port of [options.originPort, options.proxyPort]) {
  if (!Number.isInteger(port) || port < 0 || port > 65535) throw new Error('Ports must be 0 (automatic) or 1..65535.')
}
if (typeof options.testId !== 'string') throw new Error('--test-id requires a fixture ID.')

const key = `${options.framework}-${options.fileSystem ? 'filesystem' : 'default'}${options.testId ? `-diagnostic-${options.testId.replace(/[^a-zA-Z0-9._-]/g, '_')}` : ''}`
const results = join(root, `results-${key}.json`)
const baseline = join(root, 'expected-results.json')
const running = new Set()
const logs = []
let contentRoot
let interrupted = false

function launch (command, argv, { cwd = root, env = process.env, log, echo = false } = {}) {
  const output = log ? createWriteStream(join(root, `${key}-${log}.log`)) : null
  if (output) logs.push(output)
  const child = spawn(command, argv, { cwd, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] })
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
    // Concrete PID only, including the actual Framework worker descended from the proxy.
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
  if (contentRoot) await rm(contentRoot, { recursive: true, force: true })
}
for (const signal of ['SIGINT', 'SIGTERM']) {
  process.once(signal, () => {
    interrupted = true
    cleanup().then(() => process.exit(130), error => { console.error(error); process.exit(1) })
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

async function waitForHttp (url, child, verify) {
  const deadline = Date.now() + 45_000
  let lastError
  while (Date.now() < deadline) {
    alive(child)
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(2000) })
      // The pinned suite's static-file root returns 404 on Windows; its HTTP test endpoints still work.
      if (!response.ok && (verify || response.status !== 404)) throw new Error(`HTTP ${response.status}`)
      const text = await response.text()
      alive(child)
      if (verify) return verify(text)
      return
    } catch (error) {
      lastError = error
      await delay(250)
    }
  }
  throw new Error(`Timed out waiting for owned process at ${url}: ${lastError}`)
}

try {
  await rm(results, { force: true })
  if (!existsSync(join(suite, '.git'))) await run('git', ['clone', '--quiet', 'https://github.com/http-tests/cache-tests.git', suite], { echo: true })
  // Avoid an index write when the pinned checkout is already ready (parallel cells share this directory).
  if (await run('git', ['-C', suite, 'rev-parse', 'HEAD'], { capture: true }) !== pin)
    await run('git', ['-C', suite, 'checkout', '--quiet', pin], { echo: true })
  await run('git', ['-C', suite, 'diff', '--exit-code', pin, '--', 'tests', 'test-engine', 'spec', 'package.json'], { echo: true })
  if (!existsSync(join(suite, 'node_modules'))) {
    if (process.platform === 'win32') {
      await run(process.env.ComSpec ?? 'cmd.exe', ['/d', '/s', '/c', 'npm install --no-audit --no-fund --silent'], { cwd: suite, log: 'install', echo: true })
    } else {
      await run('npm', ['install', '--no-audit', '--no-fund', '--silent'], { cwd: suite, log: 'install', echo: true })
    }
  }

  const proxyName = options.framework === 'netstandard2.0' ? 'StandardConformanceProxy' : 'ConformanceProxy'
  const proxyProject = join(root, proxyName, `${proxyName}.csproj`)
  if (options.framework === 'net472') {
    await run('dotnet', ['build', join(root, 'FrameworkWorker', 'FrameworkWorker.csproj'), '-c', 'Release', '-v', 'q', '--nologo'], { log: 'worker-build', echo: true })
  }
  await run('dotnet', ['build', proxyProject, '-c', 'Release', '-v', 'q', '--nologo'], { log: 'proxy-build', echo: true })

  const originPort = await freePort(options.originPort)
  let proxyPort = await freePort(options.proxyPort)
  if (proxyPort === originPort) {
    if (options.proxyPort) throw new Error('Origin and proxy ports must differ.')
    do { proxyPort = await freePort(0) } while (proxyPort === originPort)
  }
  contentRoot = await mkdtemp(join(root, '.content-store-'))
  const pidFile = join(contentRoot, 'origin.pid')
  const pkg = JSON.parse(readFileSync(join(suite, 'package.json'), 'utf8'))
  const suiteEnv = { ...process.env }
  for (const [name, value] of Object.entries(pkg.config)) suiteEnv[`npm_package_config_${name}`] = String(value)
  Object.assign(suiteEnv, {
    npm_config_protocol: 'http', npm_config_port: String(originPort),
    npm_config_pidfile: pidFile, npm_config_base: `http://127.0.0.1:${proxyPort}`, npm_config_id: options.testId
  })
  // Run the pinned npm scripts' Node entrypoints directly with npm's config environment.
  // This avoids shell quoting issues and leaves no npm/shell process between us and the owned servers.
  const origin = launch(process.execPath, ['test-engine/server/server.mjs'], { cwd: suite, env: suiteEnv, log: 'origin' })
  await waitForHttp(`http://127.0.0.1:${originPort}/`, origin)
  if (readFileSync(pidFile, 'utf8') !== String(origin.pid)) throw new Error('Origin readiness PID mismatch.')

  const proxy = launch('dotnet', [
    join(root, proxyName, 'bin', 'Release', 'net10.0', `${proxyName}.dll`),
    '--framework', options.framework, '--port', String(proxyPort), '--origin', `http://127.0.0.1:${originPort}`,
    '--file-system', String(options.fileSystem), '--content-root', contentRoot,
    '--worker', join(root, 'FrameworkWorker', 'bin', 'Release', 'net472', 'FrameworkWorker.exe')
  ], { log: 'proxy' })
  const healthUrl = `http://127.0.0.1:${proxyPort}/proxy-health`
  const verifyHealth = text => {
    const report = JSON.parse(text)
    if (report.framework !== options.framework || report.processId !== proxy.pid || report.ready !== true)
      throw new Error(`Unexpected proxy provenance: ${text}`)
    return report
  }
  const provenance = await waitForHttp(healthUrl, proxy, verifyHealth)
  await writeFile(join(root, `${key}-provenance.json`), JSON.stringify(provenance, null, 2) + '\n')
  console.log(provenance.provenance)
  console.log(`Running ${key}: origin PID ${origin.pid} :${originPort}, proxy PID ${proxy.pid} :${proxyPort}`)

  const client = launch(process.execPath, ['--no-warnings', 'test-engine/cli.mjs'], { cwd: suite, env: suiteEnv, log: 'client' })
  const chunks = []
  client.stdout.on('data', chunk => { chunks.push(chunk); if (options.testId) process.stdout.write(chunk) })
  const clientStatus = await Promise.race([
    client.done,
    delay(20 * 60 * 1000, null, { ref: false }).then(() => { throw new Error('Suite exceeded its 20-minute deadline.') })
  ])
  if (clientStatus.error || clientStatus.code !== 0) throw new Error(`Suite client failed: ${JSON.stringify(clientStatus)}`)
  alive(origin)
  alive(proxy)
  await waitForHttp(healthUrl, proxy, verifyHealth)
  if (!options.testId) {
    const output = Buffer.concat(chunks).toString('utf8')
    JSON.parse(output)
    await writeFile(results, output)
    await run(process.execPath, [join(root, 'compare-results.mjs'), results, ...(options.update ? ['--update'] : []), baseline, '--framework', options.framework], { log: 'comparison', echo: true })
  }
} catch (error) {
  console.error(error)
  process.exitCode = 1
} finally {
  await cleanup()
}
